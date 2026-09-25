using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetScannerDesktop.Models;

namespace NetScannerDesktop.Services;

/// <summary>
/// Historical port scan entry for a specific host.
/// </summary>
public sealed class HostPortHistory
{
    public string IpAddress { get; set; } = string.Empty;
    public DateTime ScannedAt { get; set; } = DateTime.Now;
    public string PortRange { get; set; } = string.Empty;

    /// <summary>Open ports with the names the engine gave them, ascending.</summary>
    public List<PortResult> OpenPorts { get; set; } = new();

    /// <summary>
    /// Port numbers only, as history.json stored them before ports had
    /// names. Read once and folded into <see cref="OpenPorts"/> on load;
    /// never written again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? OpenPortNumbers { get; set; }

    internal void MigrateLegacyPorts()
    {
        if (OpenPorts.Count == 0 && OpenPortNumbers is { Count: > 0 } numbers)
        {
            OpenPorts = numbers.Order().Select(p => new PortResult(p)).ToList();
        }

        OpenPortNumbers = null;
    }
}

/// <summary>
/// Manages persistent scan history across the application so that scanning ports
/// for an IP persists when navigating between Discovery and Port scan, across sessions,
/// and when switching targets.
/// </summary>
public static class ScanHistoryService
{
    private static readonly Dictionary<string, HostPortHistory> History = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SyncRoot = new();

    public static event Action<string, HostPortHistory>? HistoryUpdated;
    public static event Action? HistoryCleared;

    static ScanHistoryService()
    {
        LoadFromDisk();
    }

    public static void Record(string ip, string portRange, IEnumerable<PortResult> openPorts)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        string cleanIp = ip.Trim();
        var entry = new HostPortHistory
        {
            IpAddress = cleanIp,
            ScannedAt = DateTime.Now,
            PortRange = portRange,
            OpenPorts = openPorts.OrderBy(p => p.Port).ToList()
        };

        lock (SyncRoot)
        {
            History[cleanIp] = entry;
            SaveToDisk();
        }

        HistoryUpdated?.Invoke(cleanIp, entry);
    }

    public static bool TryGet(string ip, out HostPortHistory? entry)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            entry = null;
            return false;
        }

        lock (SyncRoot)
        {
            return History.TryGetValue(ip.Trim(), out entry);
        }
    }

    public static IReadOnlyList<HostPortHistory> GetAll()
    {
        lock (SyncRoot)
        {
            return History.Values.OrderByDescending(h => h.ScannedAt).ToList();
        }
    }

    public static void Clear()
    {
        lock (SyncRoot)
        {
            History.Clear();
            DeleteDiskFile();
        }

        HistoryCleared?.Invoke();
    }

    private static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScanner", "history.json");

    private static void LoadFromDisk()
    {
        try
        {
            string path = HistoryFilePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize(json, HistoryJsonContext.Default.ListHostPortHistory);
                if (items != null)
                {
                    lock (SyncRoot)
                    {
                        foreach (var item in items)
                        {
                            if (!string.IsNullOrWhiteSpace(item.IpAddress))
                            {
                                item.MigrateLegacyPorts();
                                History[item.IpAddress] = item;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort; never crash startup for corrupted history.
        }
    }

    private static void SaveToDisk()
    {
        try
        {
            string path = HistoryFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(History.Values.ToList(), HistoryJsonContext.Default.ListHostPortHistory);
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch
        {
            // Disk persistence is best-effort.
        }
    }

    private static void DeleteDiskFile()
    {
        try
        {
            string path = HistoryFilePath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Source-generated serializer for history.json: no runtime reflection,
/// so it keeps working in trimmed Release builds. Computed (get-only)
/// properties such as <see cref="PortResult.CategoryLabel"/> stay out of
/// the file.
/// </summary>
[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(List<HostPortHistory>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext
{
}
