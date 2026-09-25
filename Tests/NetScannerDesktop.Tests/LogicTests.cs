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
    public void Name_replaces_ip_as_the_title_once_known()
    {
        var host = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        Assert.Equal("192.168.1.10", host.DisplayName);
        Assert.False(host.HasSecondaryLine);

        host.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology Incorporated"));

        Assert.Equal("nas", host.DisplayName);
        Assert.Equal("192.168.1.10 · Synology Incorporated · 00:11:32:11:22:33", host.SecondaryLine);
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("synology")]
    [InlineData("00:11:32")]
    [InlineData("tcp")]
    [InlineData("SSH")]
    public void Filter_matches_every_visible_field(string filter)
    {
        var host = new HostResult("192.168.1.10", "TCP", DateTime.Now);
        host.ApplyDetail(new HostDetail("192.168.1.10", "nas", "00:11:32:11:22:33", "Synology Incorporated"));
        host.UpdateScannedPorts("Open: 22 (SSH)");

        Assert.True(host.Matches(filter));
        Assert.False(host.Matches("printer"));
    }
}
