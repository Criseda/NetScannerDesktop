using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace NetScannerDesktop.Views;

/// <summary>
/// Host discovery page. The XAML binds to <see cref="ViewModel"/>; this
/// file triggers the initial load and the two-column / stacked switch.
/// </summary>
public sealed partial class DiscoveryPage : Page
{
    public DiscoveryViewModel ViewModel { get; } = new();

    // Last mode requested by the shell. Reapplied on Loaded so a page
    // opened while the window is already narrow starts stacked.
    private bool wideLayout = true;
    private bool? lastAppliedWideLayout;

    public DiscoveryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += (_, _) =>
        {
            lastAppliedWideLayout = null;
            ApplyLayoutMode();
            lastAppliedWideLayout = wideLayout;
        };
        ViewModel.ConfirmLargeScanAsync = ConfirmLargeScanAsync;
        ViewModel.SaveFileAsync = SaveFileAsync;

        // Subscribed for the page's lifetime (it is cached): a scan keeps
        // running while the user is on another page, and the badge is how
        // they see it progress.
        ViewModel.Hosts.CollectionChanged += OnHostsChanged;
    }

    private async System.Threading.Tasks.Task<string?> SaveFileAsync(string name, string ext, string content)
    {
        var window = (App.Current as App)?.MainAppWindow;
        return await NetScannerDesktop.Services.FileSaver.SaveTextWithPickerAsync(window, name, ext, content);
    }

    private async System.Threading.Tasks.Task<bool> ConfirmLargeScanAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Large scan",
            Content = message,
            PrimaryButtonText = "Scan anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the newest engine line visible without stealing focus.
        // Deferred + guarded: Select() during TextChanged can throw
        // InvalidOperationException (reentrant layout) which would
        // otherwise surface as an unhandled crash.
        if (sender is not TextBox box || string.IsNullOrEmpty(box.Text))
        {
            return;
        }

        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(box.Text))
                    {
                        box.Select(box.Text.Length, 0);
                    }
                }
                catch
                {
                    // Autoscroll is best-effort only.
                }
            });
        }
        catch
        {
        }
    }

    private void CopyHost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            var package = new DataPackage();
            package.SetText(ip);
            Clipboard.SetContent(package);
            ViewModel.StatusText = $"Copied {ip} to the clipboard.";
        }
    }

    private void Input_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ViewModel.ScanCommand.CanExecute(null))
            {
                ViewModel.ScanCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.IsScanning)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void HostsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (HostsList.SelectedItem is NetScannerDesktop.Models.HostResult host)
        {
            ScanPorts(host.IpAddress);
        }
    }

    private void ScanPorts_Click(object sender, RoutedEventArgs e)
    {
        string? ip = (sender as FrameworkElement)?.Tag as string;
        if (!string.IsNullOrWhiteSpace(ip))
        {
            ScanPorts(ip);
        }
    }

    private void ScanPorts(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        if (App.Current is App app && app.MainAppWindow is MainWindow shell)
        {
            shell.NavigateToPortScan(ip.Trim());
        }
        else
        {
            Frame.Navigate(typeof(PortScanPage), ip.Trim());
        }
    }

    private async void OpenHttp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ip } && !string.IsNullOrWhiteSpace(ip))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{ip}"));
        }
    }

    private async void OpenHttps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ip } && !string.IsNullOrWhiteSpace(ip))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://{ip}"));
        }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ip } && !string.IsNullOrWhiteSpace(ip))
        {
            var host = ViewModel.Hosts.FirstOrDefault(h => h.IpAddress == ip);
            if (host != null)
            {
                string summary = $"{host.IpAddress} ({host.Source}) - Found {host.FoundAtShort}";
                if (!string.IsNullOrEmpty(host.PortsSummary))
                {
                    summary += $" - {host.PortsSummary}";
                }
                var package = new DataPackage();
                package.SetText(summary);
                Clipboard.SetContent(package);
                ViewModel.StatusText = $"Copied summary for {ip}.";
            }
        }
    }

    private void FilterAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
        args.Handled = true;
    }

    private void ScanAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CancelAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CancelCommand.CanExecute(null))
        {
            ViewModel.CancelCommand.Execute(null);
            args.Handled = true;
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
        ViewModel.RefreshHistoryForHosts();
        UpdateBadge();
    }

    private void OnHostsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateBadge();

    private void UpdateBadge()
    {
        try
        {
            if (App.Current is not App app || app.MainAppWindow is not MainWindow shell)
            {
                return;
            }

            int count = ViewModel.Hosts.Count;
            if (DispatcherQueue.HasThreadAccess)
            {
                shell.SetDiscoveryBadge(count);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        shell.SetDiscoveryBadge(count);
                    }
                    catch
                    {
                    }
                });
            }
        }
        catch
        {
            // Badge is decoration; never crash for it.
        }
    }

    private void FirstRunTipBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.DismissFirstRunTip();

    /// <summary>
    /// Called by the shell: side-by-side cards when true, stacked when
    /// false. Done in code on purpose: framework AdaptiveTriggers measure
    /// physical pixels, so on a scaled display they fire at the wrong
    /// window widths, while this uses logical pixels from the window.
    /// </summary>
    public void SetWideLayout(bool wide)
    {
        if (lastAppliedWideLayout == wide)
        {
            return;
        }

        wideLayout = wide;
        lastAppliedWideLayout = wide;
        ApplyLayoutMode();
    }

    private void ApplyLayoutMode()
    {
        if (wideLayout)
        {
            RootGrid.Padding = new Thickness(32, 24, 32, 32);
            SetupColumn.Width = new GridLength(380);
            Grid.SetColumnSpan(SetupCard, 1);
            Grid.SetRow(ResultsCard, 0);
            Grid.SetColumn(ResultsCard, 1);
            Grid.SetColumnSpan(ResultsCard, 1);
        }
        else
        {
            RootGrid.Padding = new Thickness(20, 16, 20, 24);
            SetupColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumnSpan(SetupCard, 2);
            Grid.SetRow(ResultsCard, 1);
            Grid.SetColumn(ResultsCard, 0);
            Grid.SetColumnSpan(ResultsCard, 2);
        }
    }
}
