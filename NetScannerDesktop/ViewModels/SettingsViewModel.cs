using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.ViewModels;

/// <summary>
/// App settings: theme + defaults + history. Applies theme live via
/// the callback the shell wires up (MainWindow.ApplyTheme).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>0=System, 1=Light, 2=Dark. Set by the view.</summary>
    public System.Action<ElementTheme>? ApplyTheme { get; set; }

    [ObservableProperty]
    private int selectedThemeIndex;

    [ObservableProperty]
    private string defaultPortRange = "1-1024";

    [ObservableProperty]
    private string defaultTimeoutMs = "500";

    [ObservableProperty]
    private string statusText = string.Empty;

    public SettingsViewModel()
    {
        SelectedThemeIndex = AppSettings.GetInt(AppSettings.AppTheme, 0);
        DefaultPortRange = AppSettings.GetString("defaults.portRange", "1-1024");
        if (string.IsNullOrWhiteSpace(DefaultPortRange))
        {
            DefaultPortRange = "1-1024";
        }

        DefaultTimeoutMs = AppSettings.GetString("defaults.timeoutMs", "500");
        if (string.IsNullOrWhiteSpace(DefaultTimeoutMs))
        {
            DefaultTimeoutMs = "500";
        }
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        AppSettings.SetInt(AppSettings.AppTheme, value);
        ApplyTheme?.Invoke(value switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
        StatusText = "Theme applied.";
    }

    [RelayCommand]
    private void SaveDefaults()
    {
        if (!NetworkValidation.TryParsePortRange(DefaultPortRange, out _, out _, out string rangeError))
        {
            StatusText = rangeError;
            return;
        }

        if (!NetworkValidation.TryParseTimeout(DefaultTimeoutMs, out _, out string timeoutError))
        {
            StatusText = timeoutError;
            return;
        }

        AppSettings.SetString("defaults.portRange", DefaultPortRange.Trim());
        AppSettings.SetString("defaults.timeoutMs", DefaultTimeoutMs.Trim());
        AppSettings.SetString(AppSettings.PortRange, DefaultPortRange.Trim());
        AppSettings.SetString(AppSettings.PortTimeout, DefaultTimeoutMs.Trim());
        StatusText = "Defaults saved. New Port scans will use them.";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        AppSettings.SetString(AppSettings.DiscoveryRecent, string.Empty);
        AppSettings.SetString(AppSettings.PortRecent, string.Empty);
        ScanHistoryService.Clear();
        StatusText = "Recent scans and port history cleared.";
    }

    public Task LoadAsync() => Task.CompletedTask;
}
