using System;
using System.Collections.Generic;
using System.Linq;

namespace NetScannerDesktop.Models;

/// <summary>
/// One open TCP port, named by the engine (NetScanner v1.4+): a display
/// name, the service IANA officially registers for the port, a
/// description and a coarse category. Older engines report the number
/// only, and <see cref="HasServiceData"/> tells the two apart, so an
/// unnamed port reads "Unknown" only when the engine actually looked.
/// </summary>
public sealed record PortResult(
    int Port,
    string? Service = null,
    string? Iana = null,
    string? Description = null,
    string? Category = null,
    bool HasServiceData = false)
{
    /// <summary>Service cell text: the name, "Unknown" when the engine knows nothing, blank for old engines.</summary>
    public string ServiceText => Service ?? (HasServiceData ? "Unknown" : string.Empty);

    /// <summary>True when there is a real name to show (the placeholder is styled differently).</summary>
    public bool HasService => Service is not null;

    public bool ShowServicePlaceholder => Service is null && HasServiceData;

    /// <summary>The port is officially registered with IANA for TCP.</summary>
    public bool IsIanaAssigned => Iana is not null;

    /// <summary>The engine looked the port up and IANA assigns nothing there.</summary>
    public bool ShowIanaUnassigned => Iana is null && HasServiceData;

    /// <summary>
    /// Description cell: blank when it only repeats a name already on the
    /// row (IANA registers many ports with their own name as description).
    /// </summary>
    public string? DescriptionText =>
        string.Equals(Description, Service, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Description, Iana, StringComparison.OrdinalIgnoreCase)
            ? null
            : Description;

    public string CategoryLabel => LabelFor(Category);

    public bool HasCategory => CategoryLabel.Length > 0;

    /// <summary>"22 — SSH" for screen readers and copy.</summary>
    public string Display => Service is null ? Port.ToString() : $"{Port} — {Service}";

    /// <summary>Short form for summaries: "22 SSH".</summary>
    public string ShortText => Service is null ? Port.ToString() : $"{Port} {Service}";

    public bool IsWebPort => Category == "web";

    /// <summary>Scheme for "Open in browser": HTTPS when the names say so, HTTP otherwise.</summary>
    public string WebScheme =>
        Port is 443 or 8443 ||
        (Iana?.Contains("https", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Service?.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) ?? false)
            ? "https"
            : "http";

    /// <summary>Everything searchable about the port, for the filter box.</summary>
    public bool Matches(string filter) =>
        Port.ToString().Contains(filter, StringComparison.Ordinal) ||
        (Service?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Iana?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        CategoryLabel.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Display text for the engine's category ids. The engine keeps ids
    /// stable and lowercase and leaves wording to frontends; an id this
    /// build does not know yet is shown capitalised rather than hidden.
    /// </summary>
    public static string LabelFor(string? category) => category switch
    {
        null or "" => string.Empty,
        "web" => "Web",
        "remote" => "Remote access",
        "file" => "File sharing",
        "mail" => "Mail",
        "dns" => "DNS",
        "database" => "Database",
        "directory" => "Directory",
        "media" => "Media",
        "printing" => "Printing",
        "messaging" => "Messaging",
        "voip" => "Voice & video",
        "network" => "Network",
        "vpn" => "VPN",
        "proxy" => "Proxy",
        "home" => "Smart home",
        _ => char.ToUpperInvariant(category[0]) + category[1..],
    };

    /// <summary>
    /// One-line summary of a host's open ports for the Discovery table:
    /// "22 SSH, 80 HTTP, 443 HTTPS, +2 more", or "None open".
    /// </summary>
    public static string Summarize(IReadOnlyCollection<PortResult> ports, int max = 3)
    {
        if (ports.Count == 0)
        {
            return "None open";
        }

        string text = string.Join(", ", ports.OrderBy(p => p.Port).Take(max).Select(p => p.ShortText));
        return ports.Count > max ? $"{text}, +{ports.Count - max} more" : text;
    }

    /// <summary>Every open port, for tooltips, filtering and export: "22 SSH, 80 HTTP, 8123 Home Assistant".</summary>
    public static string ListAll(IEnumerable<PortResult> ports) =>
        string.Join(", ", ports.OrderBy(p => p.Port).Select(p => p.ShortText));
}
