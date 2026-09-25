using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Views;

namespace NetScannerDesktop;

/// <summary>
/// App shell: a NavigationView that hosts one page per scan type.
/// Scan logic lives in the page view-models, not here. This also owns
/// the responsive behavior (nav pane + page layout) and the minimum
/// window size, both measured in logical pixels so display scaling
/// doesn't shift them.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Minimum size enforced at the OS level via WM_GETMINMAXINFO.
    private const int MinWindowWidth = 640;
    private const int MinWindowHeight = 540;

    // Below this window width the pages stack their cards vertically
    // and the nav pane collapses to its icon rail to make room.
    private const double WideLayoutMinWidth = 1100;

    private Microsoft.UI.Windowing.AppWindow? appWindow;
    private nint hwnd;
    private double lastWidth = 1280;
    private bool? currentWideMode;
    private readonly SubclassProc subclassProc;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        SizeWindowToDefault();

        subclassProc = new SubclassProc(WindowSubclass);
        SetWindowSubclass(hwnd, subclassProc, 1, 0);
        Closed += (_, _) => RemoveWindowSubclass(hwnd, subclassProc, 1);

        SizeChanged += OnWindowSizeChanged;
        ContentFrame.Navigated += ContentFrame_Navigated;
        ApplySavedTheme();

        // Land on discovery.
        NavigateTo("Discovery");
    }

    public void ApplySavedTheme()
    {
        int theme = Services.AppSettings.GetInt(Services.AppSettings.AppTheme, 0);
        ElementTheme requested = theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = requested;
        }

        SyncCaptionTheme(requested);
    }

    /// <summary>
    /// Keep the caption buttons in step with the app theme (System follows
    /// the PC theme, like the Settings app). Transparent backgrounds allow
    /// Mica to show through all the way to the top edge.
    /// </summary>
    public void SyncCaptionTheme(ElementTheme requested)
    {
        try
        {
            bool dark = requested switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => IsSystemDark(),
            };
            ApplyImmersiveDarkMode(hwnd, dark);

            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported() && appWindow?.TitleBar is { } titleBar)
            {
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

                if (dark)
                {
                    titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                    titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 255, 255, 255);
                    titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(180, 255, 255, 255);
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
                    titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(120, 255, 255, 255);
                }
                else
                {
                    titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 20, 20, 20);
                    titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 0, 0, 0);
                    titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(180, 0, 0, 0);
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
                    titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(120, 0, 0, 0);
                }
            }
        }
        catch
        {
            // Caption theming is cosmetic only.
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            // Luminance heuristic: dark background means dark mode.
            double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return luminance < 0.5;
        }
        catch
        {
            return true;
        }
    }

    private static void ApplyImmersiveDarkMode(nint windowHandle, bool dark)
    {
        if (windowHandle == 0)
        {
            return;
        }

        int value = dark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE on 20H1+; 19 on older builds.
        if (DwmSetWindowAttribute(windowHandle, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(windowHandle, 19, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Start at a comfortable desktop size, not the OS default.</summary>
    private void SizeWindowToDefault()
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(ToPhysicalSize(1280, 800));
        appWindow.Title = "NetScanner";
        // NOTE: the titlebar is intentionally left to the system so it
        // follows the PC theme like other native apps. ApplySavedTheme only
        // syncs the caption-button theme (light/dark/default).
        SyncCaptionTheme(ElementTheme.Default);
    }

    /// <summary>
    /// AppWindow works in physical pixels, but our sizes are logical.
    /// The manifest opts into per-monitor DPI awareness, so scale them.
    /// </summary>
    private Windows.Graphics.SizeInt32 ToPhysicalSize(int logicalWidth, int logicalHeight)
    {
        double scale = GetDpiForWindow(hwnd) / 96.0;
        return new Windows.Graphics.SizeInt32(
            (int)(logicalWidth * scale), (int)(logicalHeight * scale));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_ERASEBKGND = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    /// <summary>
    /// Intercept WM_GETMINMAXINFO to natively and synchronously clamp the window's
    /// minimum size at the OS level. This prevents sizing jitter, screen tearing,
    /// and ensures the mouse physically cannot drag the window below the minimum dimensions.
    /// Also suppresses WM_ERASEBKGND so GDI does not paint a black background during resize.
    /// </summary>
    private nint WindowSubclass(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_ERASEBKGND)
        {
            return 1;
        }

        nint res = DefSubclassProc(hWnd, uMsg, wParam, lParam);

        if (uMsg == WM_GETMINMAXINFO && lParam != 0)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var min = ToPhysicalSize(MinWindowWidth, MinWindowHeight);
            mmi.ptMinTrackSize.x = Math.Max(mmi.ptMinTrackSize.x, min.Width);
            mmi.ptMinTrackSize.y = Math.Max(mmi.ptMinTrackSize.y, min.Height);
            Marshal.StructureToPtr(mmi, lParam, false);
            return 0;
        }

        return res;
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        Nav.IsBackEnabled = ContentFrame.CanGoBack;
        AppTitleBar.Margin = ContentFrame.CanGoBack
            ? new Thickness(96, 0, 140, 0)
            : new Thickness(48, 0, 140, 0);

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

        string? targetTag = pageType switch
        {
            var t when t == typeof(DiscoveryPage) => "Discovery",
            var t when t == typeof(PortScanPage) => "Ports",
            var t when t == typeof(AboutPage) => "About",
            _ => null,
        };

        if (targetTag != null)
        {
            foreach (var item in Nav.MenuItems.Cast<object>().Concat(Nav.FooterMenuItems.Cast<object>()))
            {
                if (item is NavigationViewItem nvi && (nvi.Tag as string) == targetTag)
                {
                    Nav.SelectedItem = nvi;
                    break;
                }
            }
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            if (ContentFrame.Content is not SettingsPage)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                UpdateResponsiveLayout(lastWidth, force: true);
            }
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Type? targetType = tag switch
            {
                "Ports" => typeof(PortScanPage),
                "About" => typeof(AboutPage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(DiscoveryPage),
            };

            if (ContentFrame.Content?.GetType() != targetType)
            {
                NavigateTo(tag);
            }
        }
    }

    /// <summary>
    /// One place drives both the nav pane and the page layout from the
    /// window width (logical pixels). Keeps them in sync: a wide window
    /// gets side-by-side cards with an open pane, a narrow one gets
    /// stacked cards with the icon rail.
    /// </summary>
    private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        UpdateResponsiveLayout(args.Size.Width);
    }

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

        switch (ContentFrame.Content)
        {
            case DiscoveryPage discovery:
                discovery.SetWideLayout(wide);
                break;
            case PortScanPage ports:
                ports.SetWideLayout(wide);
                break;
        }
    }

    private void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "Ports":
                ContentFrame.Navigate(typeof(PortScanPage));
                break;
            case "About":
                ContentFrame.Navigate(typeof(AboutPage));
                break;
            case "Settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
            default:
                ContentFrame.Navigate(typeof(DiscoveryPage));
                break;
        }

        // The new page starts in Wide arrangement; pull it into line
        // when the window is already narrow (Loaded reapplies it too).
        UpdateResponsiveLayout(lastWidth, force: true);
    }

    /// <summary>
    /// Discovery handoff: open Port scan with the IP pre-filled.
    /// </summary>
    public void NavigateToPortScan(string ipAddress)
    {
        ContentFrame.Navigate(typeof(PortScanPage), ipAddress);
        UpdateResponsiveLayout(lastWidth, force: true);
    }

    /// <summary>Show result counts as native InfoBadges on the nav items.</summary>
    public void SetDiscoveryBadge(int count) => SetNavBadge(0, count);

    public void SetPortBadge(int count) => SetNavBadge(1, count);

    private void SetNavBadge(int index, int count)
    {
        if (Nav.MenuItems.Count <= index || Nav.MenuItems[index] is not NavigationViewItem item)
        {
            return;
        }

        if (count <= 0)
        {
            item.InfoBadge = null;
            return;
        }

        item.InfoBadge = new InfoBadge { Value = count };
    }
}
