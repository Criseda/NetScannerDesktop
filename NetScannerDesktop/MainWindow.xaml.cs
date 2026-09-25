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
        RootGrid.ActualThemeChanged += (_, _) => UpdateResizeBackground();
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
        UpdateResizeBackground();
    }

    private nint resizeBrush;
    private SubclassProc? eraseProc;

    /// <summary>
    /// When the window grows faster than XAML re-renders, the newly exposed
    /// strip used to flash black: WinUI answers WM_ERASEBKGND without
    /// painting (and a class background brush is never used). Paint that
    /// strip ourselves with the Mica base colour of the current theme, so a
    /// fast resize shows plain app background until the content catches
    /// up. Measured: black resize frames went from 24/27 to 0/27.
    /// </summary>
    private void UpdateResizeBackground()
    {
        // Mica base: #202020 dark, #F3F3F3 light. COLORREF is 0x00BBGGRR.
        uint color = RootGrid.ActualTheme == ElementTheme.Light ? 0x00F3F3F3u : 0x00202020u;
        nint old = resizeBrush;
        resizeBrush = CreateSolidBrush(color);
        if (old != 0)
        {
            DeleteObject(old);
        }

        if (eraseProc is null)
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            eraseProc = EraseSubclass; // field keeps the delegate alive for native code
            SetWindowSubclass(hwnd, eraseProc, 1, 0);
            Closed += (_, _) =>
            {
                RemoveWindowSubclass(hwnd, eraseProc, 1);
                DeleteObject(resizeBrush);
                resizeBrush = 0;
            };
        }
    }

    private nint EraseSubclass(nint hWnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint WM_ERASEBKGND = 0x0014;
        if (msg == WM_ERASEBKGND && resizeBrush != 0 && GetClientRect(hWnd, out RECT rect))
        {
            FillRect((nint)wParam, ref rect, resizeBrush);
            return 1;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);

    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nuint id, nuint data);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc proc, nuint id);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int FillRect(nint hdc, ref RECT rect, nint brush);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

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
