using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// What the Discovery and Port scan pages share: running one engine scan
/// at a time with cancel, a ticking status line, the bounded engine log,
/// the notice InfoBar, and the dialog/save hooks the view provides.
/// </summary>
public abstract partial class ScanViewModelBase : ObservableObject
{
    protected readonly INetScannerService Scanner;
    private readonly EngineLog log = new();
    private CancellationTokenSource? runningScan;
    private Action? refreshProgress;

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
