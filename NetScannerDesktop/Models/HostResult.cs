using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace NetScannerDesktop.Models;

/// <summary>
/// One live host found by <c>ns -s</c>.
/// <see cref="FoundViaArp"/> is true when the host was quiet on TCP and only
/// confirmed through the ARP harvest pass (printed as "(arp)" by ns).
/// Also tracks port scan summary and status if ports have been scanned for this host.
/// </summary>
public sealed partial class HostResult : ObservableObject
{
    public string IpAddress { get; }
    public bool FoundViaArp { get; }
    public DateTime FoundAt { get; }

    public string Source => FoundViaArp ? "ARP" : "TCP";

    public string FoundAtShort => FoundAt.ToString("HH:mm:ss");

    [ObservableProperty]
    private string? portsSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScannedPortsVisibility))]
    private bool hasScannedPorts;

    [ObservableProperty]
    private string portsButtonText = "Ports";

    public Visibility HasScannedPortsVisibility => HasScannedPorts ? Visibility.Visible : Visibility.Collapsed;

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
        PortsButtonText = HasScannedPorts ? "Ports ✓" : "Ports";
    }
}
