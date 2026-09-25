using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace NetScannerDesktop.Views;

/// <summary>
/// Settings page: theme, defaults, history, and About.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new();

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel.ApplyTheme = theme => PageHelpers.Shell?.ApplyTheme(theme);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void ConfirmClearHistory_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearHistoryCommand.Execute(null);
        ClearHistoryFlyout.Hide();
    }

    private void CopyCrashLogPath_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.CrashLogPath);
        Clipboard.SetContent(package);
    }
}
