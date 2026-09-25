using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// Port scan page: runs <c>ns -p</c> for one host and streams open ports.
/// Mirrors <see cref="DiscoveryViewModel"/> on purpose so both pages read
/// the same way.
/// </summary>
public sealed partial class PortScanViewModel : ScanViewModelBase
{
    private static readonly IComparer<PortResult> ByPort =
        Comparer<PortResult>.Create((a, b) => a.Port.CompareTo(b.Port));

    private readonly FilteredSortedView<PortResult> portView = new(ByPort);

    public const int LargePortScanConfirmThreshold = 10000;

    /// <summary>Range presets offered next to the port range box.</summary>
    public static IReadOnlyList<PortRangePreset> Presets { get; } =
    [
        new("Well-known ports", "1-1024"),
        new("Common services", "1-10000"),
        new("Registered ports", "1-49151"),
        new("All ports", "1-65535"),
    ];

    public PortScanViewModel() : this(new NetScannerService())
    {
    }

    public PortScanViewModel(INetScannerService scanner) : base(scanner, defaultSortColumn: "port")
    {
        StatusText = "Enter a host to start.";
        VisiblePorts.CollectionChanged += (_, _) => NotifyEmptyStateChanged();

        ScanHistoryService.HistoryCleared += () =>
        {
            RefreshRecent();
            if (!IsScanning)
            {
                ClearPorts();
                resultTarget = null;
                NotifyEmptyStateChanged();
                StatusText = "History cleared.";
            }
        };
    }

    // Results ----------------------------------------------------------------

    /// <summary>Every open port of the shown result.</summary>
    public IReadOnlyList<PortResult> OpenPorts => portView.Source;

    /// <summary>Open ports matching the filter, in the chosen order. Bind the list to this.</summary>
    public ObservableCollection<PortResult> VisiblePorts => portView.View;

    public int PortCount => OpenPorts.Count;

    public string PortCountTitle => PortCount == 1 ? "1 open port" : $"{PortCount:N0} open ports";

    private void AddPort(PortResult port)
    {
        if (OpenPorts.All(p => p.Port != port.Port))
        {
            portView.Add(port);
            OnPropertyChanged(nameof(PortCount));
            OnPropertyChanged(nameof(PortCountTitle));
        }
    }

    private void ClearPorts()
    {
        portView.Clear();
        OnPropertyChanged(nameof(PortCount));
        OnPropertyChanged(nameof(PortCountTitle));
    }

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value)
    {
        string f = value.Trim();
        portView.SetFilter(f.Length == 0 ? _ => true : p => p.Matches(f));
    }

    protected override void ApplySort() => portView.SetComparer(PortComparer(SortColumn, SortDescending));

    /// <summary>Comparer for a Port scan table column; blank cells last, ties by port number.</summary>
    internal static IComparer<PortResult> PortComparer(string column, bool descending) => column switch
    {
        "service" => TableSort.ByText<PortResult>(p => p.Service, descending, ByPort),
        "category" => TableSort.ByText<PortResult>(p => p.CategoryLabel, descending, ByPort),
        "iana" => TableSort.ByText<PortResult>(p => p.Iana, descending, ByPort),
        "description" => TableSort.ByText<PortResult>(p => p.Description, descending, ByPort),
        _ => TableSort.ByValue<PortResult, int>(p => p.Port, descending),
    };

    // Form -----------------------------------------------------------------

    [ObservableProperty]
    private string ipAddress = string.Empty;

    [ObservableProperty]
    private string portRange = "1-1024";

    /// <summary>NumberBox value; NaN (cleared box) means the engine default.</summary>
    [ObservableProperty]
    private double timeoutMs = 500;

    public ObservableCollection<string> RecentIps { get; } = new();

    public string PortEstimateText
    {
        get
        {
            if (!NetworkValidation.TryParsePortRange(PortRange, out int start, out int end, out _))
            {
                return "Start and end port, 1-65535. Either order works.";
            }

            int n = end - start + 1;
            return $"{n:N0} {(n == 1 ? "port" : "ports")} will be probed.";
        }
    }

    // Empty state ------------------------------------------------------------

    /// <summary>The host the shown results belong to (a finished scan or history); null before any.</summary>
    private string? resultTarget;

    public bool ShowEmptyState => !IsScanning && VisiblePorts.Count == 0;

    private bool IsFilteredOut => PortCount > 0;

    public string EmptyStateGlyph => IsFilteredOut ? "" : resultTarget is null ? "" : "";

    public string EmptyStateTitle => IsFilteredOut ? "No matching ports"
        : resultTarget is null ? "Ready to scan ports"
        : "No open ports found";

    public string EmptyStateMessage => IsFilteredOut
        ? $"None of the {PortCountTitle} match “{FilterText.Trim()}”."
        : resultTarget is null
            ? "Enter a host, or pick one from Discovery, to probe it for open TCP services."
            : $"Every port scanned on {resultTarget} was closed or filtered. Try a wider range or a longer timeout.";

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateGlyph));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    protected override void OnScanStateChanged()
    {
        ScanCommand.NotifyCanExecuteChanged();
        NotifyEmptyStateChanged();
    }

    // Validation -------------------------------------------------------------

    public string IpErrorText =>
        string.IsNullOrWhiteSpace(IpAddress) || NetworkValidation.IsValidIpv4(IpAddress)
            ? string.Empty
            : "Enter a valid IPv4 address, e.g. 192.168.1.1.";

    public bool HasIpError => !string.IsNullOrEmpty(IpErrorText);

    public string PortRangeErrorText =>
        string.IsNullOrWhiteSpace(PortRange) || NetworkValidation.TryParsePortRange(PortRange, out _, out _, out string error)
            ? string.Empty
            : error;

    public bool HasPortRangeError => !string.IsNullOrEmpty(PortRangeErrorText);

    /// <summary>Engine value for --timeout: null (engine default) when the box is cleared.</summary>
    private int? TimeoutArgument => double.IsNaN(TimeoutMs) ? null : (int)Math.Clamp(TimeoutMs, 1, 60000);

    public bool CanScan => !IsScanning
        && !IsEngineMissing
        && !string.IsNullOrWhiteSpace(IpAddress)
        && !string.IsNullOrWhiteSpace(PortRange)
        && !HasIpError
        && !HasPortRangeError;

    partial void OnIpAddressChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IpErrorText));
        OnPropertyChanged(nameof(HasIpError));
    }

    partial void OnPortRangeChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PortEstimateText));
        OnPropertyChanged(nameof(PortRangeErrorText));
        OnPropertyChanged(nameof(HasPortRangeError));
    }

    // Commands ----------------------------------------------------------------

    [RelayCommand]
    private void ApplyPreset(string range) => PortRange = range;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!NetworkValidation.TryParsePortRange(PortRange, out int start, out int end, out string error))
        {
            ShowNotice(error, InfoBarSeverity.Warning);
            return;
        }

        int portCount = end - start + 1;
        if (portCount > LargePortScanConfirmThreshold && ConfirmLargeScanAsync is not null &&
            !await ConfirmLargeScanAsync($"This will probe {portCount:N0} ports and may take a while. Continue?"))
        {
            StatusText = "Large scan not started.";
            return;
        }

        ClearPorts();
        string target = IpAddress.Trim();
        string range = $"{start}-{end}";
        int? timeout = TimeoutArgument;
        AppSettings.SetString(AppSettings.PortIp, target);
        AppSettings.SetString(AppSettings.PortRange, range);
        AppSettings.SetString(AppSettings.PortTimeout, timeout?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        AppSettings.PushRecent(AppSettings.PortRecent, target);
        RefreshRecent();

        var portProgress = new Progress<PortResult>(port =>
        {
            AddPort(port);
            RefreshProgress();
        });

        await RunScanAsync(
            elapsed => $"Scanning {target} • {FormatElapsed(elapsed)} • {PortCountTitle}…",
            async (token, stopwatch) =>
            {
                PortScanResult result = await Scanner.ScanPortsAsync(
                    target, start, end, portProgress, new Progress<string>(AppendLogLine), token, timeout);

                resultTarget = target;
                string total = FormatElapsed(stopwatch.Elapsed);
                StatusText = result.OpenPorts.Count == 0
                    ? $"No open ports on {target} ({range}, {total})."
                    : $"{PortCountTitle} on {target} ({range}, {total}).";

                ScanHistoryService.Record(target, range, OpenPorts);
            },
            elapsed => $"Cancelled after {FormatElapsed(elapsed)} — {PortCountTitle} found.");

        NotifyEmptyStateChanged();
    }

    [RelayCommand]
    private void CopyResults()
    {
        if (VisiblePorts.Count > 0)
        {
            CopyToClipboard(string.Join(", ", VisiblePorts.Select(p => p.Port)),
                $"Copied {VisiblePorts.Count:N0} ports to the clipboard.");
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (VisiblePorts.Count == 0)
        {
            ShowNotice("Nothing to export yet — run a scan first.", InfoBarSeverity.Warning);
            return;
        }

        string host = resultTarget ?? IpAddress.Trim();
        string csv = Csv.Format(
            VisiblePorts.Select(p => new[]
            {
                host, p.Port.ToString(CultureInfo.InvariantCulture), p.Service ?? string.Empty,
                p.Iana ?? string.Empty, p.CategoryLabel, p.Description ?? string.Empty,
            }),
            new[] { "ip_address", "port", "service", "iana_service", "category", "description" });

        await SaveCsvAsync("ports", csv, $"{VisiblePorts.Count:N0} ports");
    }

    /// <summary>
    /// Loads a target IP, restoring any previously scanned open ports from history
    /// or resetting to an empty ready state if this IP has not been scanned yet.
    /// </summary>
    public void LoadTargetIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || IsScanning)
        {
            return;
        }

        string cleanIp = ip.Trim();
        IpAddress = cleanIp;
        ClearPorts();
        ClearLog();

        if (ScanHistoryService.TryGet(cleanIp, out var history) && history != null)
        {
            foreach (PortResult port in history.OpenPorts)
            {
                AddPort(port);
            }

            if (!string.IsNullOrEmpty(history.PortRange))
            {
                PortRange = history.PortRange;
            }

            resultTarget = cleanIp;
            StatusText = $"Showing the scan from {history.ScannedAt:g}. Press Scan to refresh.";
        }
        else
        {
            PortRange = AppSettings.GetString(AppSettings.DefaultPortRange, "1-1024");
            resultTarget = null;
            StatusText = $"Ready to scan {cleanIp}. Press Scan or F5.";
        }

        NotifyEmptyStateChanged();
    }

    private bool loaded;

    /// <summary>
    /// Restore last session + recent IPs once. The page is cached, so this
    /// must never run again over the user's edits.
    /// </summary>
    public async Task LoadAsync()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        string savedTimeout = AppSettings.GetString(AppSettings.PortTimeout);
        if (string.IsNullOrWhiteSpace(savedTimeout))
        {
            savedTimeout = AppSettings.GetString(AppSettings.DefaultTimeout, "500");
        }

        TimeoutMs = double.TryParse(savedTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out double t)
            ? t
            : double.NaN;

        // A Discovery handoff may already have set the target.
        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            string savedIp = AppSettings.GetString(AppSettings.PortIp);
            if (!string.IsNullOrWhiteSpace(savedIp))
            {
                LoadTargetIp(savedIp);
            }
            else
            {
                PortRange = AppSettings.GetString(AppSettings.DefaultPortRange, "1-1024");
            }
        }

        RefreshRecent();
        await ProbeEngineAsync();
    }

    private void RefreshRecent()
    {
        RecentIps.Clear();
        foreach (string recent in AppSettings.GetRecent(AppSettings.PortRecent))
        {
            RecentIps.Add(recent);
        }
    }
}

/// <summary>A named port range for the presets menu.</summary>
public sealed record PortRangePreset(string Name, string Range)
{
    public string Display => $"{Name} ({Range})";
}
