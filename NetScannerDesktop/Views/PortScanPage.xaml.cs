using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.Models;
using NetScannerDesktop.ViewModels;

namespace NetScannerDesktop.Views;

/// <summary>
/// Port scan page. The XAML binds to <see cref="ViewModel"/>; this file
/// handles the host box, context actions, and layout sizing.
/// </summary>
public sealed partial class PortScanPage : Page, IResponsivePage
{
    /// <summary>Below this width the port table scrolls sideways instead of squeezing its columns.</summary>
    private const double PortsTableMinWidth = 800;

    public PortScanViewModel ViewModel { get; } = new();

    private bool wideLayout = true;

    public PortScanPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyLayoutMode();
        PageHelpers.FitTableToViewport(PortsTableScroller, PortsTable, PortsTableMinWidth);
        ViewModel.ConfirmLargeScanAsync = message => PageHelpers.ConfirmLargeScanAsync(XamlRoot, message);
        ViewModel.SaveFileAsync = PageHelpers.SaveFileAsync;

        // Page is cached; keep the badge live while a scan runs in the background.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PortScanViewModel.PortCount))
            {
                UpdateBadge();
            }
        };

        foreach (PortRangePreset preset in PortScanViewModel.Presets)
        {
            PresetsMenu.Items.Add(new MenuFlyoutItem
            {
                Text = preset.Display,
                Command = ViewModel.ApplyPresetCommand,
                CommandParameter = preset.Range,
            });
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Pre-fill from Discovery handoff, if any.
        if (e.Parameter is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            ViewModel.LoadTargetIp(ip);
        }

        await ViewModel.LoadAsync();
        UpdateBadge();
    }

    private void UpdateBadge() => PageHelpers.Shell?.SetPortBadge(ViewModel.PortCount);

    // Host box -----------------------------------------------------------------

    private void IpBox_GotFocus(object sender, RoutedEventArgs e) =>
        IpBox.IsSuggestionListOpen = ViewModel.RecentIps.Count > 0;

    private void IpBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string ip)
        {
            ViewModel.LoadTargetIp(ip);
        }
    }

    private void IpBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string ip)
        {
            ViewModel.LoadTargetIp(ip);
        }
        else
        {
            StartScan();
        }
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            StartScan();
            e.Handled = true;
        }
    }

    private void StartScan()
    {
        if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
        }
    }

    // Port actions -------------------------------------------------------------

    private string Host => ViewModel.IpAddress.Trim();

    private static int? PortFromTag(object sender) => (sender as FrameworkElement)?.Tag as int?;

    private void PortsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // One predictable action everywhere: copy host:port. Web ports
        // also get an inline Open link.
        if (PortsList.SelectedItem is PortResult port)
        {
            CopyText($"{Host}:{port.Port}");
        }
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (PortFromTag(sender) is int port)
        {
            CopyText(port.ToString());
        }
    }

    private void CopyHostPort_Click(object sender, RoutedEventArgs e)
    {
        if (PortFromTag(sender) is int port)
        {
            CopyText($"{Host}:{port}");
        }
    }

    private void OpenWebPort_Click(object sender, RoutedEventArgs e)
    {
        if (PortFromTag(sender) is int port)
        {
            Open(ViewModel.OpenPorts.FirstOrDefault(p => p.Port == port)?.WebScheme ?? "http", port);
        }
    }

    private void OpenPortHttp_Click(object sender, RoutedEventArgs e)
    {
        if (PortFromTag(sender) is int port)
        {
            Open("http", port);
        }
    }

    private void OpenPortHttps_Click(object sender, RoutedEventArgs e)
    {
        if (PortFromTag(sender) is int port)
        {
            Open("https", port);
        }
    }

    private async void Open(string scheme, int port) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri($"{scheme}://{Host}:{port}"));

    private void CopyText(string text) => ViewModel.CopyToClipboard(text, $"Copied {text} to the clipboard.");

    // Keyboard -----------------------------------------------------------------

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
        args.Handled = true;
    }

    private void ScanAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsScanning)
        {
            ViewModel.CancelCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CopyResultsCommand.Execute(null);
        args.Handled = true;
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) =>
        PageHelpers.ScrollLogToEnd(sender as TextBox);

    // Layout -------------------------------------------------------------------

    public void SetWideLayout(bool wide)
    {
        wideLayout = wide;
        if (IsLoaded)
        {
            ApplyLayoutMode();
        }
    }

    private void ApplyLayoutMode() => PageHelpers.ApplyPagePadding(wideLayout, RootGrid);
}
