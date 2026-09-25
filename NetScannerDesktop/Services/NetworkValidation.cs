using System;
using System.Net;

namespace NetScannerDesktop.Services;

/// <summary>
/// Input checks for the scan forms. Every method returns false plus a
/// message the UI can show directly, so no exception handling in the view.
/// </summary>
public static class NetworkValidation
{
    public static bool IsValidIpv4(string? text) =>
        IPAddress.TryParse(text?.Trim(), out IPAddress? address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    public static bool TryParseCidr(string? text, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a subnet like 192.168.1.0/24.";
            return false;
        }

        string[] parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IsValidIpv4(parts[0]))
        {
            error = "Use CIDR notation, e.g. 192.168.1.0/24.";
            return false;
        }

        if (!int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > 32)
        {
            error = "Prefix length must be between /0 and /32.";
            return false;
        }

        return true;
    }

    public static int PrefixLength(string cidr) =>
        int.Parse(cidr.Trim().Split('/')[1]);

    /// <summary>
    /// How many host addresses ns will probe. Mirrors the engine's
    /// usableHosts: network and broadcast are skipped, except for
    /// /31 and /32 which have no such addresses.
    /// </summary>
    public static long CountUsableHosts(string cidr)
    {
        int prefix = PrefixLength(cidr);
        if (prefix >= 31)
        {
            return 1L << (32 - prefix);
        }

        return (1L << (32 - prefix)) - 2;
    }

    public static bool TryParsePortRange(string? text, out int start, out int end, out string error)
    {
        start = 0;
        end = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a port range like 1-1024.";
            return false;
        }

        string[] parts = text.Trim().Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0].Trim(), out start) ||
            !int.TryParse(parts[1].Trim(), out end))
        {
            error = "Use the form start-end, e.g. 1-1024.";
            return false;
        }

        if (start < 1 || start > 65535 || end < 1 || end > 65535)
        {
            error = "Ports must be between 1 and 65535.";
            return false;
        }

        // The engine accepts either order; normalise the same way.
        if (start > end)
        {
            (start, end) = (end, start);
        }

        return true;
    }

    /// <summary>
    /// Parses the optional per-probe timeout for <c>ns -p ... --timeout</c>.
    /// Empty means "engine default (500ms)". Otherwise 1-60000ms.
    /// </summary>
    public static bool TryParseTimeout(string? text, out int? timeoutMs, out string error)
    {
        timeoutMs = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), out int value))
        {
            error = "Timeout must be a number of milliseconds, e.g. 500.";
            return false;
        }

        if (value < 1 || value > 60000)
        {
            error = "Timeout must be between 1 and 60000 ms.";
            return false;
        }

        timeoutMs = value;
        return true;
    }
}
