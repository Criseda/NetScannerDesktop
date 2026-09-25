using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetScannerDesktop.Services;

/// <summary>One GitHub release, reduced to what the updater needs.</summary>
public sealed record EngineRelease(string Tag, string WindowsZipUrl);

/// <summary>
/// Checks github.com/Criseda/NetScanner for a newer engine and installs it
/// into per-user LocalAppData (the MSIX install folder is read-only, so a
/// self-update can never overwrite the bundled copy — it shadows it).
/// Downloaded binaries are verified by running <c>ns --version</c> and
/// requiring an exact match with the release tag before install.
/// </summary>
public static class EngineUpdater
{
    /// <summary>Bundled engine. Keep in sync with csproj NetScannerVersion.</summary>
    public const string PinnedVersion = "v1.1.0";

    private const string LatestApi = "https://api.github.com/repos/Criseda/NetScanner/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static EngineUpdater()
    {
        // GitHub API rejects requests without a User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("NetScannerDesktop");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Per-user engine location. Checked first by ResolveExePath.</summary>
    public static string UserExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScanner", "Tools", "ns.exe");

    /// <summary>Latest published release, or null when offline / unexpected shape.</summary>
    public static async Task<EngineRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(LatestApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tag) ||
            tag.GetString() is not { } tagName || string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets))
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out JsonElement name) &&
                string.Equals(name.GetString(), "windows.zip", StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out JsonElement url) &&
                url.GetString() is { } downloadUrl && !string.IsNullOrWhiteSpace(downloadUrl))
            {
                return new EngineRelease(tagName.Trim(), downloadUrl);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/> ("v1.2.0" style).</summary>
    public static bool IsNewerThan(string latest, string current) =>
        CompareVersions(latest, current) > 0;

    public static int CompareVersions(string a, string b)
    {
        int[] pa = ParseVersion(a);
        int[] pb = ParseVersion(b);
        for (int i = 0; i < 3; i++)
        {
            int diff = pa[i].CompareTo(pb[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }

    private static int[] ParseVersion(string version)
    {
        var parts = new int[3];
        string[] tokens = version.Trim().TrimStart('v', 'V').Split('.');
        for (int i = 0; i < parts.Length && i < tokens.Length; i++)
        {
            // Tolerate suffixes like "1.1.0-beta" by reading leading digits.
            string digits = string.Empty;
            foreach (char c in tokens[i].Trim())
            {
                if (!char.IsDigit(c))
                {
                    break;
                }

                digits += c;
            }

            int.TryParse(digits, out parts[i]);
        }

        return parts;
    }

    /// <summary>
    /// Downloads the release zip, extracts ns.exe, verifies
    /// <c>--version</c> matches <paramref name="release"/>.Tag, then
    /// installs it per-user. Returns the installed version string.
    /// </summary>
    public static async Task<string> UpdateAsync(
        EngineRelease release,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "netscanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            log?.Report($"Downloading {release.Tag}…");
            string zipPath = Path.Combine(tempDir, "windows.zip");
            using (var response = await Http.GetAsync(release.WindowsZipUrl, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file, cancellationToken);
            }

            log?.Report("Extracting…");
            string extractDir = Path.Combine(tempDir, "out");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            string? downloadedExe = null;
            foreach (string candidate in Directory.EnumerateFiles(extractDir, "ns.exe", SearchOption.AllDirectories))
            {
                downloadedExe = candidate;
                break;
            }

            if (downloadedExe is null)
            {
                throw new InvalidOperationException("Downloaded release did not contain ns.exe.");
            }

            string reported = RunVersion(downloadedExe);
            if (!reported.Trim().Equals(release.Tag.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded engine reports '{reported}' but release is '{release.Tag}'. Aborting.");
            }

            string? destDir = Path.GetDirectoryName(UserExePath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(downloadedExe, UserExePath, overwrite: true);
            log?.Report($"Installed ns {reported}.");
            return reported;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    private static string RunVersion(string exePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not verify the downloaded engine.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output.Trim();
    }
}
