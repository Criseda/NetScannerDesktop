using System;
using System.IO;
using System.Threading.Tasks;

namespace NetScannerDesktop.Services;

/// <summary>
/// Fallback save target when the file picker is unavailable: a plain
/// write under Documents\NetScanner, the same packaged or unpackaged.
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
}
