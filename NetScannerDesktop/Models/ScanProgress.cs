using System;

namespace NetScannerDesktop.Models;

/// <summary>
/// One <c>{"type":"progress","phase":...,"done":N,"total":M}</c> event
/// (NetScanner v1.5.0+). Phases arrive in order: <c>ports</c> for
/// <c>-p</c>; <c>sweep</c>, then <c>arp</c> (quiet hosts from the ARP
/// table, TCP sweeps only) and <c>identify</c> (<c>--resolve</c>) for
/// <c>-s</c>. Each phase counts from zero to its own total, so the bar
/// restarts per phase and the label says which one is running.
/// </summary>
public sealed record ScanProgress(string Phase, long Done, long Total)
{
    public const string PortsPhase = "ports";
    public const string SweepPhase = "sweep";
    public const string ArpPhase = "arp";
    public const string IdentifyPhase = "identify";

    /// <summary>0-100, for a ProgressBar.</summary>
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100.0 * Done / Total, 0, 100);

    /// <summary>Whole percent, rounded down so a running phase never reads 100%.</summary>
    public int WholePercent => (int)Math.Floor(Percent);

    /// <summary>
    /// What a status line shows for this phase, e.g. "42%" for a sweep or
    /// "3 of 9" where the counts are small and mean something (hosts).
    /// </summary>
    public string Text => Phase switch
    {
        SweepPhase or PortsPhase => $"{WholePercent}%",
        _ => $"{Done:N0} of {Total:N0}",
    };

    /// <summary>
    /// Time left, extrapolated from the phase so far; null until there is
    /// enough to go on (at least 5% done and 2s in), so it does not jump
    /// around at the start.
    /// </summary>
    public TimeSpan? Remaining(TimeSpan phaseElapsed)
    {
        if (Total <= 0 || Done <= 0 || Done >= Total || Percent < 5 || phaseElapsed < TimeSpan.FromSeconds(2))
        {
            return null;
        }

        return TimeSpan.FromTicks((long)(phaseElapsed.Ticks * (double)(Total - Done) / Done));
    }
}
