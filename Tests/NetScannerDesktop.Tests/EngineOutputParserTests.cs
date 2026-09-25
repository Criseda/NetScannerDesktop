using System;
using System.Collections.Generic;
using System.Linq;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;
using Xunit;

namespace NetScannerDesktop.Tests;

public class EngineOutputParserTextTests
{
    private static List<EngineEvent> ParseAll(string output, string textHostSource = "TCP")
    {
        var parser = new EngineOutputParser(json: false, textHostSource);
        return output.Split('\n')
            .Select(line => parser.Parse(line.TrimEnd('\r')))
            .OfType<EngineEvent>()
            .ToList();
    }

    [Fact]
    public void Streamed_hosts_carry_their_source()
    {
        var events = ParseAll("""
            Scanning network: 192.168.1.0/24 (Range: 192.168.1.0 - 192.168.1.255)
            Host 192.168.1.1 is online
            Host 192.168.1.25 is online (arp)
            """);

        var hosts = events.OfType<HostFoundEvent>().Select(e => e.Host).ToList();
        Assert.Equal(["192.168.1.1", "192.168.1.25"], hosts.Select(h => h.IpAddress));
        Assert.Equal(["TCP", "ARP"], hosts.Select(h => h.Source));
    }

    [Fact]
    public void Ping_sweep_hosts_are_labelled_by_the_caller()
    {
        // Text output prints ping hits exactly like TCP hits.
        var host = Assert.Single(ParseAll("Host 10.0.0.5 is online", textHostSource: "Ping").OfType<HostFoundEvent>());
        Assert.Equal("Ping", host.Host.Source);
    }

    [Theory]
    [InlineData("Host 999.1.1.1 is online")]
    [InlineData("Host 192.168.1 is online")]
    [InlineData("Some Host 192.168.1.1 is online now")]
    public void Malformed_host_lines_are_log_text(string line) =>
        Assert.Empty(ParseAll(line));

    [Fact]
    public void Plain_recap_ip_list_is_not_a_table()
    {
        // Without --resolve the recap lists bare IPs; they are not detail rows.
        var events = ParseAll("""
            Host 192.168.1.1 is online
            192.168.1.1
            1 host up (2.1s)
            """);

        Assert.Single(events.OfType<HostFoundEvent>());
        Assert.Empty(events.OfType<HostDetailEvent>());
        var summary = Assert.Single(events.OfType<SubnetSummaryEvent>()).Summary;
        Assert.Equal(1, summary.HostCount);
        Assert.Equal(TimeSpan.FromSeconds(2.1), summary.Elapsed);
    }

    [Fact]
    public void Resolve_table_from_ns_1_2_2()
    {
        // Captured from ns v1.2.2: `ns -s 127.0.0.1/32 --resolve`.
        var events = ParseAll("""
            Scanning network: 127.0.0.1/32 (Range: 127.0.0.1 - 127.0.0.1)
            Host 127.0.0.1 is online
            IP               HOSTNAME                  MAC                MANUFACTURER
            127.0.0.1        kubernetes                -                  -
            1 host up (2.1s)
            """);

        var detail = Assert.Single(events.OfType<HostDetailEvent>()).Detail;
        Assert.Equal(new HostDetail("127.0.0.1", "kubernetes", null, null), detail);
    }

    [Fact]
    public void Resolve_table_splits_on_the_mac_not_fixed_widths()
    {
        var details = ParseAll("""
            IP               HOSTNAME                  MAC                MANUFACTURER
            192.168.1.1      gateway                   00:50:56:a1:b2:c3  VMware, Inc.
            192.168.1.10     a-very-long-hostname-that-overflows.local 00:11:32:11:22:33  Synology Incorporated
            192.168.1.105    smart-light               -                  -
            192.168.1.140    -                         FC-CA-40-77-88-99  Sony Interactive Entertainment Inc.
            192.168.1.200    -                         -                  -
            3 hosts up (1.8s)
            """).OfType<HostDetailEvent>().Select(e => e.Detail).ToList();

        Assert.Equal(
        [
            new HostDetail("192.168.1.1", "gateway", "00:50:56:a1:b2:c3", "VMware, Inc."),
            new HostDetail("192.168.1.10", "a-very-long-hostname-that-overflows.local", "00:11:32:11:22:33", "Synology Incorporated"),
            new HostDetail("192.168.1.105", "smart-light", null, null),
            new HostDetail("192.168.1.140", null, "fc:ca:40:77:88:99", "Sony Interactive Entertainment Inc."),
            new HostDetail("192.168.1.200", null, null, null),
        ], details);
    }

    [Fact]
    public void Hostname_only_and_vendor_only_tables()
    {
        var hostnameOnly = ParseAll("""
            IP               HOSTNAME
            192.168.1.1      gateway
            192.168.1.2      -
            """).OfType<HostDetailEvent>().Select(e => e.Detail).ToList();
        Assert.Equal([new HostDetail("192.168.1.1", "gateway", null, null), new HostDetail("192.168.1.2", null, null, null)], hostnameOnly);

        var vendorOnly = ParseAll("""
            IP               MAC                MANUFACTURER
            192.168.1.1      00:50:56:a1:b2:c3  VMware, Inc.
            192.168.1.2      -                  -
            """).OfType<HostDetailEvent>().Select(e => e.Detail).ToList();
        Assert.Equal([new HostDetail("192.168.1.1", null, "00:50:56:a1:b2:c3", "VMware, Inc."), new HostDetail("192.168.1.2", null, null, null)], vendorOnly);
    }

    [Fact]
    public void Summary_ends_the_table()
    {
        var events = ParseAll("""
            IP               HOSTNAME
            192.168.1.1      gateway
            1 host up (0.4s)
            192.168.1.9
            """);

        Assert.Single(events.OfType<HostDetailEvent>());
    }

    [Fact]
    public void Port_scan_stream_and_recap()
    {
        var events = ParseAll("""
            Open port: 443
            Open port: 80
            Open ports: 80, 443
            """);

        Assert.Equal([443, 80], events.OfType<PortFoundEvent>().Select(e => e.Result.Port));
        Assert.Equal([80, 443], Assert.Single(events.OfType<PortSummaryEvent>()).OpenPorts);
    }

    [Fact]
    public void Named_text_port_lines_keep_the_name()
    {
        PortResult port = Assert.Single(ParseAll("Open port: 3389 (RDP)").OfType<PortFoundEvent>()).Result;
        Assert.Equal(new PortResult(3389, "RDP"), port);
    }

    [Fact]
    public void No_open_ports_is_an_empty_recap() =>
        Assert.Empty(Assert.Single(ParseAll("No open ports found").OfType<PortSummaryEvent>()).OpenPorts);

    [Theory]
    [InlineData("Open port: 0")]
    [InlineData("Open port: 70000")]
    [InlineData("Open port: http")]
    public void Out_of_range_ports_are_ignored(string line) =>
        Assert.Empty(ParseAll(line));

    [Fact]
    public void Engine_errors_are_recognised() =>
        Assert.Equal("NetScanner: Invalid IP address",
            Assert.Single(ParseAll("NetScanner: Invalid IP address").OfType<EngineErrorEvent>()).Message);
}

public class EngineOutputParserJsonTests
{
    private static List<EngineEvent> ParseAll(string output)
    {
        var parser = new EngineOutputParser(json: true);
        return output.Split('\n')
            .Select(line => parser.Parse(line.TrimEnd('\r')))
            .OfType<EngineEvent>()
            .ToList();
    }

    [Fact]
    public void Subnet_scan_events()
    {
        // Captured from ns with --json: `ns -s 127.0.0.1/32 --resolve --json`, plus an ARP host.
        var events = ParseAll("""
            {"type":"start","mode":"subnet","cidr":"127.0.0.1/32","first":"127.0.0.1","last":"127.0.0.1"}
            {"type":"host","ip":"127.0.0.1","source":"tcp"}
            {"type":"host","ip":"127.0.0.2","source":"arp"}
            {"type":"host","ip":"127.0.0.3","source":"ping"}
            {"type":"host_detail","ip":"127.0.0.1","hostname":"kubernetes","mac":null,"vendor":null}
            {"type":"summary","hosts":3,"elapsed_ms":2094}
            """);

        Assert.Equal(["TCP", "ARP", "Ping"], events.OfType<HostFoundEvent>().Select(e => e.Host.Source));
        Assert.Equal(new HostDetail("127.0.0.1", "kubernetes", null, null), Assert.Single(events.OfType<HostDetailEvent>()).Detail);

        var summary = Assert.Single(events.OfType<SubnetSummaryEvent>()).Summary;
        Assert.Equal(3, summary.HostCount);
        Assert.Equal(TimeSpan.FromMilliseconds(2094), summary.Elapsed);
        Assert.Equal("3 hosts up (2.1s)", summary.RawLine);
    }

    [Fact]
    public void Escaped_strings_are_decoded()
    {
        var detail = Assert.Single(ParseAll(
            """{"type":"host_detail","ip":"10.0.0.2","hostname":"odd\"name\\x","mac":"aa:bb:cc:dd:ee:ff","vendor":"Société"}""")
            .OfType<HostDetailEvent>()).Detail;

        Assert.Equal("odd\"name\\x", detail.Hostname);
        Assert.Equal("Société", detail.Vendor);
    }

    [Fact]
    public void Port_scan_events()
    {
        var events = ParseAll("""
            {"type":"port","port":135}
            {"type":"summary","open_ports":[135,445],"elapsed_ms":515}
            """);

        Assert.Equal([135], events.OfType<PortFoundEvent>().Select(e => e.Result.Port));
        Assert.False(Assert.Single(events.OfType<PortFoundEvent>()).Result.HasServiceData);
        Assert.Equal([135, 445], Assert.Single(events.OfType<PortSummaryEvent>()).OpenPorts);
    }

    [Fact]
    public void Named_port_events()
    {
        // Captured from ns v1.4 `ns -p 127.0.0.1 130-450 --json`, plus a common-use and an unknown port.
        var ports = ParseAll("""
            {"type":"port","port":135,"service":"MS RPC","iana":"epmap","description":"Microsoft RPC endpoint mapper","category":"network"}
            {"type":"port","port":8123,"service":"Home Assistant","iana":null,"description":"Home Assistant web UI","category":"home"}
            {"type":"port","port":40000,"service":null,"iana":null,"description":null,"category":null}
            """).OfType<PortFoundEvent>().Select(e => e.Result).ToList();

        Assert.Equal(new PortResult(135, "MS RPC", "epmap", "Microsoft RPC endpoint mapper", "network", true), ports[0]);
        Assert.Equal(new PortResult(8123, "Home Assistant", null, "Home Assistant web UI", "home", true), ports[1]);
        Assert.Equal(new PortResult(40000, HasServiceData: true), ports[2]);
    }

    [Fact]
    public void Error_event() =>
        Assert.Equal("Invalid subnet 'x\"y' (use CIDR like 192.168.1.0/24)",
            Assert.Single(ParseAll("""{"type":"error","message":"Invalid subnet 'x\"y' (use CIDR like 192.168.1.0/24)"}""")
                .OfType<EngineErrorEvent>()).Message);

    [Theory]
    [InlineData("""{"type":"host","ip":"not-an-ip","source":"tcp"}""")]
    [InlineData("""{"type":"port","port":0}""")]
    [InlineData("""{"type":"mystery"}""")]
    [InlineData("""{"type":"host",""")]
    [InlineData("Host 10.0.0.1 is online")]
    [InlineData("arp table is empty: macOS hides it")]
    public void Unknown_or_malformed_lines_are_log_text(string line) =>
        Assert.Empty(ParseAll(line));

    [Fact]
    public void Text_errors_on_stderr_still_count_in_json_mode() =>
        Assert.Single(ParseAll("NetScanner: Unknown option").OfType<EngineErrorEvent>());
}
