using System;
using NetScannerDesktop.Models;

namespace NetScannerDesktop.Services;

/// <summary>
/// Reads the text lines printed by the ns engine and turns them into data.
/// <para>
/// Covered output (NetScanner v1.1.0):
/// <c>Host 192.168.1.10 is online</c>,
/// <c>Host 192.168.1.11 is online (arp)</c>,
/// <c>Open port: 80</c> (streamed hit),
/// <c>Open ports: 80, 443</c> (closing recap),
/// <c>No open ports found</c>,
/// <c>12 hosts up (2.1s)</c>,
/// <c>NetScanner: ...</c> (CLI usage errors).
/// Anything unrecognised returns null so callers can treat it as log text.
/// </para>
/// </summary>
public static class NetScannerParser
{
    private const string HostMarker = " is online";
    private const string ArpSuffix = "(arp)";
    private const string OpenPortMarker = "Open port:";
    private const string OpenPortsRecapMarker = "Open ports:";
    private const string NoOpenPortsMarker = "No open ports found";
    private const string EngineErrorMarker = "NetScanner:";

    public static HostResult? TryParseOnlineHost(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("Host ", StringComparison.Ordinal))
        {
            return null;
        }

        int marker = line.IndexOf(HostMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        int hostWord = line.IndexOf("Host ", StringComparison.Ordinal);
        string ip = line.Substring(hostWord + "Host ".Length, marker - hostWord - "Host ".Length).Trim();

        if (!NetworkValidation.IsValidIpv4(ip))
        {
            return null;
        }

        bool viaArp = line.Contains(ArpSuffix, StringComparison.Ordinal);
        return new HostResult(ip, viaArp, DateTime.Now);
    }

    public static int? TryParseOpenPort(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(OpenPortMarker, StringComparison.Ordinal))
        {
            return null;
        }

        // Must not match the plural recap "Open ports: ..." — that is
        // handled by TryParseOpenPortsRecap.
        string after = trimmed.Substring(OpenPortMarker.Length);
        if (after.TrimStart().StartsWith("s", StringComparison.Ordinal))
        {
            return null;
        }

        string number = after.Trim();
        if (int.TryParse(number, out int port) && port >= 1 && port <= 65535)
        {
            return port;
        }

        return null;
    }

    /// <summary>
    /// Parses the v1.1.0 closing recap, e.g. <c>Open ports: 80, 443</c>.
    /// Returns an empty list for any other line.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<int> TryParseOpenPortsRecap(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return System.Array.Empty<int>();
        }

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(OpenPortsRecapMarker, StringComparison.Ordinal))
        {
            return System.Array.Empty<int>();
        }

        string list = trimmed.Substring(OpenPortsRecapMarker.Length).Trim();
        if (string.IsNullOrEmpty(list))
        {
            return System.Array.Empty<int>();
        }

        var ports = new System.Collections.Generic.List<int>();
        foreach (string part in list.Split(','))
        {
            if (int.TryParse(part.Trim(), out int port) && port >= 1 && port <= 65535)
            {
                if (!ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }

        return ports;
    }

    /// <summary>True when the engine reports no open ports (v1.1.0).</summary>
    public static bool IsNoOpenPortsReport(string text) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains(NoOpenPortsMarker, StringComparison.Ordinal);

    /// <summary>True for CLI usage errors like "NetScanner: Invalid IP address".</summary>
    public static bool IsEngineError(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.TrimStart().StartsWith(EngineErrorMarker, StringComparison.Ordinal);

    /// <summary>
    /// Parses the closing recap line, e.g. <c>12 hosts up (2.1s)</c>.
    /// Returns null for any other line (including the streamed hits).
    /// </summary>
    public static ScanSummary? TryParseSummary(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string trimmed = line.Trim();
        if (!trimmed.EndsWith(")", StringComparison.Ordinal) || !trimmed.Contains(" up (", StringComparison.Ordinal))
        {
            return null;
        }

        // "12 hosts up (2.1s)" -> count part "12 hosts", seconds part "2.1s".
        int upIndex = trimmed.IndexOf(" up (", StringComparison.Ordinal);
        string countPart = trimmed.Substring(0, upIndex).Trim();
        string countDigits = countPart.Split(' ')[0];

        string secondsPart = trimmed.Substring(upIndex + " up (".Length).TrimEnd(')');
        if (secondsPart.EndsWith("s", StringComparison.Ordinal))
        {
            secondsPart = secondsPart.Substring(0, secondsPart.Length - 1);
        }

        if (!int.TryParse(countDigits, out int hostCount))
        {
            return null;
        }

        TimeSpan elapsed = TimeSpan.Zero;
        if (double.TryParse(secondsPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            elapsed = TimeSpan.FromSeconds(seconds);
        }

        return new ScanSummary(hostCount, trimmed, elapsed);
    }

    /// <summary>True when the line is the scan header ns always prints first.</summary>
    public static bool IsScanHeader(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.Contains("Scanning network:", StringComparison.Ordinal);
}
