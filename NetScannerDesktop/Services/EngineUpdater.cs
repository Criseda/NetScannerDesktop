using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetScannerDesktop.Services;

/// <summary>
/// One GitHub release, reduced to what the updater needs.
/// <see cref="Sha256"/> comes from the asset's <c>digest</c> field; null
/// when GitHub did not publish one, in which case the updater refuses it.
/// </summary>
public sealed record EngineRelease(string Tag, string WindowsZipUrl, string? Sha256);

/// <summary>
/// Checks github.com/Criseda/NetScanner for a newer engine and installs it
/// into per-user LocalAppData (the MSIX install folder is read-only, so a
/// self-update can never overwrite the bundled copy — it sits beside it and
/// <see cref="NetScannerService.ResolveExePath"/> picks the newer one).
/// A download is SHA-256 checked against the release digest before it is
/// ever executed, then must report exactly the release tag via
/// <c>ns --version</c>.
/// </summary>
public static class EngineUpdater
{
    /// <summary>Bundled engine. Keep in sync with csproj NetScannerVersion.</summary>
    public const string PinnedVersion = "v1.1.0";

    /// <summary>Written next to every installed ns.exe so resolving never has to spawn it.</summary>
    public const string VersionFileName = "ns.version.txt";

    private const string LatestApi = "https://api.github.com/repos/Criseda/NetScanner/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly object LatestSync = new();
    private static Task<EngineRelease?>? latestCheck;

    static EngineUpdater()
    {
        // GitHub API rejects requests without a User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("NetScannerDesktop");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Per-user engine location, installed by <see cref="UpdateAsync"/>.</summary>
    public static string UserExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScanner", "Tools", "ns.exe");

    /// <summary>
    /// Latest published release, fetched once per app session and shared
    /// by every caller (unauthenticated GitHub API calls are limited to 60
    /// an hour). Null when offline or the response has an unexpected shape;
    /// a failed check is retried on the next call.
    /// </summary>
    public static Task<EngineRelease?> GetLatestReleaseAsync()
    {
        lock (LatestSync)
        {
            if (latestCheck is null || latestCheck.IsFaulted || (latestCheck.IsCompleted && latestCheck.Result is null))
            {
                latestCheck = FetchLatestReleaseAsync();
            }

            return latestCheck;
        }
    }

    private static async Task<EngineRelease?> FetchLatestReleaseAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestApi);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return ParseRelease(doc.RootElement);
        }
        catch
        {
            // Offline or API hiccup: callers treat null as "unknown".
            return null;
        }
    }

    private static EngineRelease? ParseRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out JsonElement tag) ||
            tag.GetString() is not { } tagName || string.IsNullOrWhiteSpace(tagName) ||
            !root.TryGetProperty("assets", out JsonElement assets))
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
                string? sha256 = null;
                if (asset.TryGetProperty("digest", out JsonElement digest) &&
                    digest.ValueKind == JsonValueKind.String &&
                    digest.GetString() is { } d &&
                    d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = d.Substring("sha256:".Length).Trim().ToLowerInvariant();
                }

                return new EngineRelease(tagName.Trim(), downloadUrl, sha256);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/> ("v1.2.0" style).</summary>
    public static bool IsNewerThan(string latest, string current) =>
        CompareVersions(latest, current) > 0;

    /// <summary>
    /// The parser understands the output of the major version this app was
    /// built against. A new major may change the output format, so it is
    /// never self-installed; it arrives with an app update instead.
    /// </summary>
    public static bool IsCompatible(string version) =>
        ParseVersion(version)[0] == ParseVersion(PinnedVersion)[0];

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
    /// Downloads the release zip, verifies its SHA-256 against the release
    /// digest, extracts ns.exe, checks <c>--version</c> matches the tag, then
    /// installs it per-user. Returns the installed version string.
    /// </summary>
    public static async Task<string> UpdateAsync(
        EngineRelease release,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        if (!IsCompatible(release.Tag))
        {
            throw new InvalidOperationException(
                $"ns {release.Tag} is a new major version. Update NetScannerDesktop to use it.");
        }

        if (string.IsNullOrEmpty(release.Sha256))
        {
            throw new InvalidOperationException(
                $"Release {release.Tag} has no published checksum, so it cannot be verified.");
        }

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

            // Verify before anything from the download is extracted or run.
            string actual;
            await using (var zip = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(zip, cancellationToken)).ToLowerInvariant();
            }

            if (actual != release.Sha256)
            {
                throw new InvalidOperationException(
                    $"Checksum mismatch for {release.Tag} (expected {release.Sha256}, got {actual}). Aborting.");
            }

            log?.Report("Checksum OK. Extracting…");
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

            string reported = ProbeVersion(downloadedExe) ?? string.Empty;
            if (!reported.Equals(release.Tag.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded engine reports '{reported}' but release is '{release.Tag}'. Aborting.");
            }

            string destDir = Path.GetDirectoryName(UserExePath)!;
            Directory.CreateDirectory(destDir);
            File.Copy(downloadedExe, UserExePath, overwrite: true);
            File.WriteAllText(Path.Combine(destDir, VersionFileName), reported);
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

    /// <summary>
    /// Runs <c>ns --version</c> and returns its trimmed output, or null when
    /// the binary fails to start, exits non-zero, or takes over 5 seconds.
    /// </summary>
    internal static string? ProbeVersion(string exePath)
    {
        try
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

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Read asynchronously so a hung binary cannot block past the timeout.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string version = output.Result.Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch
        {
            return null;
        }
    }
}
