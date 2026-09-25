using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetScannerDesktop.Models;

namespace NetScannerDesktop.Services;

/// <summary>Something the engine reported. Lines that are not events are log text only.</summary>
public abstract record EngineEvent;

public sealed record HostFoundEvent(HostResult Host) : EngineEvent;

public sealed record HostDetailEvent(HostDetail Detail) : EngineEvent;

/// <summary>One open port, named when the engine supports it (NetScanner v1.4+).</summary>
public sealed record PortFoundEvent(PortResult Result) : EngineEvent;

/// <summary>Closing recap of a port scan (empty list = "No open ports found").</summary>
public sealed record PortSummaryEvent(IReadOnlyList<int> OpenPorts) : EngineEvent;

public sealed record SubnetSummaryEvent(ScanSummary Summary) : EngineEvent;

public sealed record EngineErrorEvent(string Message) : EngineEvent;

/// <summary>
/// Turns engine output lines into <see cref="EngineEvent"/>s. One instance
/// per scan: text mode is stateful (the <c>--resolve</c> table rows only
/// mean something after its header).
/// <para>
/// JSON mode (<c>--json</c>, NetScanner v1.3.0+) reads one object
/// per line: <c>start</c>, <c>host</c>, <c>host_detail</c>, <c>port</c>,
/// <c>summary</c>, <c>error</c>. From v1.4.0 <c>port</c> also carries
/// <c>service</c>, <c>iana</c>, <c>description</c> and <c>category</c>.
/// Text mode covers v1.1–v1.2 output:
/// <c>Host 192.168.1.10 is online (arp)</c>, <c>Open port: 80</c>,
/// <c>Open ports: 80, 443</c>, <c>No open ports found</c>,
/// <c>12 hosts up (2.1s)</c>, <c>NetScanner: ...</c> errors, and the
/// <c>IP HOSTNAME MAC MANUFACTURER</c> table printed by <c>--resolve</c>,
/// <c>--hostname</c> and <c>--vendor</c>.
/// </para>
/// </summary>
public sealed partial class EngineOutputParser
{
    private readonly bool json;
    private readonly string textHostSource;

    /// <summary>Columns of the text-mode detail table once its header has been seen.</summary>
    private (bool Hostname, bool Vendor)? tableColumns;

    /// <param name="json">Lines are <c>--json</c> events rather than text.</param>
    /// <param name="textHostSource">
    /// Source label for text-mode hosts not tagged "(arp)": text output
    /// prints ping hits exactly like TCP hits, so the caller says which
    /// sweep ran. JSON events name their source themselves.
    /// </param>
    public EngineOutputParser(bool json, string textHostSource = "TCP")
    {
        this.json = json;
        this.textHostSource = textHostSource;
    }

    public EngineEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // Engine errors keep the text prefix on stderr in both modes (and
        // older engines only ever print that), so always recognise it.
        string trimmed = line.Trim();
        if (trimmed.StartsWith("NetScanner:", StringComparison.Ordinal))
        {
            return new EngineErrorEvent(trimmed);
        }

        return json ? ParseJson(trimmed) : ParseText(trimmed);
    }

    // JSON -----------------------------------------------------------------

    private static EngineEvent? ParseJson(string line)
    {
        if (!line.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            return Str(root, "type") switch
            {
                "host" when Str(root, "ip") is { } ip && NetworkValidation.IsValidIpv4(ip) =>
                    new HostFoundEvent(new HostResult(ip, SourceLabel(Str(root, "source")), DateTime.Now)),
                "host_detail" when Str(root, "ip") is { } ip =>
                    new HostDetailEvent(new HostDetail(ip, Str(root, "hostname"), Str(root, "mac"), Str(root, "vendor"))),
                "port" when root.TryGetProperty("port", out JsonElement p) && p.TryGetInt32(out int port) && IsPort(port) =>
                    new PortFoundEvent(new PortResult(port, Str(root, "service"), Str(root, "iana"),
                        Str(root, "description"), Str(root, "category"),
                        // Present (even as null) only on engines that name ports.
                        HasServiceData: root.TryGetProperty("service", out _))),
                "summary" when root.TryGetProperty("open_ports", out JsonElement ports) =>
                    new PortSummaryEvent(ports.EnumerateArray()
                        .Select(e => e.TryGetInt32(out int n) ? n : 0).Where(IsPort).Distinct().ToList()),
                "summary" when root.TryGetProperty("hosts", out JsonElement hosts) && hosts.TryGetInt32(out int count) =>
                    new SubnetSummaryEvent(JsonSummary(root, count)),
                "error" => new EngineErrorEvent(Str(root, "message") ?? "Engine error"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ScanSummary JsonSummary(JsonElement root, int hostCount)
    {
        TimeSpan elapsed = root.TryGetProperty("elapsed_ms", out JsonElement ms) && ms.TryGetInt64(out long v)
            ? TimeSpan.FromMilliseconds(v)
            : TimeSpan.Zero;
        string noun = hostCount == 1 ? "host" : "hosts";
        return new ScanSummary(hostCount,
            string.Create(CultureInfo.InvariantCulture, $"{hostCount} {noun} up ({elapsed.TotalSeconds:F1}s)"), elapsed);
    }

    private static string SourceLabel(string? source) => source switch
    {
        "arp" => "ARP",
        "ping" => "Ping",
        _ => "TCP",
    };

    private static bool IsPort(int port) => port is >= 1 and <= 65535;

    // Text -----------------------------------------------------------------

    [GeneratedRegex(@"^Host (?<ip>\d{1,3}(?:\.\d{1,3}){3}) is online(?<arp> \(arp\))?$")]
    private static partial Regex HostLine();

    [GeneratedRegex(@"^(?<count>\d+) hosts? up \((?<secs>[\d.]+)s\)$")]
    private static partial Regex SummaryLine();

    /// <summary>"Open port: 80", or "Open port: 80 (HTTP)" from v1.4+ text output.</summary>
    [GeneratedRegex(@"^Open port: (?<port>\d+)(?: \((?<name>.+)\))?$")]
    private static partial Regex OpenPortLine();

    [GeneratedRegex(@"^IP\s+(?<cols>(?:HOSTNAME|MAC|MANUFACTURER)(?:\s+(?:HOSTNAME|MAC|MANUFACTURER))*)$")]
    private static partial Regex TableHeader();

    /// <summary>
    /// A table row. Split on the MAC column rather than fixed widths:
    /// the engine pads columns but never truncates, so a long hostname
    /// pushes the rest of the row right.
    /// </summary>
    [GeneratedRegex(@"^(?<ip>\d{1,3}(?:\.\d{1,3}){3})(?:\s+(?<rest>.*))?$")]
    private static partial Regex TableRow();

    /// <summary>Hostname (optional), a real MAC, then the vendor ("-" when unknown).</summary>
    [GeneratedRegex(@"^(?<before>.*?)\s*(?<mac>(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2})\s+(?<vendor>.+)$")]
    private static partial Regex MacAndVendor();

    /// <summary>No MAC means no vendor either: the row ends in "-  -".</summary>
    [GeneratedRegex(@"^(?<before>.*?)\s*-\s+-$")]
    private static partial Regex NoMacNoVendor();

    private EngineEvent? ParseText(string line)
    {
        if (HostLine().Match(line) is { Success: true } host && NetworkValidation.IsValidIpv4(host.Groups["ip"].Value))
        {
            return new HostFoundEvent(new HostResult(
                host.Groups["ip"].Value, host.Groups["arp"].Success ? "ARP" : textHostSource, DateTime.Now));
        }

        if (OpenPortLine().Match(line) is { Success: true } open &&
            int.TryParse(open.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && IsPort(port))
        {
            return new PortFoundEvent(new PortResult(port, open.Groups["name"].Success ? open.Groups["name"].Value : null));
        }

        if (line.StartsWith("Open ports:", StringComparison.Ordinal))
        {
            return new PortSummaryEvent(line["Open ports:".Length..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, out int n) ? n : 0)
                .Where(IsPort)
                .Distinct()
                .ToList());
        }

        if (line == "No open ports found")
        {
            return new PortSummaryEvent(Array.Empty<int>());
        }

        if (SummaryLine().Match(line) is { Success: true } summary)
        {
            tableColumns = null;
            double.TryParse(summary.Groups["secs"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs);
            return new SubnetSummaryEvent(new ScanSummary(
                int.Parse(summary.Groups["count"].Value, CultureInfo.InvariantCulture), line, TimeSpan.FromSeconds(secs)));
        }

        if (TableHeader().Match(line) is { Success: true } header)
        {
            string cols = header.Groups["cols"].Value;
            tableColumns = (cols.Contains("HOSTNAME"), cols.Contains("MANUFACTURER"));
            return null;
        }

        return tableColumns is { } columns ? ParseTableRow(line, columns) : null;
    }

    private static HostDetailEvent? ParseTableRow(string line, (bool Hostname, bool Vendor) columns)
    {
        if (TableRow().Match(line) is not { Success: true } row || !NetworkValidation.IsValidIpv4(row.Groups["ip"].Value))
        {
            return null;
        }

        string ip = row.Groups["ip"].Value;
        string rest = row.Groups["rest"].Value.Trim();
        string? hostname = null, mac = null, vendor = null;

        if (!columns.Vendor)
        {
            hostname = Dash(rest);
        }
        else if (MacAndVendor().Match(rest) is { Success: true } mv)
        {
            hostname = columns.Hostname ? Dash(mv.Groups["before"].Value) : null;
            mac = mv.Groups["mac"].Value.Replace('-', ':').ToLowerInvariant();
            vendor = Dash(mv.Groups["vendor"].Value);
        }
        else if (NoMacNoVendor().Match(rest) is { Success: true } none)
        {
            hostname = columns.Hostname ? Dash(none.Groups["before"].Value) : null;
        }

        return new HostDetailEvent(new HostDetail(ip, hostname, mac, vendor));
    }

    /// <summary>The table prints "-" for an unresolved field.</summary>
    private static string? Dash(string value)
    {
        string v = value.Trim();
        return v.Length == 0 || v == "-" ? null : v;
    }
}
