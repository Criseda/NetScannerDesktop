using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public List<int> OpenPortNumbers { get; set; } = new();

    public List<PortResult> GetPortResults() =>
        OpenPortNumbers.OrderBy(p => p).Select(p => new PortResult(p)).ToList();

    public string SummaryText
    {
        get
        {
            if (OpenPortNumbers.Count == 0)
            {
                return "No open ports";
            }

            var items = OpenPortNumbers.Take(4).Select(p =>
            {
                string svc = WellKnownPorts.GetServiceName(p);
                return string.IsNullOrEmpty(svc) ? p.ToString() : $"{p} ({svc})";
            });

            string text = "Open: " + string.Join(", ", items);
            if (OpenPortNumbers.Count > 4)
            {
                text += $", +{OpenPortNumbers.Count - 4} more";
            }

            return text;
        }
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
            OpenPortNumbers = openPorts.Select(p => p.Port).OrderBy(p => p).ToList()
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
                var items = JsonSerializer.Deserialize<List<HostPortHistory>>(json);
                if (items != null)
                {
                    lock (SyncRoot)
                    {
                        foreach (var item in items)
                        {
                            if (!string.IsNullOrWhiteSpace(item.IpAddress))
                            {
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

            string json = JsonSerializer.Serialize(History.Values.ToList());
            File.WriteAllText(path, json);
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
