using System;

namespace NetScannerDesktop.Services;

/// <summary>
/// How long a port scan can take, to decide whether it is worth a warning.
/// With Auto (no <c>--timeout</c>) the engine learns a short wait from the
/// host, so even all 65,535 ports finish in well under a minute and need no
/// warning. A fixed timeout is waited out in full by every probe that gets no
/// answer, so a large one makes a wide scan slow.
/// </summary>
public static class ScanEstimate
{
    /// <summary>Port probes the engine keeps in flight at once.</summary>
    public const int ParallelProbes = 1024;

    /// <summary>Warn before a port scan that could take longer than this.</summary>
    public static readonly TimeSpan SlowPortScan = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Worst case for a port scan with a fixed timeout, when no port answers:
    /// the ports go out <see cref="ParallelProbes"/> at a time and each waits
    /// the full timeout. Null for Auto.
    /// </summary>
    public static TimeSpan? WorstCasePortScan(int portCount, int? timeoutMs) =>
        timeoutMs is int t && portCount > 0
            ? TimeSpan.FromMilliseconds(Math.Ceiling(portCount / (double)ParallelProbes) * t)
            : null;

    /// <summary>
    /// The question to ask before a port scan that could be slow because of
    /// a fixed timeout, or null when the scan can just start.
    /// </summary>
    public static string? SlowPortScanWarning(int portCount, int? timeoutMs)
    {
        if (WorstCasePortScan(portCount, timeoutMs) is not { } worst || worst <= SlowPortScan)
        {
            return null;
        }

        string ports = portCount == 1 ? "1 port" : $"{portCount:N0} ports";
        return $"With a fixed timeout of {timeoutMs:N0} ms per port, scanning {ports} could take up to {Describe(worst)}. "
            + "Clear the timeout box to use Auto, which learns the wait from the host and is usually much faster. Continue?";
    }

    /// <summary>A rough length for people: "about 2 minutes", "about 1.5 hours".</summary>
    public static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 90)
        {
            int s = Math.Max(1, (int)Math.Round(span.TotalSeconds));
            return s == 1 ? "1 second" : $"{s} seconds";
        }

        if (span.TotalMinutes < 90)
        {
            return $"about {(int)Math.Round(span.TotalMinutes)} minutes";
        }

        return $"about {Math.Round(span.TotalHours, 1):0.#} hours";
    }
}
