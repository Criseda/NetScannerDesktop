using System;
using System.Collections.Generic;
using System.Linq;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;
using Xunit;

namespace NetScannerDesktop.Tests;

public class NetworkValidationTests
{
    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData(" 10.0.0.1 ", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.1", false)] // IPAddress.TryParse reads this as 192.168.0.1; ns rejects it
    [InlineData("0x7f.0.0.1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.2.3.256", false)]
    [InlineData("1..3.4", false)]
    [InlineData("::1", false)]
    public void Ipv4_is_strict_dotted_quad(string text, bool valid) =>
        Assert.Equal(valid, NetworkValidation.IsValidIpv4(text));

    [Theory]
    [InlineData("192.168.1.0/24", 254)]
    [InlineData("10.0.0.0/16", 65534)]
    [InlineData("10.0.0.4/30", 2)]
    [InlineData("10.0.0.4/31", 2)]
    [InlineData("10.0.0.4/32", 1)]
    public void Usable_hosts_match_the_engine(string cidr, long expected)
    {
        Assert.True(NetworkValidation.TryParseCidr(cidr, out _));
        Assert.Equal(expected, NetworkValidation.CountUsableHosts(cidr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.0")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1/24")]
    [InlineData("300.1.1.1/24")]
    [InlineData("::1/64")]
    public void Invalid_cidrs_explain_why(string cidr)
    {
        Assert.False(NetworkValidation.TryParseCidr(cidr, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("1-1024", 1, 1024)]
    [InlineData(" 80 - 443 ", 80, 443)]
    [InlineData("1024-1", 1, 1024)]
    [InlineData("22-22", 22, 22)]
    public void Port_ranges_normalise(string text, int start, int end)
    {
        Assert.True(NetworkValidation.TryParsePortRange(text, out int s, out int e, out _));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("0-10")]
    [InlineData("1-65536")]
    [InlineData("80")]
    [InlineData("a-b")]
    [InlineData("1-2-3")]
    public void Invalid_port_ranges(string text) =>
        Assert.False(NetworkValidation.TryParsePortRange(text, out _, out _, out _));

    [Theory]
    [InlineData("", true, null)]
    [InlineData("250", true, 250)]
    [InlineData("0", false, null)]
    [InlineData("60001", false, null)]
    [InlineData("fast", false, null)]
    public void Timeouts(string text, bool ok, int? expected)
    {
        Assert.Equal(ok, NetworkValidation.TryParseTimeout(text, out int? value, out _));
        Assert.Equal(expected, value);
    }
}

public class EngineVersionTests
{
    [Fact]
    public void Pinned_version_comes_from_the_app_csproj() =>
        Assert.Matches(@"^v\d+\.\d+\.\d+$", EngineUpdater.PinnedVersion);

    [Theory]
    [InlineData("v1.2.2", "v1.1.0", 1)]
    [InlineData("v1.10.0", "v1.9.9", 1)]
    [InlineData("1.2.2", "v1.2.2", 0)]
    [InlineData("v1.2.0-beta", "v1.2.0", 0)]
    [InlineData("v1.1.0", "v1.2.2", -1)]
    public void Versions_compare_numerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(EngineUpdater.CompareVersions(a, b)));

    [Fact]
    public void Only_the_pinned_major_is_compatible()
    {
        string major = EngineUpdater.PinnedVersion.TrimStart('v').Split('.')[0];
        int next = int.Parse(major) + 1;
        Assert.True(EngineUpdater.IsCompatible($"v{major}.99.0"));
        Assert.False(EngineUpdater.IsCompatible($"v{next}.0.0"));
    }
}

public class FilteredSortedViewTests
{
    private static readonly IComparer<int> Ascending = Comparer<int>.Default;

    [Fact]
    public void Streamed_items_land_in_sorted_position()
    {
        var view = new FilteredSortedView<int>(Ascending);
        foreach (int n in new[] { 5, 1, 4, 2, 3 })
        {
            view.Add(n);
        }

        Assert.Equal([1, 2, 3, 4, 5], view.View);
        Assert.Equal([5, 1, 4, 2, 3], view.Source);
    }

    [Fact]
    public void Each_add_is_a_single_insert()
    {
        var view = new FilteredSortedView<int>(Ascending);
        view.Add(10);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        view.View.CollectionChanged += (_, e) => changes.Add(e.Action);

        view.Add(5);

        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);
    }

    [Fact]
    public void Equal_items_keep_arrival_order()
    {
        var byLength = Comparer<string>.Create((a, b) => a.Length.CompareTo(b.Length));
        var view = new FilteredSortedView<string>(byLength);
        foreach (string s in new[] { "bb", "a", "cc", "dd" })
        {
            view.Add(s);
        }

        Assert.Equal(["a", "bb", "cc", "dd"], view.View);

        view.Refresh();
        Assert.Equal(["a", "bb", "cc", "dd"], view.View);
    }

    [Fact]
    public void Filter_hides_existing_and_new_items()
    {
        var view = new FilteredSortedView<int>(Ascending);
        view.Add(1);
        view.Add(2);
        view.SetFilter(n => n % 2 == 0);
        view.Add(3);
        view.Add(4);

        Assert.Equal([2, 4], view.View);
        Assert.Equal(4, view.Source.Count);
    }

    [Fact]
    public void Comparer_change_reorders()
    {
        var view = new FilteredSortedView<int>(Ascending);
        view.Add(1);
        view.Add(3);
        view.Add(2);
        view.SetComparer(Comparer<int>.Create((a, b) => b.CompareTo(a)));

        Assert.Equal([3, 2, 1], view.View);
    }

    [Fact]
    public void Clear_empties_both()
    {
        var view = new FilteredSortedView<int>(Ascending);
        view.Add(1);
        view.Clear();

        Assert.Empty(view.View);
        Assert.Empty(view.Source);
    }
}

public class CsvTests
{
    [Fact]
    public void Quotes_fields_that_need_it()
    {
        string csv = Csv.Format([["192.168.1.1", "VMware, Inc.", "say \"hi\""]], ["ip", "vendor", "note"]);
        Assert.Equal("ip,vendor,note\r\n192.168.1.1,\"VMware, Inc.\",\"say \"\"hi\"\"\"\r\n", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Neutralises_spreadsheet_formulas(string field, string expected) =>
        Assert.Equal($"h\r\n{expected}\r\n", Csv.Format([[field]], ["h"]));
}

public class HostResultTests
{
    [Fact]
    public void Name_replaces_ip_as_the_display_name_once_known()
    {
        var host = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        Assert.Equal("192.168.1.10", host.DisplayName);
        Assert.Equal("", host.DetailsLine);

        host.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology Incorporated"));

        Assert.Equal("nas", host.DisplayName);
        Assert.Equal("nas · Synology Incorporated · 00:11:32:11:22:33", host.DetailsLine);
    }

    [Fact]
    public void Ports_column_distinguishes_never_scanned_from_none_open()
    {
        var host = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        Assert.False(host.HasScannedPorts);
        Assert.Equal("", host.PortsSummary);
        Assert.Equal(-1, host.OpenPortCount);
        Assert.Equal("Scan ports", host.PortsButtonText);

        host.OpenPorts = [];
        Assert.Equal("None open", host.PortsSummary);
        Assert.Equal("View ports", host.PortsButtonText);

        host.OpenPorts = [new PortResult(443, "HTTPS"), new PortResult(22, "SSH"), new PortResult(80, "HTTP"), new PortResult(8123, "Home Assistant")];
        Assert.Equal("22 SSH, 80 HTTP, 443 HTTPS, +1 more", host.PortsSummary);
        Assert.Equal("22 SSH, 80 HTTP, 443 HTTPS, 8123 Home Assistant", host.PortsDetail);
        Assert.Equal(4, host.OpenPortCount);
    }

    [Fact]
    public void Copy_all_details_lists_only_known_fields_in_column_order()
    {
        var host = new HostResult("192.168.1.10", "ARP", DateTime.Now);
        Assert.False(host.HasHostname || host.HasMacAddress || host.HasVendor || host.HasOpenPorts);
        Assert.Equal(
            $"IP address: 192.168.1.10{Environment.NewLine}Found via: ARP{Environment.NewLine}Found at: {host.FoundAtShort}",
            host.AllDetailsText);

        host.OpenPorts = [];
        Assert.False(host.HasOpenPorts);
        Assert.Contains("Open ports: None open", host.AllDetailsText);

        host.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology Incorporated"));
        host.OpenPorts = [new PortResult(80, "HTTP"), new PortResult(22, "SSH")];
        Assert.True(host.HasHostname && host.HasMacAddress && host.HasVendor && host.HasOpenPorts);
        Assert.Equal(string.Join(Environment.NewLine,
            "Name: nas",
            "IP address: 192.168.1.10",
            "Manufacturer: Synology Incorporated",
            "MAC address: 00:11:32:11:22:33",
            "Found via: ARP",
            "Open ports: 22 SSH, 80 HTTP",
            $"Found at: {host.FoundAtShort}"), host.AllDetailsText);
    }

    [Fact]
    public void Hosts_table_puts_open_ports_last_and_leaves_unknowns_blank()
    {
        var nas = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        nas.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology"));
        nas.OpenPorts = [new PortResult(22, "SSH")];
        var quiet = new HostResult("192.168.1.2", "ARP", DateTime.Now);

        string[] lines = HostResult.FormatTable([nas, quiet]).Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Name  IP address    Manufacturer  MAC address", lines[0]);
        Assert.EndsWith("Open ports", lines[0]);
        Assert.StartsWith("nas   192.168.1.10  Synology      00:11:32:11:22:33", lines[1]);
        Assert.EndsWith("22 SSH", lines[1]);
        // Blank name, vendor and MAC cells keep their column widths (4, 12, 17).
        Assert.StartsWith(new string(' ', 4 + 2) + "192.168.1.2" + new string(' ', 1 + 2 + 12 + 2 + 17 + 2) + "ARP", lines[2]);
        Assert.EndsWith(quiet.FoundAtShort, lines[2]); // never port scanned: no trailing padding
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("synology")]
    [InlineData("00:11:32")]
    [InlineData("tcp")]
    [InlineData("SSH")]
    [InlineData("8123")]
    public void Filter_matches_every_visible_field(string filter)
    {
        var host = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        host.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology Incorporated"));
        host.OpenPorts = [new PortResult(22, "SSH"), new PortResult(80), new PortResult(443), new PortResult(8123)];

        Assert.True(host.Matches(filter));
        Assert.False(host.Matches("printer"));
    }
}

public class PortResultTests
{
    [Fact]
    public void Engine_names_fill_every_column()
    {
        var rdp = new PortResult(3389, "RDP", "ms-wbt-server", "Remote Desktop Protocol (Windows)", "remote", HasServiceData: true);

        Assert.Equal("RDP", rdp.ServiceText);
        Assert.True(rdp.IsIanaAssigned);
        Assert.False(rdp.ShowIanaUnassigned);
        Assert.Equal("Remote access", rdp.CategoryLabel);
        Assert.Equal("3389 — RDP", rdp.Display);
        Assert.False(rdp.IsWebPort);
    }

    [Fact]
    public void Copied_details_skip_unknown_fields_and_repeated_descriptions()
    {
        var ssh = new PortResult(22, "SSH", "ssh", "The Secure Shell (SSH) Protocol", "remote", HasServiceData: true);
        Assert.Equal(string.Join(Environment.NewLine,
            "Host: 192.168.1.10",
            "Port: 22",
            "Service: SSH",
            "Category: Remote access",
            "IANA name: ssh",
            "Description: The Secure Shell (SSH) Protocol"), ssh.DetailsText("192.168.1.10"));

        var unknown = new PortResult(40000, HasServiceData: true);
        Assert.Equal($"Host: 192.168.1.10{Environment.NewLine}Port: 40000", unknown.DetailsText("192.168.1.10"));

        var http = new PortResult(80, "HTTP", "http", "HTTP", "web", HasServiceData: true);
        Assert.DoesNotContain("Description", http.DetailsText("h"));
    }

    [Fact]
    public void Ports_table_names_the_host_and_aligns_columns()
    {
        string[] lines = PortResult.FormatTable("192.168.1.10",
        [
            new PortResult(22, "SSH", "ssh", "The Secure Shell (SSH) Protocol", "remote", HasServiceData: true),
            new PortResult(8123, "Home Assistant", null, "Home Assistant web UI", "home", HasServiceData: true),
            new PortResult(40000, HasServiceData: true),
        ]).Split(Environment.NewLine);

        Assert.Equal(
        [
            "Open ports on 192.168.1.10",
            "Port   Service         Category       IANA name  Description",
            "22     SSH             Remote access  ssh        The Secure Shell (SSH) Protocol",
            "8123   Home Assistant  Smart home                Home Assistant web UI",
            "40000",
        ], lines);
    }

    [Fact]
    public void Common_use_ports_show_unassigned_iana()
    {
        var ha = new PortResult(8123, "Home Assistant", null, "Home Assistant web UI", "home", HasServiceData: true);

        Assert.False(ha.IsIanaAssigned);
        Assert.True(ha.ShowIanaUnassigned);
        Assert.Equal("Smart home", ha.CategoryLabel);
    }

    [Fact]
    public void Unknown_only_when_the_engine_looked()
    {
        var looked = new PortResult(40000, HasServiceData: true);
        Assert.Equal("Unknown", looked.ServiceText);
        Assert.True(looked.ShowServicePlaceholder);
        Assert.True(looked.ShowIanaUnassigned);

        // An engine older than v1.4 reports numbers only: leave the cells blank.
        var old = new PortResult(40000);
        Assert.Equal("", old.ServiceText);
        Assert.False(old.ShowServicePlaceholder);
        Assert.False(old.ShowIanaUnassigned);
        Assert.False(old.HasCategory);
    }

    [Theory]
    [InlineData(80, "HTTP", "http", "http")]
    [InlineData(443, "HTTPS", "https", "https")]
    [InlineData(8443, "HTTPS alt", "pcsync-https", "https")]
    [InlineData(5001, "Web app (HTTPS)", "commplex-link", "https")]
    [InlineData(8080, "HTTP alt", "http-alt", "http")]
    public void Web_ports_open_with_the_right_scheme(int port, string service, string iana, string scheme)
    {
        var p = new PortResult(port, service, iana, null, "web", HasServiceData: true);
        Assert.True(p.IsWebPort);
        Assert.Equal(scheme, p.WebScheme);
    }

    [Theory]
    [InlineData("rdp")]
    [InlineData("wbt")]
    [InlineData("remote access")]
    [InlineData("desktop")]
    [InlineData("338")]
    public void Filter_matches_number_names_description_and_category(string filter) =>
        Assert.True(new PortResult(3389, "RDP", "ms-wbt-server", "Remote Desktop Protocol", "remote", true).Matches(filter));

    [Fact]
    public void Description_is_blank_when_it_repeats_the_name()
    {
        Assert.Null(new PortResult(1824, "metrics-pas", "metrics-pas", "metrics-pas", null, true).DescriptionText);
        Assert.Equal("Remote Framebuffer", new PortResult(5900, "VNC", "rfb", "Remote Framebuffer", "remote", true).DescriptionText);
    }

    [Fact]
    public void Unknown_categories_are_capitalised_not_hidden() =>
        Assert.Equal("Gaming", PortResult.LabelFor("gaming"));
}

public class TableSortTests
{
    private sealed record Row(string Ip, string? Name, int? Count);

    private static readonly IComparer<Row> ByIp = Comparer<Row>.Create((a, b) => string.CompareOrdinal(a.Ip, b.Ip));

    private static readonly Row[] Rows =
    [
        new("10.0.0.3", "printer", 2),
        new("10.0.0.1", null, null),
        new("10.0.0.2", "Laptop", 5),
        new("10.0.0.4", null, 0),
    ];

    [Fact]
    public void Blank_names_sort_last_in_both_directions()
    {
        Assert.Equal(["10.0.0.2", "10.0.0.3", "10.0.0.1", "10.0.0.4"],
            Rows.Order(TableSort.ByText<Row>(r => r.Name, descending: false, ByIp)).Select(r => r.Ip));
        Assert.Equal(["10.0.0.3", "10.0.0.2", "10.0.0.1", "10.0.0.4"],
            Rows.Order(TableSort.ByText<Row>(r => r.Name, descending: true, ByIp)).Select(r => r.Ip));
    }

    [Fact]
    public void Missing_values_sort_last_in_both_directions()
    {
        Assert.Equal(["10.0.0.4", "10.0.0.3", "10.0.0.2", "10.0.0.1"],
            Rows.Order(TableSort.ByValue<Row, int>(r => r.Count, descending: false, ByIp)).Select(r => r.Ip));
        Assert.Equal(["10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.1"],
            Rows.Order(TableSort.ByValue<Row, int>(r => r.Count, descending: true, ByIp)).Select(r => r.Ip));
    }

    [Fact]
    public void Header_glyph_marks_only_the_sorted_column()
    {
        Assert.Equal("\uE70E", TableSort.Glyph("ip", "ip", descending: false));
        Assert.Equal("\uE70D", TableSort.Glyph("ip", "ip", descending: true));
        Assert.Equal("", TableSort.Glyph("name", "ip", descending: false));
    }
}
