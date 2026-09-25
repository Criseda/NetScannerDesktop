using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetScannerDesktop.Services;

namespace NetScannerDesktop.Views;

/// <summary>
/// View plumbing shared by the Discovery and Port scan pages, which are
/// laid out the same way: a setup card beside (or above) a results card.
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

    /// <summary>
    /// Wide: cards side by side, the page fits the window and only the
    /// results list scrolls. Narrow: cards stacked, the whole page scrolls
    /// and the list gets a fixed height so it stays usable.
    /// </summary>
    public static void ApplyCardLayout(
        bool wide,
        ScrollViewer pageScroller,
        Grid rootGrid,
        RowDefinition cardsRow,
        RowDefinition setupRow,
        ColumnDefinition setupColumn,
        FrameworkElement setupCard,
        FrameworkElement resultsCard,
        ListViewBase resultsList)
    {
        pageScroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        pageScroller.VerticalScrollMode = wide ? ScrollMode.Disabled : ScrollMode.Auto;
        rootGrid.Padding = wide ? new Thickness(32, 16, 32, 24) : new Thickness(20, 12, 20, 20);
        cardsRow.Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        setupRow.Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        setupColumn.Width = wide ? new GridLength(360) : new GridLength(1, GridUnitType.Star);
        resultsList.MaxHeight = wide ? double.PositiveInfinity : 480;

        Grid.SetColumnSpan(setupCard, wide ? 1 : 2);
        Grid.SetRow(resultsCard, wide ? 0 : 1);
        Grid.SetColumn(resultsCard, wide ? 1 : 0);
        Grid.SetColumnSpan(resultsCard, wide ? 1 : 2);
    }
}
