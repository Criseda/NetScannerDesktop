using System;

namespace NetScannerDesktop.Models;

/// <summary>
/// The closing recap ns prints after a scan, e.g. <c>12 hosts up (2.1s)</c>.
/// Kept as text because only the host count is useful for logic; the rest
/// is shown to the user as-is.
/// </summary>
public sealed record ScanSummary(int HostCount, string RawLine, TimeSpan Elapsed);
