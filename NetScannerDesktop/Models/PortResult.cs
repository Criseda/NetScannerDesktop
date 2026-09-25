using NetScannerDesktop.Services;

namespace NetScannerDesktop.Models;

/// <summary>
/// One open TCP port. Service name is display-only enrichment.
/// </summary>
public sealed record PortResult(int Port)
{
    public string Service => WellKnownPorts.GetServiceName(Port);

    public string Display => string.IsNullOrEmpty(Service) ? Port.ToString() : $"{Port} — {Service}";

    public bool IsWebPort => Port is 80 or 443 or 8080 or 8443 or 8000 or 8888 or 3000 or 5000;

    public string Category => Port switch
    {
        80 or 443 or 8080 or 8443 or 8000 or 8888 or 3000 or 5000 => "Web",
        22 => "SSH",
        21 or 20 => "FTP",
        23 => "Telnet",
        25 or 110 or 143 or 465 or 587 or 993 or 995 => "Mail",
        53 => "DNS",
        3389 => "RDP",
        445 or 139 => "SMB",
        3306 or 5432 or 1433 or 1521 or 27017 or 6379 => "Database",
        _ => string.IsNullOrEmpty(Service) ? "Port" : Service
    };
}
