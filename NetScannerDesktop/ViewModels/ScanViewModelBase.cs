using System;
using System.Diagnostics;
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
/// What the Discovery and Port scan pages share: running one engine scan
/// at a time with cancel, a ticking status line, the progress bar, the bounded engine log,
/// the notice InfoBar, and the dialog/save hooks the view provides.
/// </summary>
public abstract partial class ScanViewModelBase : ObservableObject
{
    protected readonly INetScannerService Scanner;
    private readonly EngineLog log = new();
    private CancellationTokenSource? runningScan;
    private Action? refreshProgress;
    private Stopwatch? scanStopwatch;
    private TimeSpan phaseStartedAt;

    protected ScanViewModelBase(INetScannerService scanner, string defaultSortColumn)
    {
        Scanner = scanner;
        sortColumn = defaultSortColumn;
    }

    /// <summary>Set by the view: (suggestedName, extension, content) -> saved path or null.</summary>
    public Func<string, string, string, Task<string?>>? SaveFileAsync { get; set; }

    /// <summary>Set by the view to confirm unusually large scans.</summary>
    public Func<string, Task<bool>>? ConfirmLargeScanAsync { get; set; }

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isEngineMissing;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string logText = string.Empty;

    [ObservableProperty]
    private bool isNoticeOpen;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity noticeSeverity = InfoBarSeverity.Informational;

    public string LogLineCountText => log.LineCountText;

    /// <summary>
    /// Latest engine progress event of the running scan; null before the
    /// first one, and always with engines older than v1.5 (no events), when
    /// the bar stays indeterminate.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate), nameof(ProgressPercent))]
    private ScanProgress? currentProgress;

    public bool IsProgressIndeterminate => CurrentProgress is null;

    public double ProgressPercent => CurrentProgress?.Percent ?? 0;

    /// <summary>How long the current phase has been running.</summary>
    protected TimeSpan PhaseElapsed => (scanStopwatch?.Elapsed ?? TimeSpan.Zero) - phaseStartedAt;

    /// <summary>
    /// Progress&lt;ScanProgress&gt; target. Keeps the latest event: the engine
    /// may skip counts, and a new phase restarts the bar from its own zero.
    /// </summary>
    protected void ReportScanProgress(ScanProgress progress)
    {
        if (!IsScanning)
        {
            return;
        }

        if (CurrentProgress is { } current && current.Phase == progress.Phase)
        {
            if (progress.Done < current.Done)
            {
                return;
            }
        }
        else
        {
            phaseStartedAt = scanStopwatch?.Elapsed ?? TimeSpan.Zero;
        }

        CurrentProgress = progress;
        RefreshProgress();
    }

    /// <summary>"42% · about 5s left"-style tail for the status line, or empty.</summary>
    protected string ProgressStatusPart(bool withRemaining)
    {
        if (CurrentProgress is not { } progress)
        {
            return string.Empty;
        }

        string text = progress.Text;
        if (withRemaining && progress.Remaining(PhaseElapsed) is { } left)
        {
            text += $" • about {FormatRemaining(left)} left";
        }

        return text;
    }

    private static string FormatRemaining(TimeSpan left) => left.TotalMinutes >= 1
        ? $"{(int)Math.Ceiling(left.TotalMinutes)} min"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))}s";

    partial void OnIsScanningChanged(bool value) => OnScanStateChanged();

    partial void OnIsEngineMissingChanged(bool value) => OnScanStateChanged();

    /// <summary>
    /// Scanning started/stopped or the engine appeared/vanished: refresh
    /// the Scan command and anything derived from those.
    /// </summary>
    protected abstract void OnScanStateChanged();

    /// <summary>
    /// Runs one scan with the shared plumbing: clears the log and notice,
    /// ticks <paramref name="progressStatus"/> into the status line (and on
    /// <see cref="RefreshProgress"/>), and turns cancellation and failures
    /// into a status line / error notice.
    /// </summary>
    protected async Task RunScanAsync(
        Func<TimeSpan, string> progressStatus,
        Func<CancellationToken, Stopwatch, Task> scan,
        Func<TimeSpan, string> cancelledStatus)
    {
        HideNotice();
        ClearLog();

        using var cts = new CancellationTokenSource();
        runningScan = cts;
        var stopwatch = Stopwatch.StartNew();
        scanStopwatch = stopwatch;
        phaseStartedAt = TimeSpan.Zero;
        CurrentProgress = null;
        refreshProgress = () => StatusText = progressStatus(stopwatch.Elapsed);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshProgress();
        IsScanning = true;
        timer.Start();
        RefreshProgress();

        try
        {
            await scan(cts.Token, stopwatch);
        }
        catch (OperationCanceledException)
        {
            // CurrentProgress still holds where it stopped.
            StatusText = cancelledStatus(stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Error);
            StatusText = "Scan failed.";
        }
        finally
        {
            timer.Stop();
            refreshProgress = null;
            scanStopwatch = null;
            CurrentProgress = null;
            runningScan = null;
            FlushLog();
            IsScanning = false;
        }
    }

    /// <summary>Re-render the progress status now (e.g. when a result arrives).</summary>
    protected void RefreshProgress() => refreshProgress?.Invoke();

    [RelayCommand]
    private void Cancel() => runningScan?.Cancel();

    protected static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
        : $"{elapsed.TotalSeconds:F1}s";

    // Results table sorting ------------------------------------------------------

    /// <summary>Column the results table is sorted by: the key its header passes to <see cref="SortByCommand"/>.</summary>
    [ObservableProperty]
    private string sortColumn;

    [ObservableProperty]
    private bool sortDescending;

    /// <summary>Header click: sort by that column, or flip the direction if it already is.</summary>
    [RelayCommand]
    private void SortBy(string column)
    {
        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        ApplySort();
    }

    /// <summary>Re-sort the results for <see cref="SortColumn"/> and <see cref="SortDescending"/>.</summary>
    protected abstract void ApplySort();

    /// <summary>
    /// Arrow shown in a column header. Bound as an x:Bind function of the
    /// sort state, so every header refreshes when the sort changes.
    /// </summary>
    public string SortGlyph(string column, string sortColumn, bool descending) =>
        TableSort.Glyph(column, sortColumn, descending);

    // Engine log ---------------------------------------------------------------

    /// <summary>Progress&lt;string&gt; target for engine output lines.</summary>
    protected void AppendLogLine(string line)
    {
        if (log.Append(line))
        {
            LogText = log.Text;
            OnPropertyChanged(nameof(LogLineCountText));
        }
    }

    protected void ClearLog()
    {
        log.Clear();
        LogText = string.Empty;
        OnPropertyChanged(nameof(LogLineCountText));
    }

    protected void FlushLog()
    {
        LogText = log.Flush();
        OnPropertyChanged(nameof(LogLineCountText));
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (!string.IsNullOrEmpty(LogText))
        {
            CopyToClipboard(LogText, $"Copied engine log ({LogLineCountText}) to the clipboard.");
        }
    }

    // Helpers ------------------------------------------------------------------

    protected void ShowNotice(string text, InfoBarSeverity severity)
    {
        NoticeText = text;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    protected void HideNotice() => IsNoticeOpen = false;

    public void CopyToClipboard(string text, string status)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusText = status;
    }

    /// <summary>Saves via the view's picker, falling back to Documents\NetScanner.</summary>
    protected async Task SaveCsvAsync(string name, string csv, string what)
    {
        string? path = SaveFileAsync is not null
            ? await SaveFileAsync(name, ".csv", csv)
            : await ResultExporter.SaveTextAsync(name, ".csv", csv);

        StatusText = path is null ? "Export cancelled." : $"Saved {what} to {path}.";
    }

    /// <summary>Probes the engine once at startup; false (with an error notice) when it is missing.</summary>
    protected async Task<string?> ProbeEngineAsync()
    {
        try
        {
            string version = await Scanner.GetVersionAsync(CancellationToken.None);
            IsEngineMissing = false;
            return version;
        }
        catch (Exception ex)
        {
            IsEngineMissing = true;
            ShowNotice($"Engine not found: {ex.Message}", InfoBarSeverity.Error);
            return null;
        }
    }
}
