using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.Views;

/// <summary>
/// View plumbing shared by the Discovery and Port scan pages, which are
/// laid out the same way: a scan toolbar above a full-width results table.
/// </summary>
internal static class PageHelpers
{
    public static MainWindow? Shell => (Application.Current as App)?.MainAppWindow;

    public static async Task<bool> ConfirmLargeScanAsync(XamlRoot root, string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Large scan",
            Content = message,
            PrimaryButtonText = "Scan anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static Task<string?> SaveFileAsync(string name, string extension, string content) =>
        FileSaver.SaveTextWithPickerAsync(Shell, name, extension, content);

    /// <summary>
    /// Keep the newest engine line visible without stealing focus.
    /// Deferred + guarded: Select() during TextChanged can throw
    /// (reentrant layout), which would otherwise crash the app.
    /// </summary>
    public static void ScrollLogToEnd(TextBox? box)
    {
        if (box is null || string.IsNullOrEmpty(box.Text))
        {
            return;
        }

        box.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                box.Select(box.Text.Length, 0);
            }
            catch
            {
                // Autoscroll is best-effort only.
            }
        });
    }

    /// <summary>Roomier margins on a wide window, tighter ones beside the icon rail.</summary>
    public static void ApplyPagePadding(bool wide, Grid rootGrid) =>
        rootGrid.Padding = wide ? new Thickness(24, 12, 24, 20) : new Thickness(16, 8, 16, 16);

    /// <summary>
    /// Keep a results table exactly as wide as its horizontal scroller, but
    /// never narrower than <paramref name="minWidth"/>: star columns share
    /// the full width on a big window, and a small one scrolls sideways
    /// instead of crushing the columns. (A scroller measures its content
    /// with unlimited width, so without this the table would size to its
    /// longest text rather than to the window.)
    /// </summary>
    public static void FitTableToViewport(ScrollViewer scroller, FrameworkElement table, double minWidth) =>
        scroller.SizeChanged += (_, e) => table.Width = Math.Max(minWidth, e.NewSize.Width);
}
