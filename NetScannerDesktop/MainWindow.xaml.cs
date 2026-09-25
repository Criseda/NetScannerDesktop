using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Views;

namespace NetScannerDesktop;

/// <summary>
/// App shell: a TitleBar plus a NavigationView that hosts one page per
/// scan type. Scan logic lives in the page view-models, not here. This
/// also owns the responsive behavior (nav pane + page layout) and the
/// minimum window size, both measured in logical pixels so display
/// scaling doesn't shift them.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 800;
    private const int MinWindowWidth = 640;
    private const int MinWindowHeight = 540;

    // Below this window width the pages stack their cards vertically
    // and the nav pane collapses to its icon rail to make room.
    private const double WideLayoutMinWidth = 1100;

    private double lastWidth = DefaultWindowWidth;
    private bool? currentWideMode;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        AppWindow.Title = "NetScanner";
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(DefaultWindowWidth * scale), (int)(DefaultWindowHeight * scale)));
        ApplyMinimumSize(scale);

        // Moving to a monitor with another scale changes the physical
        // size of our logical minimum.
        RootGrid.Loaded += (_, _) =>
            RootGrid.XamlRoot.Changed += (root, _) => ApplyMinimumSize(root.RasterizationScale);

        SizeChanged += (_, args) => UpdateResponsiveLayout(args.Size.Width);
        ContentFrame.Navigated += ContentFrame_Navigated;
        ApplySavedTheme();

        NavigateTo("Discovery");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>Presenter minimums are physical pixels; ours are logical.</summary>
    private void ApplyMinimumSize(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWindowWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinWindowHeight * scale);
        }
    }

    /// <summary>0 = follow Windows, 1 = light, 2 = dark (Settings page).</summary>
    public void ApplySavedTheme() =>
        ApplyTheme(Services.AppSettings.GetInt(Services.AppSettings.AppTheme, 0) switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });

    /// <summary>
    /// Themes the content and the caption buttons together. With Default
    /// both follow the Windows app mode live, like the Settings app.
    /// </summary>
    public void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        UpdateNavSelectionForPage(e.SourcePageType);
        UpdateResponsiveLayout(lastWidth, force: true);
    }

    private void UpdateNavSelectionForPage(Type? pageType)
    {
        if (pageType == typeof(SettingsPage))
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }

        string? targetTag = pageType == typeof(PortScanPage) ? "Ports"
            : pageType == typeof(DiscoveryPage) ? "Discovery"
            : null;

        Nav.SelectedItem = Nav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (item.Tag as string) == targetTag) ?? Nav.SelectedItem;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo("Settings");
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    /// <summary>
    /// One place drives both the nav pane and the page layout from the
    /// window width (logical pixels). A wide window gets side-by-side cards
    /// with an open pane, a narrow one stacked cards with the icon rail.
    /// </summary>
    private void UpdateResponsiveLayout(double windowWidth, bool force = false)
    {
        lastWidth = windowWidth;
        bool wide = windowWidth >= WideLayoutMinWidth;

        if (!force && currentWideMode == wide)
        {
            return;
        }

        currentWideMode = wide;
        Nav.PaneDisplayMode = wide
            ? NavigationViewPaneDisplayMode.Auto
            : NavigationViewPaneDisplayMode.LeftCompact;

        if (ContentFrame.Content is IResponsivePage page)
        {
            page.SetWideLayout(wide);
        }
    }

    private void NavigateTo(string tag)
    {
        Type target = tag switch
        {
            "Ports" => typeof(PortScanPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(DiscoveryPage),
        };

        if (ContentFrame.Content?.GetType() != target)
        {
            ContentFrame.Navigate(target);
        }
    }

    /// <summary>Discovery handoff: open Port scan with the IP pre-filled.</summary>
    public void NavigateToPortScan(string ipAddress) =>
        ContentFrame.Navigate(typeof(PortScanPage), ipAddress);

    /// <summary>Show result counts as native InfoBadges on the nav items.</summary>
    public void SetDiscoveryBadge(int count) => SetNavBadge("Discovery", count);

    public void SetPortBadge(int count) => SetNavBadge("Ports", count);

    private void SetNavBadge(string tag, int count)
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag) is { } item)
        {
            item.InfoBadge = count > 0 ? new InfoBadge { Value = count } : null;
        }
    }
}

/// <summary>Pages that switch between side-by-side and stacked cards.</summary>
public interface IResponsivePage
{
    void SetWideLayout(bool wide);
}
