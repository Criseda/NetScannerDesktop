using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.ViewModels;

namespace NetScannerDesktop.Views;

/// <summary>
/// Settings page: theme, defaults, history.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel.ApplyTheme = theme =>
        {
            if (App.Current is App app && app.MainAppWindow is MainWindow shell &&
                shell.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
                shell.SyncCaptionTheme(theme);
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }
}
