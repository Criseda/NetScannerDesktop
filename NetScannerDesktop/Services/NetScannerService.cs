using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetScannerDesktop.Models;

namespace NetScannerDesktop.Services;

/// <summary>Outcome of <c>ns -s &lt;subnet&gt;</c>.</summary>
public sealed record SubnetScanResult(
    List<HostResult> Hosts,
    ScanSummary? Summary,
    string RawLog,
    TimeSpan Elapsed);

/// <summary>Outcome of <c>ns -p &lt;ip&gt; &lt;range&gt;</c>.</summary>
public sealed record PortScanResult(
    List<int> OpenPorts,
    string RawLog,
    TimeSpan Elapsed);

public interface INetScannerService
{
    /// <summary>Where ns.exe will be launched from (or "ns" for PATH).</summary>
    string ResolveExePath();

    Task<string> GetVersionAsync(CancellationToken cancellationToken);

    Task<SubnetScanResult> ScanSubnetAsync(
        string cidr,
        bool usePingFallback,
        IProgress<HostResult>? hostFound,
        IProgress<string>? logLine,
        CancellationToken cancellationToken);

    Task<PortScanResult> ScanPortsAsync(
        string ipAddress,
        int startPort,
        int endPort,
        IProgress<int>? portFound,
        IProgress<string>? logLine,
        CancellationToken cancellationToken,
        int? timeoutMs = null);
}

/// <summary>
/// Runs the ns engine as a child process and streams its output.
/// The engine stays the single source of truth for scanning; this class
/// only launches it, parses lines via <see cref="NetScannerParser"/>, and
/// supports cancellation. One instance is shared by the whole app.
/// </summary>
public sealed class NetScannerService : INetScannerService
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Written), string?> VersionCache = new();

    /// <summary>
    /// Picks the newest compatible engine among the bundled copy (MSBuild
    /// Content / MSIX package) and the per-user self-update. Newest wins so
    /// an app update that bundles a newer engine is never shadowed by an
    /// older self-updated copy. Falls back to <c>ns</c> on PATH (e.g.
    /// <c>zig build</c> devs).
    /// </summary>
    public string ResolveExePath()
    {
        string bundled = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "Assets", "Tools", "ns.exe"));

        string? best = null;
        string? bestVersion = null;
        foreach (string candidate in new[] { bundled, EngineUpdater.UserExePath })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            string? version = ReadInstalledVersion(candidate);
            if (version is not null && !EngineUpdater.IsCompatible(version))
            {
                continue;
            }

            // An unreadable version only wins when nothing else is present.
            if (best is null ||
                (version is not null && (bestVersion is null || EngineUpdater.IsNewerThan(version, bestVersion))))
            {
                best = candidate;
                bestVersion = version;
            }
        }

        return best ?? "ns";
    }

    /// <summary>
    /// Version of an installed ns.exe: its ns.version.txt sidecar when
    /// present (written by fetch-ns.ps1 and EngineUpdater), otherwise one
    /// <c>--version</c> probe cached per file timestamp.
    /// </summary>
    private static string? ReadInstalledVersion(string exePath)
    {
        try
        {
            string sidecar = Path.Combine(Path.GetDirectoryName(exePath)!, EngineUpdater.VersionFileName);
            if (File.Exists(sidecar))
            {
                string text = File.ReadAllText(sidecar).Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }

            return VersionCache.GetOrAdd((exePath, File.GetLastWriteTimeUtc(exePath)),
                key => EngineUpdater.ProbeVersion(key.Path));
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var log = new StringBuilder();
        int exit = await RunEngineAsync(["--version"], line => log.AppendLine(line), line => log.AppendLine(line), cancellationToken);
        string version = log.ToString().Trim();
        if (exit != 0 || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"ns --version failed (exit {exit}): {TrimLogTail(log.ToString())}");
        }

        return version;
    }

    public async Task<SubnetScanResult> ScanSubnetAsync(
        string cidr,
        bool usePingFallback,
        IProgress<HostResult>? hostFound,
        IProgress<string>? logLine,
        CancellationToken cancellationToken)
    {
        var rawLog = new StringBuilder();
        var hosts = new List<HostResult>();
        ScanSummary? summary = null;
        string? engineError = null;
        var started = DateTime.Now;

        string[] arguments = usePingFallback
            ? ["-s", cidr, "--ping"]
            : ["-s", cidr];

        void NoteError(string line)
        {
            if (NetScannerParser.IsEngineError(line))
            {
                engineError ??= line.Trim();
            }
        }

        void OnStdout(string line)
        {
            rawLog.AppendLine(line);
            logLine?.Report(line);
            NoteError(line);

            HostResult? host = NetScannerParser.TryParseOnlineHost(line);
            if (host is not null)
            {
                hosts.Add(host);
                hostFound?.Report(host);
                return;
            }

            summary ??= NetScannerParser.TryParseSummary(line);
        }

        // ns reports filtered ports and diagnostics on stderr; keep them in
        // the log but never treat them as results.
        void OnStderr(string line)
        {
            rawLog.AppendLine(line);
            logLine?.Report(line);
            NoteError(line);
        }

        int exit = await RunEngineAsync(arguments, OnStdout, OnStderr, cancellationToken);
        if (engineError is not null)
        {
            throw new InvalidOperationException(engineError);
        }

        if (exit != 0 && hosts.Count == 0 && summary is null)
        {
            throw new InvalidOperationException($"ns scan failed (exit {exit}): {TrimLogTail(rawLog.ToString())}");
        }

        return new SubnetScanResult(hosts, summary, rawLog.ToString(), DateTime.Now - started);
    }

    public async Task<PortScanResult> ScanPortsAsync(
        string ipAddress,
        int startPort,
        int endPort,
        IProgress<int>? portFound,
        IProgress<string>? logLine,
        CancellationToken cancellationToken,
        int? timeoutMs = null)
    {
        var rawLog = new StringBuilder();
        var openPorts = new List<int>();
        var started = DateTime.Now;

        var arguments = new List<string> { "-p", ipAddress, $"{startPort}-{endPort}" };
        if (timeoutMs.HasValue)
        {
            arguments.Add("--timeout");
            arguments.Add(timeoutMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        string? engineError = null;

        void OnStdout(string line)
        {
            rawLog.AppendLine(line);
            logLine?.Report(line);
            if (NetScannerParser.IsEngineError(line))
            {
                engineError ??= line.Trim();
            }

            foreach (int p in NetScannerParser.TryParseOpenPortsRecap(line))
            {
                if (!openPorts.Contains(p))
                {
                    openPorts.Add(p);
                    portFound?.Report(p);
                }
                continue;
            }

            int? port = NetScannerParser.TryParseOpenPort(line);
            if (port is not null && !openPorts.Contains(port.Value))
            {
                openPorts.Add(port.Value);
                portFound?.Report(port.Value);
            }
        }

        void OnStderr(string line)
        {
            rawLog.AppendLine(line);
            logLine?.Report(line);
            if (NetScannerParser.IsEngineError(line))
            {
                engineError ??= line.Trim();
            }
        }

        int exit = await RunEngineAsync(arguments, OnStdout, OnStderr, cancellationToken);
        if (engineError is not null)
        {
            throw new InvalidOperationException(engineError);
        }

        if (exit != 0 && openPorts.Count == 0 && !NetScannerParser.IsNoOpenPortsReport(rawLog.ToString()))
        {
            throw new InvalidOperationException($"ns scan failed (exit {exit}): {TrimLogTail(rawLog.ToString())}");
        }

        openPorts.Sort();
        return new PortScanResult(openPorts, rawLog.ToString(), DateTime.Now - started);
    }

    /// <summary>
    /// Starts ns with the given tokens and calls back for every output line.
    /// Cancelling kills the child process so scans stop immediately.
    /// Returns the process exit code.
    /// </summary>
    private async Task<int> RunEngineAsync(
        System.Collections.Generic.IReadOnlyList<string> arguments,
        Action<string> onStdoutLine,
        Action<string> onStderrLine,
        CancellationToken cancellationToken)
    {
        string exePath = ResolveExePath();

        if (exePath != "ns" && !File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"ns.exe not found at {exePath}. Run Scripts/fetch-ns.ps1 to download it, " +
                "or install ns on PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string token in arguments)
        {
            startInfo.ArgumentList.Add(token);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stdoutDone.TrySetResult();
            }
            else
            {
                onStdoutLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stderrDone.TrySetResult();
            }
            else
            {
                onStderrLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {exePath}.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"Could not launch '{exePath}'. Run Scripts/fetch-ns.ps1 to download ns.exe.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (cancellationToken.Register(TryKillTree))
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task);
        }

        return process.ExitCode;

        void TryKillTree()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process already exited; nothing to stop.
            }
        }
    }

    private static string TrimLogTail(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return "no output";
        }

        string[] lines = log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int skip = Math.Max(0, lines.Length - 3);
        string tail = string.Join(" | ", lines.Skip(skip).Select(l => l.Trim()));
        return tail.Length > 300 ? tail.Substring(0, 300) + "…" : tail;
    }
}
