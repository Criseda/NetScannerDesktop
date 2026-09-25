using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetScannerDesktop.Services;

/// <summary>
/// App settings (last-used form values, MRU lists, preferences) in one
/// JSON file: %LOCALAPPDATA%\NetScanner\settings.json. The same store
/// packaged and unpackaged (MSIX redirects the folder per package), so
/// there is no second copy to drift out of sync.
/// <para>
/// JsonNode keeps this reflection-free, which keeps it safe under
/// trimming. Writes go to a temp file first, so a crash mid-write never
/// corrupts the settings.
/// </para>
/// </summary>
public static class AppSettings
{
    private const int MaxRecent = 8;

    private static readonly object Sync = new();
    private static JsonObject? values;

    /// <summary>Overridable for tests.</summary>
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetScanner",
        "settings.json");

    private static JsonObject Values
    {
        get
        {
            lock (Sync)
            {
                return values ??= Load();
            }
        }
    }

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject loaded)
            {
                return loaded;
            }
        }
        catch
        {
            // Unreadable settings fall back to defaults rather than blocking startup.
        }

        JsonObject fresh = ImportPackagedSettings();
        if (fresh.Count > 0)
        {
            Save(fresh);
        }

        return fresh;
    }

    /// <summary>
    /// Earlier builds also wrote ApplicationData LocalSettings when
    /// packaged. Carry those over once, the first time the file is created.
    /// </summary>
    private static JsonObject ImportPackagedSettings()
    {
        var imported = new JsonObject();
        try
        {
            foreach (KeyValuePair<string, object> pair in Windows.Storage.ApplicationData.Current.LocalSettings.Values)
            {
                JsonNode? node = pair.Value switch
                {
                    string s => JsonValue.Create(s),
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    _ => null,
                };
                if (node is not null)
                {
                    imported[pair.Key] = node;
                }
            }
        }
        catch
        {
            // No package identity (unpackaged): nothing to import.
        }

        return imported;
    }

    private static void Save(JsonObject snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; the in-memory value still applies.
        }
    }

    private static T Get<T>(string key, T defaultValue)
    {
        lock (Sync)
        {
            try
            {
                return Values[key] is JsonValue value && value.TryGetValue(out T? result) ? result : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    private static void Set(string key, JsonNode node)
    {
        lock (Sync)
        {
            Values[key] = node;
            Save(Values);
        }
    }

    public static string GetString(string key, string defaultValue = "") => Get(key, defaultValue);

    public static void SetString(string key, string value) => Set(key, JsonValue.Create(value));

    public static bool GetBool(string key, bool defaultValue = false) => Get(key, defaultValue);

    public static void SetBool(string key, bool value) => Set(key, JsonValue.Create(value));

    public static int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);

    public static void SetInt(string key, int value) => Set(key, JsonValue.Create(value));

    public static IReadOnlyList<string> GetRecent(string key)
    {
        string raw = GetString(key);
        return string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        SetString(key, string.Join("|", list.Take(MaxRecent)));
    }

    // Well-known keys -----------------------------------------------------

    public const string DiscoverySubnet = "discovery.subnet";
    public const string DiscoveryUsePing = "discovery.usePing";
    public const string DiscoveryResolve = "discovery.resolve";
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
