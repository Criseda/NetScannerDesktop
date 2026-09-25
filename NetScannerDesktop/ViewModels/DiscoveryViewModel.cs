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

    public DiscoveryViewModel(INetScannerService scanner) : base(scanner, defaultSortColumn: "ip")
    {
        StatusText = "Enter a subnet to start.";
        VisibleHosts.CollectionChanged += (_, _) => NotifyEmptyStateChanged();

        ScanHistoryService.HistoryUpdated += (ip, history) =>
        {
            if (hostsByIp.TryGetValue(ip, out HostResult? host))
            {
                host.OpenPorts = history.OpenPorts;
                if (SortColumn == "ports")
                {
                    hostView.Refresh();
                }
            }
        };

        ScanHistoryService.HistoryCleared += () =>
        {
            UpdateSuggestions(string.Empty);
            foreach (HostResult host in Hosts)
            {
                host.OpenPorts = null;
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
            host.OpenPorts = history.OpenPorts;
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

    private static readonly IComparer<HostResult> ByIp =
        Comparer<HostResult>.Create((a, b) => IpToSortKey(a.IpAddress).CompareTo(IpToSortKey(b.IpAddress)));

    protected override void ApplySort() => hostView.SetComparer(HostComparer(SortColumn, SortDescending));

    /// <summary>
    /// Comparer for a Discovery table column. Blank cells sort last and
    /// ties go by IP, so unnamed hosts stay in address order at the end.
    /// Ports sort by how many are open; hosts never port scanned go last.
    /// </summary>
    internal static IComparer<HostResult> HostComparer(string column, bool descending) => column switch
    {
        "name" => TableSort.ByText<HostResult>(h => h.Hostname, descending, ByIp),
        "vendor" => TableSort.ByText<HostResult>(h => h.Vendor, descending, ByIp),
        "mac" => TableSort.ByText<HostResult>(h => h.MacAddress, descending, ByIp),
        "source" => TableSort.ByText<HostResult>(h => h.Source, descending, ByIp),
        "ports" => TableSort.ByValue<HostResult, int>(h => h.HasScannedPorts ? h.OpenPortCount : null, descending, ByIp),
        "found" => TableSort.ByValue<HostResult, DateTime>(h => h.FoundAt, descending, ByIp),
        _ => TableSort.ByValue<HostResult, uint>(h => IpToSortKey(h.IpAddress), descending),
    };

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
    [NotifyPropertyChangedFor(nameof(MethodIndex), nameof(MethodHint))]
    private bool usePingFallback;

    /// <summary>Method box index: 0 = TCP + ARP, 1 = ICMP ping.</summary>
    public int MethodIndex
    {
        get => UsePingFallback ? 1 : 0;
        set => UsePingFallback = value == 1;
    }

    public string MethodHint => UsePingFallback
        ? "Slower. For networks that filter TCP probes."
        : "Fast. Also finds quiet hosts in the ARP table.";

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

    private string IdleStatus()
    {
        if (string.IsNullOrWhiteSpace(Subnet))
        {
            return "Enter a subnet to start.";
        }

        if (HasSubnetError || !NetworkValidation.TryParseCidr(Subnet, out _))
        {
            return "Enter a valid subnet to start.";
        }

        long n = NetworkValidation.CountUsableHosts(Subnet.Trim());
        return $"Ready to probe {n:N0} {(n == 1 ? "host" : "hosts")}. Press Scan or F5.";
    }

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
            host.OpenPorts = ScanHistoryService.TryGet(host.IpAddress, out var history) ? history?.OpenPorts : null;
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

    // Copy button: the main part copies IP addresses; its dropdown offers
    // the other columns and the whole table. All act on the visible rows.

    [RelayCommand]
    private void CopyResults() => CopyColumn(h => h.IpAddress, "IP addresses");

    [RelayCommand]
    private void CopyNames() => CopyColumn(h => h.Hostname, "names");

    [RelayCommand]
    private void CopyMacAddresses() => CopyColumn(h => h.MacAddress, "MAC addresses");

    [RelayCommand]
    private void CopyTable()
    {
        if (VisibleHosts.Count > 0)
        {
            CopyToClipboard(HostResult.FormatTable(VisibleHosts), $"Copied a table of {VisibleHosts.Count:N0} hosts to the clipboard.");
        }
    }

    /// <summary>One value per line, skipping hosts where that field is unknown.</summary>
    private void CopyColumn(Func<HostResult, string?> field, string what)
    {
        if (VisibleHosts.Count == 0)
        {
            return;
        }

        List<string> values = VisibleHosts.Select(field).OfType<string>().Where(v => v.Length > 0).ToList();
        int missing = VisibleHosts.Count - values.Count;
        if (values.Count == 0)
        {
            StatusText = $"No {what} known for the listed hosts.";
            return;
        }

        CopyToClipboard(string.Join(Environment.NewLine, values),
            $"Copied {values.Count:N0} {what} to the clipboard" +
            (missing > 0 ? $" ({missing:N0} {(missing == 1 ? "host has" : "hosts have")} none)." : "."));
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
                h.Source, h.FoundAt.ToString("s"), h.PortsDetail,
            }),
            new[] { "ip_address", "hostname", "mac_address", "vendor", "source", "found_at", "ports" });

        await SaveCsvAsync("hosts", csv, $"{VisibleHosts.Count:N0} hosts");
    }
}
