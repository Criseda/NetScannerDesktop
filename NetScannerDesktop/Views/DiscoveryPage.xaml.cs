using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NetScannerDesktop.Models;
using NetScannerDesktop.Services;
using NetScannerDesktop.ViewModels;

namespace NetScannerDesktop.Views;

/// <summary>
/// Host discovery page. The XAML binds to <see cref="ViewModel"/>; this
/// file handles the subnet box, context actions, and layout sizing.
/// </summary>
public sealed partial class DiscoveryPage : Page, IResponsivePage
{
    /// <summary>Below this width the host table scrolls sideways instead of squeezing its columns.</summary>
    private const double HostsTableMinWidth = 960;

    public DiscoveryViewModel ViewModel { get; } = new();

    // Last mode requested by the shell. Reapplied on Loaded so a page
    // opened while the window is already narrow starts compact.
    private bool wideLayout = true;

    public DiscoveryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyLayoutMode();
        PageHelpers.FitTableToViewport(HostsTableScroller, HostsTable, HostsTableMinWidth);
        ViewModel.ConfirmLargeScanAsync = message => PageHelpers.ConfirmLargeScanAsync(XamlRoot, message);
        ViewModel.SaveFileAsync = PageHelpers.SaveFileAsync;

        // Subscribed for the page's lifetime (it is cached): a scan keeps
        // running while the user is on another page, and the badge is how
        // they see it progress.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiscoveryViewModel.HostCount))
            {
                UpdateBadge();
            }
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
        ViewModel.RefreshHistoryForHosts();
        UpdateBadge();
    }

    private void UpdateBadge() => PageHelpers.Shell?.SetDiscoveryBadge(ViewModel.HostCount);

    private void FirstRunTipBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.DismissFirstRunTip();

    // Subnet box ---------------------------------------------------------------

    private void SubnetBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Show every known network on focus, like a combo box.
        ViewModel.UpdateSuggestions(string.Empty);
        SubnetBox.IsSuggestionListOpen = ViewModel.SubnetSuggestions.Count > 0;
    }

    private void SubnetBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.UpdateSuggestions(sender.Text);
        }
    }

    private void SubnetBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SubnetSuggestion suggestion)
        {
            ViewModel.Subnet = suggestion.Cidr;
        }
    }

    private void SubnetBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter with a highlighted suggestion just fills it in; plain Enter scans.
        if (args.ChosenSuggestion is SubnetSuggestion suggestion)
        {
            ViewModel.Subnet = suggestion.Cidr;
        }
        else if (ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
        }
    }

    // Host actions -------------------------------------------------------------

    private HostResult? HostFromTag(object sender) =>
        (sender as FrameworkElement)?.Tag is string ip
            ? ViewModel.Hosts.FirstOrDefault(h => h.IpAddress == ip)
            : null;

    private void HostsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HostsList.SelectedItem is HostResult host)
        {
            ScanPorts(host.IpAddress);
        }
    }

    private void ScanPorts_Click(object sender, RoutedEventArgs e)
    {
        if (HostFromTag(sender) is { } host)
        {
            ScanPorts(host.IpAddress);
        }
    }

    private void ScanPorts(string ip)
    {
        if (PageHelpers.Shell is { } shell)
        {
            shell.NavigateToPortScan(ip);
        }
        else
        {
            Frame.Navigate(typeof(PortScanPage), ip);
        }
    }

    private void CopyHost_Click(object sender, RoutedEventArgs e)
    {
        if (HostFromTag(sender) is { } host)
        {
            ViewModel.CopyToClipboard(host.IpAddress, $"Copied {host.IpAddress} to the clipboard.");
        }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (HostFromTag(sender) is { } host)
        {
            string summary = string.Join(" - ", new[]
            {
                host.IpAddress, host.DetailsLine, $"found via {host.Source} at {host.FoundAtShort}",
                host.HasScannedPorts ? $"open ports: {(host.PortsDetail.Length > 0 ? host.PortsDetail : "none")}" : null,
            }.Where(s => !string.IsNullOrEmpty(s)));

            ViewModel.CopyToClipboard(summary, $"Copied details for {host.IpAddress}.");
        }
    }

    private async void OpenHttp_Click(object sender, RoutedEventArgs e)
    {
        if (HostFromTag(sender) is { } host)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"http://{host.IpAddress}"));
        }
    }

    private async void OpenHttps_Click(object sender, RoutedEventArgs e)
    {
        if (HostFromTag(sender) is { } host)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://{host.IpAddress}"));
        }
    }

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

    /// <summary>
    /// Called by the shell with the window's logical width class. The
    /// toolbar wraps and the table scrolls on their own; only the page
    /// margins change.
    /// </summary>
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
