namespace NetScannerDesktop.Models;

/// <summary>
/// Identification for one host from <c>ns -s ... --resolve</c>: reported
/// once per host after the sweep. Null fields were not resolved (or not
/// requested).
/// </summary>
public sealed record HostDetail(string IpAddress, string? Hostname, string? MacAddress, string? Vendor);
