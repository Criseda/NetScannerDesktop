using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// Port scan page: runs <c>ns -p</c> for one host and streams open ports.
/// Mirrors <see cref="DiscoveryViewModel"/> on purpose so both pages read
/// the same way.
/// </summary>
public sealed partial class PortScanViewModel : ObservableObject
{
    private readonly INetScannerService scanner;
    private readonly EngineLog log = new();
    private CancellationTokenSource? runningScan;

    public const int LargePortScanConfirmThreshold = 10000;

    /// <summary>Range presets offered next to the port range box.</summary>
    public static IReadOnlyList<PortRangePreset> Presets { get; } =
    [
        new("Well-known ports", "1-1024"),
        new("Common services", "1-10000"),
        new("Registered ports", "1-49151"),
        new("All ports", "1-65535"),
    ];

    /// <summary>Set by the view to confirm very large port ranges.</summary>
    public Func<string, Task<bool>>? ConfirmLargeScanAsync { get; set; }

    /// <summary>Set by the view: (suggestedName, extension, content) -> saved path or null.</summary>
    public Func<string, string, string, Task<string?>>? SaveFileAsync { get; set; }

    public PortScanViewModel() : this(new NetScannerService())
    {
    }

    public PortScanViewModel(INetScannerService scanner)
    {
        this.scanner = scanner;

        // Titles and empty-state hints follow the list automatically.
        OpenPorts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PortCountTitle));
            OnPropertyChanged(nameof(FilteredPorts));
            NotifyEmptyStateChanged();
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            RefreshRecent();
            if (!IsScanning)
            {
                OpenPorts.Clear();
                resultTarget = null;
                NotifyEmptyStateChanged();
                StatusText = "History cleared.";
            }
        };
    }

    // Form -----------------------------------------------------------------

    [ObservableProperty]
    private string ipAddress = string.Empty;

    [ObservableProperty]
    private string portRange = "1-1024";

    /// <summary>NumberBox value; NaN (cleared box) means the engine default.</summary>
    [ObservableProperty]
    private double timeoutMs = 500;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = "Enter a host to start.";

    [ObservableProperty]
    private string logText = string.Empty;

    [ObservableProperty]
    private bool isNoticeOpen;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity noticeSeverity = InfoBarSeverity.Informational;

    public ObservableCollection<PortResult> OpenPorts { get; } = new();

    public ObservableCollection<string> RecentIps { get; } = new();

    public string PortCountTitle => OpenPorts.Count == 1 ? "1 open port" : $"{OpenPorts.Count:N0} open ports";

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredPorts));

    public IEnumerable<PortResult> FilteredPorts
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilterText))
            {
                return OpenPorts.OrderBy(p => p.Port).ToList();
            }

            string f = FilterText.Trim();
            return OpenPorts
                .Where(p => p.Port.ToString().Contains(f, StringComparison.OrdinalIgnoreCase)
                    || p.Service.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || p.Category.Contains(f, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Port)
                .ToList();
        }
    }

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

    public bool ShowEmptyState => !IsScanning && OpenPorts.Count == 0;

    public string EmptyStateGlyph => resultTarget is null ? "" : "";

    public string EmptyStateTitle => resultTarget is null ? "Ready to scan ports" : "No open ports found";

    public string EmptyStateMessage => resultTarget is null
        ? "Enter a host, or pick one from Discovery, to probe it for open TCP services."
        : $"Every port scanned on {resultTarget} was closed or filtered. Try a wider range or a longer timeout.";

    partial void OnIsScanningChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        NotifyEmptyStateChanged();
    }

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateGlyph));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
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

    [ObservableProperty]
    private bool isEngineMissing;

    partial void OnIsEngineMissingChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

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
        if (portCount > LargePortScanConfirmThreshold && ConfirmLargeScanAsync is not null)
        {
            bool ok = await ConfirmLargeScanAsync(
                $"This will probe {portCount:N0} ports and may take a while. Continue?");
            if (!ok)
            {
                StatusText = "Large scan not started.";
                return;
            }
        }

        HideNotice();
        OpenPorts.Clear();
        log.Clear();
        LogText = string.Empty;
        OnPropertyChanged(nameof(LogLineCountText));

        string target = IpAddress.Trim();
        string range = $"{start}-{end}";
        int? timeout = TimeoutArgument;
        AppSettings.SetString(AppSettings.PortIp, target);
        AppSettings.SetString(AppSettings.PortRange, range);
        AppSettings.SetString(AppSettings.PortTimeout, timeout?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        AppSettings.PushRecent(AppSettings.PortRecent, target);
        RefreshRecent();

        IsScanning = true;
        runningScan = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        void ShowProgress() =>
            StatusText = $"Scanning {target} • {FormatElapsed(stopwatch.Elapsed)} • {PortCountTitle}…";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => ShowProgress();
        timer.Start();
        ShowProgress();

        var portProgress = new Progress<int>(port =>
        {
            if (OpenPorts.All(p => p.Port != port))
            {
                OpenPorts.Add(new PortResult(port));
                ShowProgress();
            }
        });
        var logProgress = new Progress<string>(AppendLogLine);

        try
        {
            PortScanResult result = await scanner.ScanPortsAsync(
                target, start, end, portProgress, logProgress, runningScan.Token, timeout);

            FlushLog();
            resultTarget = target;
            string total = FormatElapsed(stopwatch.Elapsed);
            StatusText = result.OpenPorts.Count == 0
                ? $"No open ports on {target} ({range}, {total})."
                : $"{PortCountTitle} on {target} ({range}, {total}).";

            ScanHistoryService.Record(target, range, OpenPorts);
        }
        catch (OperationCanceledException)
        {
            FlushLog();
            StatusText = $"Cancelled after {FormatElapsed(stopwatch.Elapsed)} — {PortCountTitle} found.";
        }
        catch (Exception ex)
        {
            FlushLog();
            ShowNotice(ex.Message, InfoBarSeverity.Error);
            StatusText = "Scan failed.";
        }
        finally
        {
            timer.Stop();
            runningScan?.Dispose();
            runningScan = null;
            IsScanning = false;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
        : $"{elapsed.TotalSeconds:F1}s";

    [RelayCommand]
    private void Cancel()
    {
        runningScan?.Cancel();
    }

    [RelayCommand]
    private void CopyResults()
    {
        if (OpenPorts.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(", ", FilteredPorts.Select(p => p.Port)));
        Clipboard.SetContent(package);
        StatusText = $"Copied {PortCountTitle} to the clipboard.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (OpenPorts.Count == 0)
        {
            ShowNotice("Nothing to export yet — run a scan first.", InfoBarSeverity.Warning);
            return;
        }

        string host = resultTarget ?? IpAddress.Trim();
        string csv = FileSaver.ToCsv(
            FilteredPorts.Select(p => new[] { host, p.Port.ToString(), p.Service }),
            new[] { "ip_address", "port", "service" });

        string? path = SaveFileAsync is not null
            ? await SaveFileAsync("ports", ".csv", csv)
            : await ResultExporter.SaveTextAsync("ports", ".csv", csv);

        StatusText = path is null ? "Export cancelled." : $"Saved {PortCountTitle} to {path}.";
    }

    // Helpers -----------------------------------------------------------------

    public string LogLineCountText => log.LineCountText;

    private void AppendLogLine(string line)
    {
        if (log.Append(line))
        {
            LogText = log.Text;
            OnPropertyChanged(nameof(LogLineCountText));
        }
    }

    private void FlushLog()
    {
        LogText = log.Flush();
        OnPropertyChanged(nameof(LogLineCountText));
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (string.IsNullOrEmpty(LogText))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(LogText);
        Clipboard.SetContent(package);
        StatusText = $"Copied engine log ({LogLineCountText}) to the clipboard.";
    }

    /// <summary>
    /// Loads a target IP, restoring any previously scanned open ports from history
    /// or resetting to an empty ready state if this IP has not been scanned yet.
    /// </summary>
    public void LoadTargetIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        string cleanIp = ip.Trim();
        IpAddress = cleanIp;

        OpenPorts.Clear();
        log.Clear();
        LogText = string.Empty;
        OnPropertyChanged(nameof(LogLineCountText));

        if (ScanHistoryService.TryGet(cleanIp, out var history) && history != null)
        {
            foreach (var p in history.GetPortResults())
            {
                OpenPorts.Add(p);
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

    private void ShowNotice(string text, InfoBarSeverity severity)
    {
        NoticeText = text;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    private void HideNotice() => IsNoticeOpen = false;

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

        try
        {
            _ = await scanner.GetVersionAsync(CancellationToken.None);
            IsEngineMissing = false;
        }
        catch (Exception ex)
        {
            IsEngineMissing = true;
            ShowNotice($"Engine not found: {ex.Message}", InfoBarSeverity.Error);
        }
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
