using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NetScannerDesktop.Models;

/// <summary>
/// One live host found by <c>ns -s</c>.
/// <see cref="FoundViaArp"/> is true when the host was quiet on TCP and only
/// confirmed through the ARP harvest pass (printed as "(arp)" by ns).
/// Also tracks the port scan summary if ports have been scanned for this host.
/// </summary>
public sealed partial class HostResult : ObservableObject
{
    public string IpAddress { get; }
    public bool FoundViaArp { get; }
    public DateTime FoundAt { get; }

    public string Source => FoundViaArp ? "ARP" : "TCP";

    public string FoundAtShort => FoundAt.ToString("T");

    [ObservableProperty]
    private string? portsSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortsButtonText))]
    private bool hasScannedPorts;

    public string PortsButtonText => HasScannedPorts ? "View ports" : "Scan ports";

    public HostResult(string ipAddress, bool foundViaArp, DateTime foundAt)
    {
        IpAddress = ipAddress;
        FoundViaArp = foundViaArp;
        FoundAt = foundAt;
    }

    public void UpdateScannedPorts(string? summary)
    {
        PortsSummary = summary;
        HasScannedPorts = !string.IsNullOrEmpty(summary);
    }
}
