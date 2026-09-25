using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace NetScannerDesktop.Views;

/// <summary>
/// Port scan page. The XAML binds to <see cref="ViewModel"/>; this
/// file triggers the initial load and the two-column / stacked switch.
/// </summary>
public sealed partial class PortScanPage : Page
{
    public PortScanViewModel ViewModel { get; } = new();

    // Last mode requested by the shell. Reapplied on Loaded so a page
    // opened while the window is already narrow starts stacked.
    private bool wideLayout = true;
    private bool? lastAppliedWideLayout;

    public PortScanPage()
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

        // Page is cached; keep the badge live while a scan runs in the background.
        ViewModel.OpenPorts.CollectionChanged += OnPortsChanged;
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
        // Deferred + guarded: Select() during TextChanged can throw
        // InvalidOperationException (reentrant layout).
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

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement item && item.Tag is int port)
        {
            var package = new DataPackage();
            package.SetText(port.ToString());
            Clipboard.SetContent(package);
            ViewModel.StatusText = $"Copied port {port} to the clipboard.";
        }
    }

    private void CopyHostPort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement item && item.Tag is int port && !string.IsNullOrWhiteSpace(ViewModel.IpAddress))
        {
            string hostPort = $"{ViewModel.IpAddress.Trim()}:{port}";
            var package = new DataPackage();
            package.SetText(hostPort);
            Clipboard.SetContent(package);
            ViewModel.StatusText = $"Copied {hostPort} to the clipboard.";
        }
    }

    private async void OpenPortHttp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement item && item.Tag is int port && !string.IsNullOrWhiteSpace(ViewModel.IpAddress))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{ViewModel.IpAddress.Trim()}:{port}"));
        }
    }

    private async void OpenPortHttps_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement item && item.Tag is int port && !string.IsNullOrWhiteSpace(ViewModel.IpAddress))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://{ViewModel.IpAddress.Trim()}:{port}"));
        }
    }

    private async void PortsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (PortsList.SelectedItem is NetScannerDesktop.Models.PortResult pr && !string.IsNullOrWhiteSpace(ViewModel.IpAddress))
        {
            string scheme = (pr.Port == 443 || pr.Port == 8443) ? "https" : "http";
            if (pr.IsWebPort)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri($"{scheme}://{ViewModel.IpAddress.Trim()}:{pr.Port}"));
            }
            else
            {
                var package = new DataPackage();
                package.SetText(pr.Port.ToString());
                Clipboard.SetContent(package);
                ViewModel.StatusText = $"Copied port {pr.Port} to the clipboard.";
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Pre-fill from Discovery handoff, if any.
        if (e.Parameter is string ip && !string.IsNullOrWhiteSpace(ip))
        {
            ViewModel.LoadTargetIp(ip.Trim());
        }

        await ViewModel.LoadAsync();
        UpdateBadge();
    }

    private void OnPortsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateBadge();

    private void UpdateBadge()
    {
        try
        {
            if (App.Current is not App app || app.MainAppWindow is not MainWindow shell)
            {
                return;
            }

            int count = ViewModel.OpenPorts.Count;
            if (DispatcherQueue.HasThreadAccess)
            {
                shell.SetPortBadge(count);
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        shell.SetPortBadge(count);
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
