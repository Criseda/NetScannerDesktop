using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NetScannerDesktop.Services;

/// <summary>
/// Native file saving. Prefers FileSavePicker (HWND-initialized so it
/// works packaged and unpackaged); falls back to Documents\NetScanner
/// when the picker is unavailable.
/// </summary>
public static class FileSaver
{
    public static async Task<string?> SaveTextWithPickerAsync(
        Microsoft.UI.Xaml.Window? window,
        string suggestedName,
        string extension,
        string content)
    {
        if (window is not null)
        {
            try
            {
                var picker = new FileSavePicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = $"{suggestedName}-{DateTime.Now:yyyy-MM-dd-HHmmss}";
                picker.FileTypeChoices.Add("CSV", new List<string> { extension });
                picker.DefaultFileExtension = extension;

                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file is null)
                {
                    return null; // user cancelled
                }

                await Windows.Storage.FileIO.WriteTextAsync(file, content);
                return file.Path;
            }
            catch
            {
                // Fall through to Documents fallback below.
            }
        }

        return await ResultExporter.SaveTextAsync(suggestedName, extension, content);
    }

    public static string ToCsv(IEnumerable<string[]> rows, string[] header) =>
        ToCsvLine(header) + Environment.NewLine +
        string.Join(Environment.NewLine, rows.Select(ToCsvLine)) + Environment.NewLine;

    private static string ToCsvLine(string[] fields) =>
        string.Join(",", fields.Select(Escape));

    private static string Escape(string field)
    {
        if (field.Contains('"') || field.Contains(',') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }
}
