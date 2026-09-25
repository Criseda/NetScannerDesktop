using System;
using System.IO;
using System.Threading.Tasks;

namespace NetScannerDesktop.Services;

/// <summary>
/// Saves scan results as files under Documents\NetScanner. A plain folder
/// write keeps the code simple and works the same packaged or unpackaged,
/// unlike the file picker which needs extra window-handle setup.
/// </summary>
public static class ResultExporter
{
    public static async Task<string> SaveTextAsync(string filePrefix, string extension, string content)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NetScanner");

        Directory.CreateDirectory(folder);

        string fileName = $"{filePrefix}-{DateTime.Now:yyyy-MM-dd-HHmmss}{extension}";
        string fullPath = Path.Combine(folder, fileName);

        await File.WriteAllTextAsync(fullPath, content);
        return fullPath;
    }

    public static string ToCsv(System.Collections.Generic.IEnumerable<string> rows, string header) =>
        header + Environment.NewLine + string.Join(Environment.NewLine, rows) + Environment.NewLine;
}
