using System;
using System.Collections.ObjectModel;
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
        OpenPorts = new ObservableCollection<PortResult>();

        // Titles and empty-state hints follow the list automatically.
        OpenPorts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PortCountTitle));
            OnPropertyChanged(nameof(FilteredPorts));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            if (!IsScanning)
            {
                OpenPorts.Clear();
                StatusText = "History cleared.";
            }
        };
    }

    // Form -----------------------------------------------------------------

    [ObservableProperty]
    private string ipAddress = string.Empty;

    [ObservableProperty]
    private string portRange = "1-1024";

    [ObservableProperty]
    private string timeoutMs = "500";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateVisibility))]
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

    public ObservableCollection<PortResult> OpenPorts { get; }

    public ObservableCollection<string> RecentIps { get; } = new();

    public string PortCountTitle => OpenPorts.Count == 1 ? "1 open port" : $"{OpenPorts.Count} open ports";

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredPorts));

    public System.Collections.Generic.IEnumerable<PortResult> FilteredPorts
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilterText))
            {
                return OpenPorts.OrderBy(p => p.Port);
            }

            string f = FilterText.Trim();
            return OpenPorts
                .Where(p => p.Port.ToString().Contains(f, StringComparison.OrdinalIgnoreCase)
                    || p.Service.Contains(f, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Port);
        }
    }

    public string PortEstimateText
    {
        get
        {
            if (!NetworkValidation.TryParsePortRange(PortRange, out int start, out int end, out _))
            {
                return "Enter a range like 1-1024.";
            }

            int n = Math.Abs(end - start) + 1;
            return $"{n:N0} {(n == 1 ? "port" : "ports")} will be probed.";
        }
    }

    /// <summary>Show the empty-state hint only when idle with no results.</summary>
    public bool ShowEmptyState => !IsScanning && OpenPorts.Count == 0;

    public Visibility EmptyStateVisibility => ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only show the progress track while a scan is running.</summary>
    public Visibility ScanProgressVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public string IpErrorText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                return string.Empty;
            }

            return NetworkValidation.IsValidIpv4(IpAddress)
                ? string.Empty
                : "Enter a valid IPv4 address, e.g. 192.168.1.1.";
        }
    }

    public Visibility IpErrorVisibility =>
        string.IsNullOrEmpty(IpErrorText) ? Visibility.Collapsed : Visibility.Visible;

    public string PortRangeErrorText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PortRange))
            {
                return string.Empty;
            }

            return NetworkValidation.TryParsePortRange(PortRange, out _, out _, out string error)
                ? string.Empty
                : error;
        }
    }

    public Visibility PortRangeErrorVisibility =>
        string.IsNullOrEmpty(PortRangeErrorText) ? Visibility.Collapsed : Visibility.Visible;

    public string TimeoutErrorText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TimeoutMs))
            {
                return string.Empty;
            }

            return NetworkValidation.TryParseTimeout(TimeoutMs, out _, out string error)
                ? string.Empty
                : error;
        }
    }

    public Visibility TimeoutErrorVisibility =>
        string.IsNullOrEmpty(TimeoutErrorText) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    private bool isEngineMissing;

    partial void OnIsEngineMissingChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    public bool CanScan => !IsScanning
        && !IsEngineMissing
        && !string.IsNullOrWhiteSpace(IpAddress)
        && !string.IsNullOrWhiteSpace(PortRange)
        && string.IsNullOrEmpty(IpErrorText)
        && string.IsNullOrEmpty(PortRangeErrorText)
        && string.IsNullOrEmpty(TimeoutErrorText);

    partial void OnIpAddressChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IpErrorText));
        OnPropertyChanged(nameof(IpErrorVisibility));
    }

    partial void OnPortRangeChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PortEstimateText));
        OnPropertyChanged(nameof(PortRangeErrorText));
        OnPropertyChanged(nameof(PortRangeErrorVisibility));
    }

    partial void OnTimeoutMsChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TimeoutErrorText));
        OnPropertyChanged(nameof(TimeoutErrorVisibility));
    }

    partial void OnIsScanningChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    // Commands ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!NetworkValidation.IsValidIpv4(IpAddress))
        {
            ShowNotice("Enter a valid IPv4 address, e.g. 192.168.1.1.", InfoBarSeverity.Warning);
            return;
        }

        if (!NetworkValidation.TryParsePortRange(PortRange, out int start, out int end, out string error))
        {
            ShowNotice(error, InfoBarSeverity.Warning);
            return;
        }

        if (!NetworkValidation.TryParseTimeout(TimeoutMs, out int? timeout, out string timeoutError))
        {
            ShowNotice(timeoutError, InfoBarSeverity.Warning);
            return;
        }

        int portCount = Math.Abs(end - start) + 1;
        if (portCount > LargePortScanConfirmThreshold && ConfirmLargeScanAsync is not null)
        {
            bool ok = await ConfirmLargeScanAsync(
                $"This will probe {portCount:N0} ports and may take a while. Continue?");
            if (!ok)
            {
                StatusText = "Cancelled — large scan not started.";
                return;
            }
        }

        HideNotice();
        OpenPorts.Clear();
        log.Clear();
        LogText = string.Empty;
        OnPropertyChanged(nameof(LogLineCountText));

        string trimmedIp = IpAddress.Trim();
        AppSettings.SetString(AppSettings.PortIp, trimmedIp);
        AppSettings.SetString(AppSettings.PortRange, PortRange.Trim());
        AppSettings.SetString(AppSettings.PortTimeout, TimeoutMs.Trim());
        AppSettings.PushRecent(AppSettings.PortRecent, trimmedIp);
        RefreshRecent();

        IsScanning = true;
        runningScan = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (!IsScanning) return;
            var elapsed = stopwatch.Elapsed;
            string timeStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
                : $"{elapsed.Seconds}.{elapsed.Milliseconds / 100}s";
            StatusText = $"Scanning {trimmedIp} • {timeStr} • {PortCountTitle}…";
        };
        timer.Start();

        StatusText = $"Scanning {trimmedIp} ports {start}-{end}…";

        var portProgress = new Progress<int>(port =>
        {
            if (OpenPorts.Any(p => p.Port == port))
            {
                return;
            }

            OpenPorts.Add(new PortResult(port));
            SortPorts();
            var elapsed = stopwatch.Elapsed;
            string timeStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
                : $"{elapsed.Seconds}.{elapsed.Milliseconds / 100}s";
            StatusText = $"Scanning {trimmedIp} • {timeStr} • {PortCountTitle}…";
        });
        var logProgress = new Progress<string>(AppendLogLine);

        try
        {
            PortScanResult result = await scanner.ScanPortsAsync(
                IpAddress.Trim(), start, end, portProgress, logProgress, runningScan.Token, timeout);

            FlushLog();
            var totalElapsed = stopwatch.Elapsed;
            string totalStr = totalElapsed.TotalMinutes >= 1
                ? $"{(int)totalElapsed.TotalMinutes}m {totalElapsed.Seconds:D2}s"
                : $"{totalElapsed.TotalSeconds:F1}s";

            StatusText = result.OpenPorts.Count == 0
                ? $"No open ports on {IpAddress.Trim()} ({totalStr})."
                : $"{PortCountTitle} on {IpAddress.Trim()} ({totalStr}).";

            ScanHistoryService.Record(IpAddress.Trim(), PortRange, OpenPorts);
        }
        catch (OperationCanceledException)
        {
            FlushLog();
            StatusText = $"Cancelled at {stopwatch.Elapsed.TotalSeconds:F1}s — {PortCountTitle} found.";
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
        package.SetText(string.Join(", ", OpenPorts.OrderBy(p => p.Port).Select(p => p.Port)));
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

        string csv = FileSaver.ToCsv(
            OpenPorts.OrderBy(p => p.Port).Select(p => new[] { IpAddress.Trim(), p.Port.ToString(), p.Service }),
            new[] { "ip_address", "port", "service" });

        string? path = SaveFileAsync is not null
            ? await SaveFileAsync("ports", ".csv", csv)
            : await ResultExporter.SaveTextAsync("ports", ".csv", csv);

        if (path is null)
        {
            StatusText = "Export cancelled.";
            return;
        }

        StatusText = $"Saved {PortCountTitle} to {path}.";
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

    private void SortPorts()
    {
        var sorted = OpenPorts.OrderBy(p => p.Port).ToList();
        OpenPorts.Clear();
        foreach (PortResult port in sorted)
        {
            OpenPorts.Add(port);
        }
    }

    [ObservableProperty]
    private string? selectedRecentIp;

    partial void OnSelectedRecentIpChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            LoadTargetIp(value);
            SelectedRecentIp = null;
        }
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

            StatusText = history.OpenPortNumbers.Count == 0
                ? $"No open ports found on {cleanIp} (scanned at {history.ScannedAt:HH:mm:ss}). Press Scan to re-scan."
                : $"{history.OpenPortNumbers.Count} open port(s) from scan at {history.ScannedAt:HH:mm:ss}. Press Scan to re-scan.";
        }
        else
        {
            string defaultRange = AppSettings.GetString("defaults.portRange", "1-1024");
            if (!string.IsNullOrWhiteSpace(defaultRange))
            {
                PortRange = defaultRange;
            }
            StatusText = $"Ready to scan {cleanIp}. Press Scan to start.";
        }
    }

    private void ShowNotice(string text, InfoBarSeverity severity)
    {
        NoticeText = text;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    private void HideNotice() => IsNoticeOpen = false;

    /// <summary>
    /// Restore last session + recent IPs. Falls back to Settings defaults.
    /// </summary>
    public async Task LoadAsync()
    {
        string savedIp = AppSettings.GetString(AppSettings.PortIp);
        string savedRange = AppSettings.GetString(AppSettings.PortRange);
        string savedTimeout = AppSettings.GetString(AppSettings.PortTimeout);
        if (string.IsNullOrWhiteSpace(savedRange))
        {
            savedRange = AppSettings.GetString("defaults.portRange", "1-1024");
        }

        if (string.IsNullOrWhiteSpace(savedTimeout))
        {
            savedTimeout = AppSettings.GetString("defaults.timeoutMs", "500");
        }

        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            if (!string.IsNullOrWhiteSpace(savedIp))
            {
                LoadTargetIp(savedIp);
            }
            if (!string.IsNullOrWhiteSpace(savedRange))
            {
                PortRange = savedRange;
            }
        }

        if (!string.IsNullOrWhiteSpace(savedTimeout) && string.IsNullOrWhiteSpace(TimeoutMs))
        {
            TimeoutMs = savedTimeout;
        }

        RecentIps.Clear();
        foreach (string recent in AppSettings.GetRecent(AppSettings.PortRecent))
        {
            RecentIps.Add(recent);
        }

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
