using System;
using NetScannerDesktop.Services;
using Xunit;

namespace NetScannerDesktop.Tests;

public class ScanEstimateTests
{
    [Fact]
    public void Auto_never_warns_even_for_every_port() =>
        Assert.Null(ScanEstimate.SlowPortScanWarning(65535, timeoutMs: null));

    [Theory]
    [InlineData(65535, 500, 32)]         // 64 rounds of 1024 probes
    [InlineData(65535, 60000, 3840)]     // about an hour
    [InlineData(1024, 60000, 60)]        // one round
    [InlineData(1025, 60000, 120)]       // a second round for one port
    [InlineData(1, 250, 0.25)]
    public void Worst_case_is_rounds_of_parallel_probes_times_timeout(int ports, int timeoutMs, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ScanEstimate.WorstCasePortScan(ports, timeoutMs));

    [Fact]
    public void Worst_case_is_unknown_for_auto() =>
        Assert.Null(ScanEstimate.WorstCasePortScan(65535, null));

    [Theory]
    [InlineData(65535, 500, false)]   // 32 s
    [InlineData(65535, 937, false)]   // just under a minute
    [InlineData(65535, 1000, true)]   // 64 s
    [InlineData(65535, 60000, true)]
    [InlineData(1024, 60000, false)]  // exactly a minute
    [InlineData(1025, 60000, true)]
    [InlineData(1024, 500, false)]
    public void Warns_only_past_a_minute(int ports, int timeoutMs, bool warns) =>
        Assert.Equal(warns, ScanEstimate.SlowPortScanWarning(ports, timeoutMs) is not null);

    [Theory]
    [InlineData(0.25, "1 second")]
    [InlineData(32, "32 seconds")]
    [InlineData(128, "about 2 minutes")]
    [InlineData(3840, "about 64 minutes")]
    public void Describes_lengths_roughly(double seconds, string expected) =>
        Assert.Equal(expected, ScanEstimate.Describe(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Describes_long_scans_in_hours()
    {
        // Culture-proof: the decimal separator depends on the machine.
        string text = ScanEstimate.Describe(TimeSpan.FromHours(2.5));
        Assert.StartsWith("about 2", text);
        Assert.EndsWith("5 hours", text);
    }
}
