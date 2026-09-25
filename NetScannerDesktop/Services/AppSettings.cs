using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace NetScannerDesktop.Services;

/// <summary>
/// Lightweight persistence via ApplicationData LocalSettings.
/// Stores last-used form values + MRU lists so the app feels native
/// across restarts. All methods are safe to call unpackaged (falls back
/// to in-memory when LocalSettings is unavailable).
/// </summary>
public static class AppSettings
{
    private const int MaxRecent = 8;

    private static readonly Dictionary<string, object?> MemoryFallback = new();
    private static readonly string SettingsFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScanner",
        "settings.json");
    private static readonly object FileSync = new();
    private static bool fileLoaded = false;

    private static void EnsureFileLoaded()
    {
        if (fileLoaded) return;
        lock (FileSync)
        {
            if (fileLoaded) return;
            try
            {
                if (System.IO.File.Exists(SettingsFilePath))
                {
                    string json = System.IO.File.ReadAllText(SettingsFilePath);
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            switch (kvp.Value.ValueKind)
                            {
                                case System.Text.Json.JsonValueKind.String:
                                    MemoryFallback[kvp.Key] = kvp.Value.GetString();
                                    break;
                                case System.Text.Json.JsonValueKind.Number:
                                    if (kvp.Value.TryGetInt32(out int i))
                                        MemoryFallback[kvp.Key] = i;
                                    break;
                                case System.Text.Json.JsonValueKind.True:
                                case System.Text.Json.JsonValueKind.False:
                                    MemoryFallback[kvp.Key] = kvp.Value.GetBoolean();
                                    break;
                            }
                        }
                    }
                }
            }
            catch { }
            fileLoaded = true;
        }
    }

    private static void SaveToFile()
    {
        lock (FileSync)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(SettingsFilePath)!;
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                string json = System.Text.Json.JsonSerializer.Serialize(MemoryFallback, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }
    }

    // Accessing ApplicationData.Current without package identity throws a
    // WinRT InvalidOperationException. Probe once and cache the result so
    // every Get/Set doesn't throw+catch (noisy in the debugger and slow).
    private static bool? hasIdentity;

    private static ApplicationDataContainer? Local
    {
        get
        {
            if (hasIdentity == false)
            {
                return null;
            }

            try
            {
                var local = ApplicationData.Current?.LocalSettings;
                hasIdentity = true;
                return local;
            }
            catch
            {
                hasIdentity = false;
                return null;
            }
        }
    }

    public static string GetString(string key, string defaultValue = "")
    {
        try
        {
            if (Local is { } local && local.Values.TryGetValue(key, out object? v) && v is string s)
            {
                return s;
            }
        }
        catch { }

        EnsureFileLoaded();
        return MemoryFallback.TryGetValue(key, out object? m) && m is string ms ? ms : defaultValue;
    }

    public static void SetString(string key, string value)
    {
        MemoryFallback[key] = value;
        SaveToFile();
        try { if (Local is { } local) { local.Values[key] = value; } } catch { }
    }

    public static bool GetBool(string key, bool defaultValue = false)
    {
        try
        {
            if (Local is { } local && local.Values.TryGetValue(key, out object? v) && v is bool b)
            {
                return b;
            }
        }
        catch { }

        EnsureFileLoaded();
        return MemoryFallback.TryGetValue(key, out object? m) && m is bool mb ? mb : defaultValue;
    }

    public static void SetBool(string key, bool value)
    {
        MemoryFallback[key] = value;
        SaveToFile();
        try { if (Local is { } local) { local.Values[key] = value; } } catch { }
    }

    public static int GetInt(string key, int defaultValue = 0)
    {
        try
        {
            if (Local is { } local && local.Values.TryGetValue(key, out object? v) && v is int i)
            {
                return i;
            }
        }
        catch { }

        EnsureFileLoaded();
        return MemoryFallback.TryGetValue(key, out object? m) && m is int mi ? mi : defaultValue;
    }

    public static void SetInt(string key, int value)
    {
        MemoryFallback[key] = value;
        SaveToFile();
        try { if (Local is { } local) { local.Values[key] = value; } } catch { }
    }

    public static IReadOnlyList<string> GetRecent(string key)
    {
        string raw = GetString(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static void PushRecent(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string item = value.Trim();
        var list = GetRecent(key).Where(s => !s.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, item);
        while (list.Count > MaxRecent)
        {
            list.RemoveAt(list.Count - 1);
        }

        SetString(key, string.Join("|", list));
    }

    // Well-known keys -----------------------------------------------------

    public const string DiscoverySubnet = "discovery.subnet";
    public const string DiscoveryUsePing = "discovery.usePing";
    public const string DiscoveryRecent = "discovery.recent";

    public const string PortIp = "ports.ip";
    public const string PortRange = "ports.range";
    public const string PortTimeout = "ports.timeout";
    public const string PortRecent = "ports.recent";

    public const string DefaultPortRange = "defaults.portRange";
    public const string DefaultTimeout = "defaults.timeoutMs";

    public const string AppTheme = "app.theme"; // 0=default, 1=light, 2=dark
    public const string SeenTeachingTip = "app.seenTeachingTip";
}
