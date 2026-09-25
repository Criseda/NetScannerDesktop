using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NetScannerDesktop.Models;

/// <summary>
/// One live host found by <c>ns -s</c>.
/// <see cref="Source"/> says how it was found: TCP probe, ARP harvest (the
/// host was quiet on TCP) or ICMP ping. Hostname, MAC and vendor arrive
/// later, after the sweep, when device identification is on. Also tracks
/// the port scan summary if ports have been scanned for this host.
/// </summary>
public sealed partial class HostResult : ObservableObject
{
    public string IpAddress { get; }
    public string Source { get; }
    public DateTime FoundAt { get; }

    public string FoundAtShort => FoundAt.ToString("T");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(HasDetails), nameof(DisplayName), nameof(SecondaryLine), nameof(HasSecondaryLine))]
    private string? hostname;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(HasDetails), nameof(SecondaryLine), nameof(HasSecondaryLine))]
    private string? macAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLine), nameof(HasDetails), nameof(SecondaryLine), nameof(HasSecondaryLine))]
    private string? vendor;

    /// <summary>Hostname when known, else the IP: what a person calls this device.</summary>
    public string DisplayName => Hostname ?? IpAddress;

    /// <summary>Secondary line under the IP, e.g. "nas-storage · Synology Incorporated · 00:11:32:…".</summary>
    public string DetailsLine => string.Join(" · ",
        new[] { Hostname, Vendor, MacAddress }.Where(s => !string.IsNullOrEmpty(s)));

    public bool HasDetails => DetailsLine.Length > 0;

    /// <summary>Line under <see cref="DisplayName"/>: the IP when a name took its place, then vendor and MAC.</summary>
    public string SecondaryLine => string.Join(" · ",
        new[] { Hostname is null ? null : IpAddress, Vendor, MacAddress }.Where(s => !string.IsNullOrEmpty(s)));

    public bool HasSecondaryLine => SecondaryLine.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsButtonText))]
    private string? portsSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsButtonText))]
    private bool hasScannedPorts;

    public string PortsButtonText => HasScannedPorts ? "View ports" : "Scan ports";

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

    public void UpdateScannedPorts(string? summary)
    {
        PortsSummary = summary;
        HasScannedPorts = !string.IsNullOrEmpty(summary);
    }

    /// <summary>Everything searchable about the host, for the filter box.</summary>
    public bool Matches(string filter) =>
        IpAddress.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        DetailsLine.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (PortsSummary?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
