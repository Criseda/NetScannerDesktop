using System.Collections.Generic;

namespace NetScannerDesktop.Services;

/// <summary>
/// Well-known TCP service names for display only. The engine reports
/// port numbers; this maps the common ones so the list reads naturally.
/// </summary>
public static class WellKnownPorts
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [20] = "FTP data",
        [21] = "FTP",
        [22] = "SSH",
        [23] = "Telnet",
        [25] = "SMTP",
        [53] = "DNS",
        [67] = "DHCP server",
        [68] = "DHCP client",
        [69] = "TFTP",
        [80] = "HTTP",
        [110] = "POP3",
        [123] = "NTP",
        [135] = "MS RPC",
        [137] = "NetBIOS name",
        [138] = "NetBIOS datagram",
        [139] = "NetBIOS session",
        [143] = "IMAP",
        [161] = "SNMP",
        [162] = "SNMP trap",
        [389] = "LDAP",
        [443] = "HTTPS",
        [445] = "SMB",
        [465] = "SMTPS",
        [514] = "Syslog",
        [587] = "SMTP submit",
        [636] = "LDAPS",
        [993] = "IMAPS",
        [995] = "POP3S",
        [1433] = "MSSQL",
        [1521] = "Oracle",
        [1723] = "PPTP",
        [3306] = "MySQL",
        [3389] = "RDP",
        [5432] = "PostgreSQL",
        [5900] = "VNC",
        [6379] = "Redis",
        [8000] = "HTTP alt",
        [8080] = "HTTP proxy",
        [8443] = "HTTPS alt",
        [27017] = "MongoDB",
    };

    public static string GetServiceName(int port) =>
        Names.TryGetValue(port, out string? name) ? name : string.Empty;
}
