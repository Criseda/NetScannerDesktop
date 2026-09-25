using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// App settings: theme, port scan defaults, history, and the About
/// details. Every change saves immediately, like Windows Settings.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Applies a theme to the running window. Set by the view.</summary>
    public Action<ElementTheme>? ApplyTheme { get; set; }

    // Backing fields are initialised directly so loading saved values does
    // not run the change handlers (which would re-save and re-apply).
    [ObservableProperty]
    private int selectedThemeIndex = AppSettings.GetInt(AppSettings.AppTheme, 0);

    [ObservableProperty]
    private string defaultPortRange = AppSettings.GetString(AppSettings.DefaultPortRange, "1-1024");

    [ObservableProperty]
    private double defaultTimeoutMs = ReadTimeout();

    [ObservableProperty]
    private string portRangeError = string.Empty;

    [ObservableProperty]
    private string historyStatus = string.Empty;

    [ObservableProperty]
    private string appVersion = GetAppVersion();

    [ObservableProperty]
    private string engineVersion = "…";

    [ObservableProperty]
    private string latestEngineVersion = "…";

    public bool HasPortRangeError => !string.IsNullOrEmpty(PortRangeError);

    public string BundledEngineVersion => EngineUpdater.PinnedVersion;

    public string CrashLogPath => App.CrashLogPath;

    private static double ReadTimeout() =>
        double.TryParse(AppSettings.GetString(AppSettings.DefaultTimeout, "500"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out double t) ? t : double.NaN;

    partial void OnSelectedThemeIndexChanged(int value)
    {
        AppSettings.SetInt(AppSettings.AppTheme, value);
        ApplyTheme?.Invoke(value switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
    }

    partial void OnDefaultPortRangeChanged(string value)
    {
        if (NetworkValidation.TryParsePortRange(value, out int start, out int end, out string error))
        {
            PortRangeError = string.Empty;
            AppSettings.SetString(AppSettings.DefaultPortRange, $"{start}-{end}");
        }
        else
        {
            PortRangeError = error;
        }

        OnPropertyChanged(nameof(HasPortRangeError));
    }

    partial void OnDefaultTimeoutMsChanged(double value) =>
        AppSettings.SetString(AppSettings.DefaultTimeout,
            double.IsNaN(value) ? string.Empty : ((int)Math.Clamp(value, 1, 60000)).ToString(CultureInfo.InvariantCulture));

    [RelayCommand]
    private void ClearHistory()
    {
        AppSettings.SetString(AppSettings.DiscoveryRecent, string.Empty);
        AppSettings.SetString(AppSettings.PortRecent, string.Empty);
        ScanHistoryService.Clear();
        HistoryStatus = "Cleared.";
    }

    public async Task LoadAsync()
    {
        HistoryStatus = string.Empty;

        try
        {
            EngineVersion = await new NetScannerService().GetVersionAsync(CancellationToken.None);
        }
        catch
        {
            EngineVersion = "not found";
        }

        // Shared with Discovery's check; silent when offline.
        LatestEngineVersion = (await EngineUpdater.GetLatestReleaseAsync())?.Tag ?? "unknown";
    }

    private static string GetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged: no package identity.
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        }
    }
}
