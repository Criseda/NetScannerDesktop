using System;
using System.Collections.Generic;
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
/// Host discovery page: runs <c>ns -s</c> and streams live hosts.
/// Progress&lt;T&gt; callbacks are created on the UI thread so results can
/// update the lists directly without dispatcher calls.
/// </summary>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly INetScannerService scanner;
    private readonly EngineLog log = new();
    private readonly List<LocalSubnet> localSubnets = new();
    private CancellationTokenSource? runningScan;

    /// <summary>True once a scan has finished (or been cancelled) this session.</summary>
    private bool hasScanned;

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

        // Titles and empty-state hints follow the list automatically.
        Hosts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HostCountTitle));
            OnPropertyChanged(nameof(FilteredHosts));
            NotifyEmptyStateChanged();
        };

        ScanHistoryService.HistoryUpdated += (ip, history) =>
        {
            var target = Hosts.FirstOrDefault(h => string.Equals(h.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
            target?.UpdateScannedPorts(history.SummaryText);
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            UpdateSuggestions(string.Empty);
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
    [NotifyPropertyChangedFor(nameof(MethodIndex))]
    private bool usePingFallback;

    /// <summary>RadioButtons index: 0 = TCP + ARP, 1 = ICMP ping.</summary>
    public int MethodIndex
    {
        get => UsePingFallback ? 1 : 0;
        set => UsePingFallback = value == 1;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateEngineCommand))]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = string.Empty;

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

    public ObservableCollection<HostResult> Hosts { get; } = new();

    /// <summary>Subnet box suggestions: this machine's networks, then recent scans.</summary>
    public ObservableCollection<SubnetSuggestion> SubnetSuggestions { get; } = new();

    public string HostCountTitle => Hosts.Count == 1 ? "1 host" : $"{Hosts.Count:N0} hosts";

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredHosts));

    /// <summary>0 = by IP address, 1 = by time found.</summary>
    [ObservableProperty]
    private int sortIndex;

    partial void OnSortIndexChanged(int value) => OnPropertyChanged(nameof(FilteredHosts));

    public IEnumerable<HostResult> FilteredHosts
    {
        get
        {
            IEnumerable<HostResult> query = Hosts;

            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                string f = FilterText.Trim();
                query = query.Where(h =>
                    h.IpAddress.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    h.Source.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    (h.PortsSummary != null && h.PortsSummary.Contains(f, StringComparison.OrdinalIgnoreCase)));
            }

            query = SortIndex == 0
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
                return "CIDR notation, for example 192.168.1.0/24.";
            }

            long n = NetworkValidation.CountUsableHosts(Subnet.Trim());
            return $"{n:N0} {(n == 1 ? "host" : "hosts")} will be probed.";
        }
    }

    partial void OnUsePingFallbackChanged(bool value) => AppSettings.SetBool(AppSettings.DiscoveryUsePing, value);

    // Empty state ------------------------------------------------------------

    /// <summary>Show the empty-state hint only when idle with no results.</summary>
    public bool ShowEmptyState => !IsScanning && Hosts.Count == 0;

    public string EmptyStateGlyph => hasScanned ? "" : "";

    public string EmptyStateTitle => hasScanned ? "No hosts found" : "Ready to discover hosts";

    public string EmptyStateMessage => hasScanned
        ? "Nothing answered on this subnet. Check the subnet, or try the ICMP ping method."
        : "Pick or enter a subnet and press Scan to find live devices on your network.";

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

            long n = NetworkValidation.CountUsableHosts(Subnet.Trim());
            return n > MaxScanHostsAllowed ? $"Too large ({n:N0} hosts). Use /16 or smaller." : string.Empty;
        }
    }

    public bool HasSubnetError => !string.IsNullOrEmpty(SubnetErrorText);

    public bool CanScan => !IsScanning
        && !string.IsNullOrWhiteSpace(Subnet)
        && !HasSubnetError
        && !IsEngineMissing;

    [ObservableProperty]
    private bool isEngineMissing;

    partial void OnSubnetChanged(string value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HostEstimateText));
        OnPropertyChanged(nameof(SubnetErrorText));
        OnPropertyChanged(nameof(HasSubnetError));
        if (!IsScanning && !hasScanned)
        {
            StatusText = IdleStatus();
        }
    }

    private string IdleStatus() => string.IsNullOrWhiteSpace(Subnet)
        ? "Enter a subnet to start."
        : "Ready. Press Scan or F5.";

    partial void OnIsEngineMissingChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    // Suggestions ------------------------------------------------------------

    /// <summary>
    /// Fills the subnet box dropdown. An empty filter lists everything, so
    /// focusing the box shows all known networks at once.
    /// </summary>
    public void UpdateSuggestions(string filter)
    {
        string f = filter.Trim();
        var items = localSubnets
            .Select(s => new SubnetSuggestion(s.Cidr, s.AdapterName))
            .Concat(AppSettings.GetRecent(AppSettings.DiscoveryRecent)
                .Select(r => new SubnetSuggestion(r, "Recent")))
            .GroupBy(s => s.Cidr)
            .Select(g => g.First())
            .Where(s => f.Length == 0 || s.Cidr.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                        s.Label.Contains(f, StringComparison.OrdinalIgnoreCase));

        SubnetSuggestions.Clear();
        foreach (SubnetSuggestion item in items)
        {
            SubnetSuggestions.Add(item);
        }
    }

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
        localSubnets.AddRange(LocalNetwork.GetLocalSubnets());
        UpdateSuggestions(string.Empty);

        // Restore last session: saved subnet wins, else first local net.
        UsePingFallback = AppSettings.GetBool(AppSettings.DiscoveryUsePing);
        string saved = AppSettings.GetString(AppSettings.DiscoverySubnet);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            Subnet = saved;
        }
        else if (localSubnets.Count > 0)
        {
            Subnet = localSubnets[0].Cidr;
        }

        StatusText = IdleStatus();

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
            host.UpdateScannedPorts(
                ScanHistoryService.TryGet(host.IpAddress, out var history) ? history?.SummaryText : null);
        }
    }

    // Engine update ----------------------------------------------------------

    /// <summary>Latest release seen by the last check (empty = unknown).</summary>
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

        long hostCount = NetworkValidation.CountUsableHosts(Subnet.Trim());
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
                StatusText = "Large scan not started.";
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
        UpdateSuggestions(string.Empty);

        IsScanning = true;
        runningScan = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        void ShowProgress() =>
            StatusText = $"Scanning {trimmedSubnet} • {FormatElapsed(stopwatch.Elapsed)} • {HostCountTitle} found…";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => ShowProgress();
        timer.Start();
        ShowProgress();

        // Created here on the UI thread: callbacks arrive on the UI thread.
        var hostProgress = new Progress<HostResult>(host =>
        {
            if (ScanHistoryService.TryGet(host.IpAddress, out var history) && history != null)
            {
                host.UpdateScannedPorts(history.SummaryText);
            }

            Hosts.Add(host);
            ShowProgress();
        });
        var logProgress = new Progress<string>(AppendLogLine);

        try
        {
            SubnetScanResult result = await scanner.ScanSubnetAsync(
                trimmedSubnet, UsePingFallback, hostProgress, logProgress, runningScan.Token);

            FlushLog();
            string total = FormatElapsed(stopwatch.Elapsed);
            StatusText = result.Hosts.Count == 0
                ? $"No hosts found on {trimmedSubnet} ({total})."
                : $"{HostCountTitle} found on {trimmedSubnet} in {total}.";
        }
        catch (OperationCanceledException)
        {
            FlushLog();
            StatusText = $"Cancelled after {FormatElapsed(stopwatch.Elapsed)} — {HostCountTitle} found.";
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
            hasScanned = true;
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
        if (Hosts.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, FilteredHosts.Select(h => h.IpAddress)));
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
            FilteredHosts.Select(h => new[] { h.IpAddress, h.Source, h.FoundAt.ToString("HH:mm:ss"), h.PortsSummary ?? string.Empty }),
            new[] { "ip_address", "source", "found_at", "ports" });

        string? path = SaveFileAsync is not null
            ? await SaveFileAsync("hosts", ".csv", csv)
            : await ResultExporter.SaveTextAsync("hosts", ".csv", csv);

        StatusText = path is null ? "Export cancelled." : $"Saved {HostCountTitle} to {path}.";
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
}
