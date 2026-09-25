using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.Models;

/// <summary>
/// One live host found by <c>ns -s</c>.
/// <see cref="Source"/> says how it was found: TCP probe, ARP harvest (the
/// host was quiet on TCP) or ICMP ping. Hostname, MAC and vendor arrive
/// later, after the sweep, when device identification is on. Also tracks
/// the open ports if this host has been port scanned.
/// </summary>
public sealed partial class HostResult : ObservableObject
{
    public string IpAddress { get; }
    public string Source { get; }
    public DateTime FoundAt { get; }

    public string FoundAtShort => FoundAt.ToString("T");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(DisplayName), nameof(HasHostname), nameof(AllDetailsText))]
    private string? hostname;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(HasMacAddress), nameof(AllDetailsText))]
    private string? macAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(HasVendor), nameof(AllDetailsText))]
    private string? vendor;

    // Enable the matching "Copy" menu entries only when there is something to copy.
    public bool HasHostname => !string.IsNullOrEmpty(Hostname);
    public bool HasMacAddress => !string.IsNullOrEmpty(MacAddress);
    public bool HasVendor => !string.IsNullOrEmpty(Vendor);

    /// <summary>Hostname when known, else the IP: what a person calls this device.</summary>
    public string DisplayName => Hostname ?? IpAddress;

    /// <summary>"nas-storage · Synology Incorporated · 00:11:32:…", for the filter.</summary>
    public string DetailsLine => string.Join(" · ",
        new[] { Hostname, Vendor, MacAddress }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Open ports from the last port scan of this host; null when never scanned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScannedPorts), nameof(HasOpenPorts), nameof(PortsSummary), nameof(PortsDetail), nameof(OpenPortCount), nameof(PortsButtonText), nameof(AllDetailsText))]
    private IReadOnlyList<PortResult>? openPorts;

    public bool HasScannedPorts => OpenPorts is not null;

    public bool HasOpenPorts => OpenPorts is { Count: > 0 };

    /// <summary>Table cell: "22 SSH, 80 HTTP, +2 more", "None open", or empty before a port scan.</summary>
    public string PortsSummary => OpenPorts is null ? string.Empty : PortResult.Summarize(OpenPorts);

    /// <summary>Every open port, for the tooltip, the filter and export.</summary>
    public string PortsDetail => OpenPorts is null ? string.Empty : PortResult.ListAll(OpenPorts);

    /// <summary>Sort key for the ports column: -1 before a port scan, so unscanned hosts sort last.</summary>
    public int OpenPortCount => OpenPorts?.Count ?? -1;

    public string PortsButtonText => HasScannedPorts ? "View ports" : "Scan ports";

    /// <summary>
    /// "Copy > All details": one labelled line per known field, in table
    /// column order, so it pastes readably into a note or a message.
    /// </summary>
    public string AllDetailsText => string.Join(Environment.NewLine, new[]
    {
        Line("Name", Hostname),
        Line("IP address", IpAddress),
        Line("Manufacturer", Vendor),
        Line("MAC address", MacAddress),
        Line("Found via", Source),
        Line("Open ports", PortsSummaryForTable),
        Line("Found at", FoundAtShort),
    }.Where(line => line is not null));

    private static string? Line(string label, string? value) =>
        string.IsNullOrEmpty(value) ? null : $"{label}: {value}";

    /// <summary>
    /// "Copy > Everything (as a table)": the hosts table as aligned text.
    /// Open ports go last because they can be much longer than the rest.
    /// </summary>
    public static string FormatTable(IEnumerable<HostResult> hosts) => TextTable.Format(
        ["Name", "IP address", "Manufacturer", "MAC address", "Found via", "Found at", "Open ports"],
        hosts.Select(h => new[]
        {
            h.Hostname ?? string.Empty, h.IpAddress, h.Vendor ?? string.Empty, h.MacAddress ?? string.Empty,
            h.Source, h.FoundAtShort, h.PortsSummaryForTable,
        }));

    private string PortsSummaryForTable => OpenPorts is null ? string.Empty : OpenPorts.Count > 0 ? PortsDetail : "None open";

    public HostResult(string ipAddress, string source, DateTime foundAt)
    {
        IpAddress = ipAddress;
        Source = source;
        FoundAt = foundAt;
    }

    public void ApplyDetail(HostDetail detail)
    {
        Hostname = detail.Hostname;
        MacAddress = detail.MacAddress;
        Vendor = detail.Vendor;
    }

    /// <summary>Everything searchable about the host, for the filter box.</summary>
    public bool Matches(string filter) =>
        IpAddress.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        DetailsLine.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        PortsDetail.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
