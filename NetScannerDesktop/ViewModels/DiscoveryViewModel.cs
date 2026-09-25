using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
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
/// Host discovery page: runs <c>ns -s</c> and streams live hosts.
/// Progress&lt;T&gt; callbacks are created on the UI thread so results can
/// update the lists directly without dispatcher calls.
/// </summary>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly INetScannerService scanner;
    private readonly EngineLog log = new();
    private CancellationTokenSource? runningScan;

    /// <summary>Above this many hosts the UI asks for confirmation.</summary>
    public const long LargeScanConfirmThreshold = 4096;

    /// <summary>Above this the engine would spawn an unreasonable number of probes.</summary>
    public const long MaxScanHostsAllowed = 65534; // /16 usable

    /// <summary>Set by the view: (suggestedName, extension, content) -> saved path or null.</summary>
    public Func<string, string, string, Task<string?>>? SaveFileAsync { get; set; }

    /// <summary>Set by the view to show a ContentDialog for large scans.</summary>
    public Func<string, Task<bool>>? ConfirmLargeScanAsync { get; set; }

    public DiscoveryViewModel() : this(new NetScannerService())
    {
    }

    public DiscoveryViewModel(INetScannerService scanner)
    {
        this.scanner = scanner;
        Hosts = new ObservableCollection<HostResult>();
        LocalSubnets = new ObservableCollection<LocalSubnet>();

        // Titles and empty-state hints follow the list automatically.
        Hosts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HostCountTitle));
            OnPropertyChanged(nameof(FilteredHosts));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        };

        ScanHistoryService.HistoryUpdated += (ip, history) =>
        {
            var target = Hosts.FirstOrDefault(h => string.Equals(h.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
            target?.UpdateScannedPorts(history.SummaryText);
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            RefreshRecent();
            foreach (var h in Hosts)
            {
                h.UpdateScannedPorts(null);
            }
        };
    }

    // Form -----------------------------------------------------------------

    [ObservableProperty]
    private string subnet = string.Empty;

    [ObservableProperty]
    private bool usePingFallback;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateVisibility))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEngineCommand))]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = "Enter a subnet to start.";

    [ObservableProperty]
    private string logText = string.Empty;

    [ObservableProperty]
    private string engineVersion = "…";

    [ObservableProperty]
    private bool isNoticeOpen;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity noticeSeverity = InfoBarSeverity.Informational;

    /// <summary>First-run guidance banner. Dismissed permanently via <see cref="DismissFirstRunTip"/>.</summary>
    [ObservableProperty]
    private bool isFirstRunTipOpen;

    public void DismissFirstRunTip()
    {
        IsFirstRunTipOpen = false;
        AppSettings.SetBool(AppSettings.SeenTeachingTip, true);
    }

    public ObservableCollection<HostResult> Hosts { get; }

    public ObservableCollection<LocalSubnet> LocalSubnets { get; }

    public ObservableCollection<string> RecentSubnets { get; } = new();

    public string HostCountTitle => Hosts.Count == 1 ? "1 host" : $"{Hosts.Count} hosts";

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredHosts));

    [ObservableProperty]
    private bool sortByIp = true;

    partial void OnSortByIpChanged(bool value) => OnPropertyChanged(nameof(FilteredHosts));

    public System.Collections.Generic.IEnumerable<HostResult> FilteredHosts
    {
        get
        {
            System.Collections.Generic.IEnumerable<HostResult> query = Hosts;

            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                string f = FilterText.Trim();
                query = query.Where(h =>
                    h.IpAddress.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    h.Source.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    (h.PortsSummary != null && h.PortsSummary.Contains(f, StringComparison.OrdinalIgnoreCase)));
            }

            query = SortByIp
                ? query.OrderBy(h => IpToSortKey(h.IpAddress))
                : query.OrderBy(h => h.FoundAt);

            return query.ToList();
        }
    }

    private static uint IpToSortKey(string ip)
    {
        string[] parts = ip.Split('.');
        if (parts.Length != 4)
        {
            return 0;
        }

        uint key = 0;
        foreach (string part in parts)
        {
            if (!byte.TryParse(part, out byte b))
            {
                return 0;
            }

            key = (key << 8) | b;
        }

        return key;
    }

    public string HostEstimateText
    {
        get
        {
            if (!NetworkValidation.TryParseCidr(Subnet, out _))
            {
                return "Enter CIDR like 192.168.1.0/24.";
            }

            try
            {
                long n = NetworkValidation.CountUsableHosts(Subnet.Trim());
                return $"{n:N0} {(n == 1 ? "host" : "hosts")} will be probed.";
            }
            catch
            {
                return "Enter CIDR like 192.168.1.0/24.";
            }
        }
    }

    /// <summary>The network picked from the known-networks dropdown, if any.</summary>
    [ObservableProperty]
    private LocalSubnet? selectedSuggestion;

    partial void OnSelectedSuggestionChanged(LocalSubnet? value)
    {
        // The dropdown acts as a fill-in helper: copy the choice over and
        // reset so it is ready for the next pick.
        if (value is not null)
        {
            Subnet = value.Cidr;
            SelectedSuggestion = null;
        }
    }

    [ObservableProperty]
    private string? selectedRecent;

    partial void OnSelectedRecentChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Subnet = value;
            SelectedRecent = null;
        }
    }

    partial void OnUsePingFallbackChanged(bool value) => AppSettings.SetBool(AppSettings.DiscoveryUsePing, value);

    /// <summary>Only show the progress track while a scan is running.</summary>
    public Visibility ScanProgressVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Show the empty-state hint only when idle with no results.</summary>
    public bool ShowEmptyState => !IsScanning && Hosts.Count == 0;

    public Visibility EmptyStateVisibility => ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;

    public string SubnetErrorText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Subnet))
            {
                return string.Empty;
            }

            if (!NetworkValidation.TryParseCidr(Subnet, out string error))
            {
                return error;
            }

            try
            {
                long n = NetworkValidation.CountUsableHosts(Subnet.Trim());
                if (n > MaxScanHostsAllowed)
                {
                    return $"Too large ({n:N0} hosts). Use /16 or smaller.";
                }
            }
            catch
            {
            }

            return string.Empty;
        }
    }

    public Visibility SubnetErrorVisibility =>
        string.IsNullOrEmpty(SubnetErrorText) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanScan => !IsScanning
        && !string.IsNullOrWhiteSpace(Subnet)
        && string.IsNullOrEmpty(SubnetErrorText)
        && !IsEngineMissing;

    [ObservableProperty]
    private bool isEngineMissing;

    partial void OnSubnetChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HostEstimateText));
        OnPropertyChanged(nameof(SubnetErrorText));
        OnPropertyChanged(nameof(SubnetErrorVisibility));
    }

    partial void OnIsScanningChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    partial void OnIsEngineMissingChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    // Startup ---------------------------------------------------------------

    private bool loaded;

    /// <summary>
    /// One-time startup: restore the form, probe the engine, check for an
    /// update. The page is cached, so navigating back must not repeat this
    /// (it would overwrite what the user typed and re-open dismissed bars).
    /// </summary>
    public async Task LoadAsync()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        IsFirstRunTipOpen = !AppSettings.GetBool(AppSettings.SeenTeachingTip);

        if (LocalSubnets.Count == 0)
        {
            foreach (LocalSubnet found in LocalNetwork.GetLocalSubnets())
            {
                LocalSubnets.Add(found);
            }
        }

        // Restore last session: saved subnet wins, else first local net.
        string saved = AppSettings.GetString(AppSettings.DiscoverySubnet);
        UsePingFallback = AppSettings.GetBool(AppSettings.DiscoveryUsePing);
        RecentSubnets.Clear();
        foreach (string recent in AppSettings.GetRecent(AppSettings.DiscoveryRecent))
        {
            RecentSubnets.Add(recent);
        }

        if (!string.IsNullOrWhiteSpace(saved))
        {
            Subnet = saved;
        }
        else if (LocalSubnets.Count > 0 && string.IsNullOrWhiteSpace(Subnet))
        {
            Subnet = LocalSubnets[0].Cidr;
        }

        try
        {
            EngineVersion = await scanner.GetVersionAsync(CancellationToken.None);
            IsEngineMissing = false;
        }
        catch (Exception ex)
        {
            EngineVersion = "missing";
            IsEngineMissing = true;
            ShowNotice($"Engine not found: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        // Fire-and-forget: a slow/offline network must never block startup.
        _ = CheckEngineUpdateAsync();
    }

    /// <summary>
    /// Updates all discovered hosts with any port scan history recorded for them.
    /// </summary>
    public void RefreshHistoryForHosts()
    {
        foreach (var host in Hosts)
        {
            if (ScanHistoryService.TryGet(host.IpAddress, out var history) && history != null)
            {
                host.UpdateScannedPorts(history.SummaryText);
            }
            else
            {
                host.UpdateScannedPorts(null);
            }
        }
    }

    /// <summary>Latest release seen by the last check (null = unknown).</summary>
    [ObservableProperty]
    private string latestEngineVersion = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateEngineCommand))]
    private bool isUpdateAvailable;

    [ObservableProperty]
    private string updateAvailableText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEngineCommand))]
    private bool isUpdating;

    public string UpdateButtonText => IsUpdating ? "Updating…" : "Update";

    public bool CanUpdateEngine => IsUpdateAvailable && !IsUpdating && !IsScanning;

    private EngineRelease? pendingRelease;

    private async Task CheckEngineUpdateAsync()
    {
        // Null when offline or on an API hiccup: stay silent, keep the current engine.
        EngineRelease? latest = await EngineUpdater.GetLatestReleaseAsync();
        if (latest is null)
        {
            return;
        }

        LatestEngineVersion = latest.Tag;

        // Only offer what UpdateAsync will accept: same major version and a
        // published checksum. Anything else arrives with an app update.
        if (EngineUpdater.IsNewerThan(latest.Tag, EngineVersion) &&
            EngineUpdater.IsCompatible(latest.Tag) &&
            latest.Sha256 is not null)
        {
            pendingRelease = latest;
            UpdateAvailableText =
                $"ns {latest.Tag} is available (you have {EngineVersion}).";
            IsUpdateAvailable = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateEngine))]
    private async Task UpdateEngineAsync()
    {
        if (pendingRelease is null)
        {
            return;
        }

        IsUpdating = true;
        StatusText = $"Updating engine to {pendingRelease.Tag}…";
        var logProgress = new Progress<string>(AppendLogLine);

        try
        {
            string installed = await EngineUpdater.UpdateAsync(
                pendingRelease, logProgress, CancellationToken.None);
            FlushLog();

            EngineVersion = installed;
            IsEngineMissing = false;
            IsUpdateAvailable = false;
            pendingRelease = null;
            StatusText = $"Engine updated to ns {installed}.";
        }
        catch (OperationCanceledException)
        {
            FlushLog();
            StatusText = "Engine update cancelled.";
        }
        catch (Exception ex)
        {
            FlushLog();
            ShowNotice($"Engine update failed: {ex.Message}", InfoBarSeverity.Error);
            StatusText = "Engine update failed.";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // Commands ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!NetworkValidation.TryParseCidr(Subnet, out string error))
        {
            ShowNotice(error, InfoBarSeverity.Warning);
            return;
        }

        long hostCount;
        try
        {
            hostCount = NetworkValidation.CountUsableHosts(Subnet.Trim());
        }
        catch
        {
            ShowNotice("Could not compute host count for that subnet.", InfoBarSeverity.Warning);
            return;
        }

        if (hostCount > MaxScanHostsAllowed)
        {
            ShowNotice(
                $"That subnet has {hostCount:N0} hosts — too large. Use /16 or smaller (max {MaxScanHostsAllowed:N0} hosts).",
                InfoBarSeverity.Warning);
            return;
        }

        if (hostCount > LargeScanConfirmThreshold && ConfirmLargeScanAsync is not null)
        {
            bool ok = await ConfirmLargeScanAsync(
                $"This will probe {hostCount:N0} hosts and may take a while. Continue?");
            if (!ok)
            {
                StatusText = "Cancelled — large scan not started.";
                return;
            }
        }

        HideNotice();
        Hosts.Clear();
        log.Clear();
        LogText = string.Empty;
        OnPropertyChanged(nameof(LogLineCountText));

        string trimmedSubnet = Subnet.Trim();
        AppSettings.SetString(AppSettings.DiscoverySubnet, trimmedSubnet);
        AppSettings.PushRecent(AppSettings.DiscoveryRecent, trimmedSubnet);
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
            StatusText = $"Scanning {trimmedSubnet} • {timeStr} • {HostCountTitle} found…";
        };
        timer.Start();

        StatusText = $"Scanning {trimmedSubnet}…";

        // Created here on the UI thread: callbacks arrive on the UI thread.
        var hostProgress = new Progress<HostResult>(host =>
        {
            if (ScanHistoryService.TryGet(host.IpAddress, out var history) && history != null)
            {
                host.UpdateScannedPorts(history.SummaryText);
            }

            Hosts.Add(host);
            var elapsed = stopwatch.Elapsed;
            string timeStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
                : $"{elapsed.Seconds}.{elapsed.Milliseconds / 100}s";
            StatusText = $"Scanning {trimmedSubnet} • {timeStr} • {HostCountTitle} found…";
        });
        var logProgress = new Progress<string>(AppendLogLine);

        try
        {
            SubnetScanResult result = await scanner.ScanSubnetAsync(
                Subnet.Trim(), UsePingFallback, hostProgress, logProgress, runningScan.Token);

            FlushLog();
            var totalElapsed = stopwatch.Elapsed;
            string totalStr = totalElapsed.TotalMinutes >= 1
                ? $"{(int)totalElapsed.TotalMinutes}m {totalElapsed.Seconds:D2}s"
                : $"{totalElapsed.TotalSeconds:F1}s";

            if (result.Summary is not null)
            {
                StatusText = $"{result.Summary.RawLine} ({totalStr} total).";
            }
            else if (result.Hosts.Count == 0)
            {
                StatusText = $"No hosts found ({totalStr}).";
            }
            else
            {
                StatusText = $"{HostCountTitle} found in {totalStr}.";
            }
        }
        catch (OperationCanceledException)
        {
            FlushLog();
            StatusText = $"Cancelled at {stopwatch.Elapsed.TotalSeconds:F1}s — {HostCountTitle} found.";
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
        if (Hosts.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, Hosts.Select(h => h.IpAddress)));
        Clipboard.SetContent(package);
        StatusText = $"Copied {HostCountTitle} to the clipboard.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Hosts.Count == 0)
        {
            ShowNotice("Nothing to export yet — run a scan first.", InfoBarSeverity.Warning);
            return;
        }

        string csv = FileSaver.ToCsv(
            Hosts.Select(h => new[] { h.IpAddress, h.Source, h.FoundAt.ToString("HH:mm:ss") }),
            new[] { "ip_address", "source", "found_at" });

        string? path = SaveFileAsync is not null
            ? await SaveFileAsync("hosts", ".csv", csv)
            : await ResultExporter.SaveTextAsync("hosts", ".csv", csv);

        if (path is null)
        {
            StatusText = "Export cancelled.";
            return;
        }

        StatusText = $"Saved {HostCountTitle} to {path}.";
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

    private void ShowNotice(string text, InfoBarSeverity severity)
    {
        NoticeText = text;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    private void HideNotice() => IsNoticeOpen = false;

    private void RefreshRecent()
    {
        RecentSubnets.Clear();
        foreach (string recent in AppSettings.GetRecent(AppSettings.DiscoveryRecent))
        {
            RecentSubnets.Add(recent);
        }
    }
}
