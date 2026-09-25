using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// Host discovery page: runs <c>ns -s</c> and streams live hosts.
/// Progress&lt;T&gt; callbacks are created on the UI thread so results can
/// update the lists directly without dispatcher calls.
/// </summary>
public sealed partial class DiscoveryViewModel : ScanViewModelBase
{
    private readonly List<LocalSubnet> localSubnets = new();
    private readonly Dictionary<string, HostResult> hostsByIp = new();
    private readonly FilteredSortedView<HostResult> hostView = new(ByIp);

    /// <summary>True once a scan has finished (or been cancelled) this session.</summary>
    private bool hasScanned;

    /// <summary>Above this many hosts the UI asks for confirmation.</summary>
    public const long LargeScanConfirmThreshold = 4096;

    /// <summary>Above this the engine would spawn an unreasonable number of probes.</summary>
    public const long MaxScanHostsAllowed = 65534; // /16 usable

    public DiscoveryViewModel() : this(new NetScannerService())
    {
    }

    public DiscoveryViewModel(INetScannerService scanner) : base(scanner)
    {
        StatusText = "Enter a subnet to start.";
        VisibleHosts.CollectionChanged += (_, _) => NotifyEmptyStateChanged();

        ScanHistoryService.HistoryUpdated += (ip, history) =>
        {
            if (hostsByIp.TryGetValue(ip, out HostResult? host))
            {
                host.UpdateScannedPorts(history.SummaryText);
            }
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            UpdateSuggestions(string.Empty);
            foreach (HostResult host in Hosts)
            {
                host.UpdateScannedPorts(null);
            }
        };
    }

    // Results ----------------------------------------------------------------

    /// <summary>Every host found by the last scan, in arrival order.</summary>
    public IReadOnlyList<HostResult> Hosts => hostView.Source;

    /// <summary>Hosts matching the filter, in the chosen order. Bind the list to this.</summary>
    public ObservableCollection<HostResult> VisibleHosts => hostView.View;

    public int HostCount => Hosts.Count;

    public string HostCountTitle => HostCount == 1 ? "1 host" : $"{HostCount:N0} hosts";

    private void AddHost(HostResult host)
    {
        if (!hostsByIp.TryAdd(host.IpAddress, host))
        {
            return;
        }

        if (ScanHistoryService.TryGet(host.IpAddress, out var history) && history != null)
        {
            host.UpdateScannedPorts(history.SummaryText);
        }

        hostView.Add(host);
        OnPropertyChanged(nameof(HostCount));
        OnPropertyChanged(nameof(HostCountTitle));
    }

    private void ClearHosts()
    {
        hostsByIp.Clear();
        hostView.Clear();
        OnPropertyChanged(nameof(HostCount));
        OnPropertyChanged(nameof(HostCountTitle));
    }

    [ObservableProperty]
    private string filterText = string.Empty;

    partial void OnFilterTextChanged(string value)
    {
        string f = value.Trim();
        hostView.SetFilter(f.Length == 0 ? _ => true : h => h.Matches(f));
    }

    /// <summary>0 = by IP address, 1 = by name, 2 = by time found.</summary>
    [ObservableProperty]
    private int sortIndex;

    partial void OnSortIndexChanged(int value) => hostView.SetComparer(value switch
    {
        1 => ByName,
        2 => ByTimeFound,
        _ => ByIp,
    });

    private static readonly IComparer<HostResult> ByIp =
        Comparer<HostResult>.Create((a, b) => IpToSortKey(a.IpAddress).CompareTo(IpToSortKey(b.IpAddress)));

    /// <summary>Named devices first (alphabetically), then the rest by IP.</summary>
    private static readonly IComparer<HostResult> ByName = Comparer<HostResult>.Create((a, b) =>
    {
        int named = (a.Hostname is null).CompareTo(b.Hostname is null);
        if (named != 0)
        {
            return named;
        }

        int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Hostname, b.Hostname);
        return byName != 0 ? byName : ByIp.Compare(a, b);
    });

    private static readonly IComparer<HostResult> ByTimeFound =
        Comparer<HostResult>.Create((a, b) => a.FoundAt.CompareTo(b.FoundAt));

    internal static uint IpToSortKey(string ip)
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

    partial void OnUsePingFallbackChanged(bool value) => AppSettings.SetBool(AppSettings.DiscoveryUsePing, value);

    /// <summary>Resolve hostnames, MACs and manufacturers (<c>--resolve</c>).</summary>
    [ObservableProperty]
    private bool identifyDevices;

    partial void OnIdentifyDevicesChanged(bool value) => AppSettings.SetBool(AppSettings.DiscoveryResolve, value);

    /// <summary>False when the engine in use predates <c>--resolve</c> (e.g. an old ns on PATH).</summary>
    [ObservableProperty]
    private bool canIdentifyDevices = true;

    [ObservableProperty]
    private string engineVersion = "…";

    /// <summary>First-run guidance banner. Dismissed permanently via <see cref="DismissFirstRunTip"/>.</summary>
    [ObservableProperty]
    private bool isFirstRunTipOpen;

    public void DismissFirstRunTip()
    {
        IsFirstRunTipOpen = false;
        AppSettings.SetBool(AppSettings.SeenTeachingTip, true);
    }

    /// <summary>Subnet box suggestions: this machine's networks, then recent scans.</summary>
    public ObservableCollection<SubnetSuggestion> SubnetSuggestions { get; } = new();

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

    // Empty state ------------------------------------------------------------

    /// <summary>Shown when idle and the list is empty: nothing yet, nothing found, or nothing matching.</summary>
    public bool ShowEmptyState => !IsScanning && VisibleHosts.Count == 0;

    private bool IsFilteredOut => HostCount > 0;

    public string EmptyStateGlyph => IsFilteredOut ? "" : hasScanned ? "" : "";

    public string EmptyStateTitle => IsFilteredOut ? "No matching hosts"
        : hasScanned ? "No hosts found"
        : "Ready to discover hosts";

    public string EmptyStateMessage => IsFilteredOut
        ? $"None of the {HostCountTitle} match “{FilterText.Trim()}”."
        : hasScanned
            ? "Nothing answered on this subnet. Check the subnet, or try the ICMP ping method."
            : "Pick or enter a subnet and press Scan to find live devices on your network.";

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
        UpdateEngineCommand.NotifyCanExecuteChanged();
        NotifyEmptyStateChanged();
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
        IdentifyDevices = AppSettings.GetBool(AppSettings.DiscoveryResolve, true);
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

        if (await ProbeEngineAsync() is not { } version)
        {
            EngineVersion = "missing";
            return;
        }

        EngineVersion = version;
        CanIdentifyDevices = (await Scanner.GetCapabilitiesAsync(CancellationToken.None)).Resolve;

        // Fire-and-forget: a slow/offline network must never block startup.
        _ = CheckEngineUpdateAsync();
    }

    /// <summary>
    /// Updates all discovered hosts with any port scan history recorded for them.
    /// </summary>
    public void RefreshHistoryForHosts()
    {
        foreach (HostResult host in Hosts)
        {
            host.UpdateScannedPorts(
                ScanHistoryService.TryGet(host.IpAddress, out var history) ? history?.SummaryText : null);
        }
    }

    // Engine update ----------------------------------------------------------

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

        // Only offer what UpdateAsync will accept: same major version and a
        // published checksum. Anything else arrives with an app update.
        if (latest is not null &&
            EngineUpdater.IsNewerThan(latest.Tag, EngineVersion) &&
            EngineUpdater.IsCompatible(latest.Tag) &&
            latest.Sha256 is not null)
        {
            pendingRelease = latest;
            UpdateAvailableText = $"ns {latest.Tag} is available (you have {EngineVersion}).";
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

        try
        {
            string installed = await EngineUpdater.UpdateAsync(
                pendingRelease, new Progress<string>(AppendLogLine), CancellationToken.None);

            EngineVersion = installed;
            IsEngineMissing = false;
            IsUpdateAvailable = false;
            pendingRelease = null;
            CanIdentifyDevices = (await Scanner.GetCapabilitiesAsync(CancellationToken.None)).Resolve;
            StatusText = $"Engine updated to ns {installed}.";
        }
        catch (Exception ex)
        {
            ShowNotice($"Engine update failed: {ex.Message}", InfoBarSeverity.Error);
            StatusText = "Engine update failed.";
        }
        finally
        {
            FlushLog();
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

        string target = Subnet.Trim();
        long hostCount = NetworkValidation.CountUsableHosts(target);
        if (hostCount > LargeScanConfirmThreshold && ConfirmLargeScanAsync is not null &&
            !await ConfirmLargeScanAsync($"This will probe {hostCount:N0} hosts and may take a while. Continue?"))
        {
            StatusText = "Large scan not started.";
            return;
        }

        ClearHosts();
        AppSettings.SetString(AppSettings.DiscoverySubnet, target);
        AppSettings.PushRecent(AppSettings.DiscoveryRecent, target);
        UpdateSuggestions(string.Empty);

        var options = new SubnetScanOptions(UsePingFallback, IdentifyDevices && CanIdentifyDevices);

        // Created here on the UI thread: callbacks arrive on the UI thread.
        var hostProgress = new Progress<HostResult>(host =>
        {
            AddHost(host);
            RefreshProgress();
        });
        var detailProgress = new Progress<HostDetail>(detail =>
        {
            if (hostsByIp.TryGetValue(detail.IpAddress, out HostResult? host))
            {
                host.ApplyDetail(detail);
            }
        });

        await RunScanAsync(
            elapsed => $"Scanning {target} • {FormatElapsed(elapsed)} • {HostCountTitle} found…",
            async (token, stopwatch) =>
            {
                SubnetScanResult result = await Scanner.ScanSubnetAsync(
                    target, options, hostProgress, detailProgress, new Progress<string>(AppendLogLine), token);

                string total = FormatElapsed(stopwatch.Elapsed);
                StatusText = result.Hosts.Count == 0
                    ? $"No hosts found on {target} ({total})."
                    : $"{HostCountTitle} found on {target} in {total}.";
            },
            elapsed => $"Cancelled after {FormatElapsed(elapsed)} — {HostCountTitle} found.");

        hasScanned = true;

        // Names arrive after the sweep; filter and "Sort by name" need them.
        hostView.Refresh();
        NotifyEmptyStateChanged();
    }

    [RelayCommand]
    private void CopyResults()
    {
        if (VisibleHosts.Count > 0)
        {
            CopyToClipboard(string.Join(Environment.NewLine, VisibleHosts.Select(h => h.IpAddress)),
                $"Copied {VisibleHosts.Count:N0} IP addresses to the clipboard.");
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (VisibleHosts.Count == 0)
        {
            ShowNotice("Nothing to export yet — run a scan first.", InfoBarSeverity.Warning);
            return;
        }

        string csv = Csv.Format(
            VisibleHosts.Select(h => new[]
            {
                h.IpAddress, h.Hostname ?? string.Empty, h.MacAddress ?? string.Empty, h.Vendor ?? string.Empty,
                h.Source, h.FoundAt.ToString("s"), h.PortsSummary ?? string.Empty,
            }),
            new[] { "ip_address", "hostname", "mac_address", "vendor", "source", "found_at", "ports" });

        await SaveCsvAsync("hosts", csv, $"{VisibleHosts.Count:N0} hosts");
    }
}
