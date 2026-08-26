using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HLAFomReader.Core.Registry;

/// <summary>
/// The settings file that lives beside the executable and records which registry database to open.
/// </summary>
/// <remarks>
/// This cannot live inside the database, because it is read <em>before</em> any database is open —
/// it is what names the database to open.
/// </remarks>
public static class AppConfig
{
    private const string ConfigFileName = "config.json";

    private const int MaxRecentDatabases = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The folder holding the running executable, which is where the config and the default
    /// database live.
    /// </summary>
    /// <remarks>
    /// Deliberately derived from <see cref="Environment.ProcessPath"/> rather than
    /// <c>AppContext.BaseDirectory</c>. In a single-file published build those two are NOT the same:
    /// if the bundle extracts itself, BaseDirectory points at a randomly named folder under
    /// %TEMP%\.net\ that changes on every launch. Writing settings there means they are silently
    /// lost between runs — which is exactly the bug this replaced. ProcessPath is the real exe in
    /// every configuration: framework-dependent, self-contained, single-file, extracted or not.
    /// </remarks>
    public static string AppDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(directory)) return directory;
            }

            return AppContext.BaseDirectory;
        }
    }

    private static string ConfigPath(string? baseDir) =>
        Path.Combine(baseDir ?? AppDirectory, ConfigFileName);

    /// <summary>The database path last opened, or null when nothing has been chosen yet.</summary>
    public static string? GetLastDatabasePath(string? baseDir = null) =>
        Load(baseDir).LastDatabasePath;

    /// <summary>Records <paramref name="databasePath"/> as the database to reopen next launch.</summary>
    public static void SetLastDatabasePath(string databasePath, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) return;

        var settings = Load(baseDir);
        var trimmed = databasePath.Trim();

        settings.LastDatabasePath = trimmed;

        // Most recent first, no duplicates, bounded.
        var recent = new List<string> { trimmed };
        recent.AddRange(settings.Recent.Where(p =>
            !string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)));

        settings.Recent = recent.Take(MaxRecentDatabases).ToList();

        Save(settings, baseDir);
    }

    /// <summary>Databases opened before, most recent first.</summary>
    public static IReadOnlyList<string> GetRecentDatabases(string? baseDir = null) =>
        Load(baseDir).Recent;

    /// <summary>Whether the shell's sidebar was last left collapsed to its icon rail.</summary>
    public static bool GetSidebarCollapsed(string? baseDir = null) =>
        Load(baseDir).SidebarCollapsed;

    /// <summary>Remembers the sidebar state, so a laptop-sized window stays that way next launch.</summary>
    public static void SetSidebarCollapsed(bool collapsed, string? baseDir = null)
    {
        var settings = Load(baseDir);
        if (settings.SidebarCollapsed == collapsed) return;

        settings.SidebarCollapsed = collapsed;
        Save(settings, baseDir);
    }

    /// <summary>
    /// The theme the user last chose, or null when they have never chosen one.
    /// </summary>
    /// <remarks>
    /// Null is a real answer, not a missing one: it means "follow Windows", which is what a first
    /// launch must do. Once the user picks a mode themselves that choice outranks the OS, because
    /// the reason people override the system theme is that the system theme is wrong for them.
    /// </remarks>
    public static string? GetThemeMode(string? baseDir = null) =>
        Load(baseDir).ThemeMode;

    /// <summary>Records the chosen theme. Null clears the choice and hands the decision back to Windows.</summary>
    public static void SetThemeMode(string? mode, string? baseDir = null)
    {
        var normalised = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();

        var settings = Load(baseDir);
        if (string.Equals(settings.ThemeMode, normalised, StringComparison.Ordinal)) return;

        settings.ThemeMode = normalised;
        Save(settings, baseDir);
    }

    /// <summary>Forgets the remembered database, so the next launch asks again.</summary>
    public static void Clear(string? baseDir = null)
    {
        TryDelete(ConfigPath(baseDir));
    }

    private static AppSettings Load(string? baseDir)
    {
        // Every read is best-effort. A malformed, locked or unreadable settings file must degrade to
        // "nothing remembered" and let the user pick, never stop the app starting.
        try
        {
            var path = ConfigPath(baseDir);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                    if (settings is not null)
                    {
                        settings.Normalise();
                        return settings;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A settings file that cannot be read is the same as no settings file: the user picks a
            // database this session and the choice is written afresh.
        }

        return new AppSettings();
    }

    private static void Save(AppSettings settings, string? baseDir)
    {
        try
        {
            var path = ConfigPath(baseDir);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) return;

            File.WriteAllText(path, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an installation in a read-only folder still works for this session, it
            // just cannot remember the choice.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignored
        }
    }

    /// <summary>On-disk shape of <c>config.json</c>.</summary>
    private sealed class AppSettings
    {
        [JsonPropertyName("lastDatabasePath")]
        public string? LastDatabasePath { get; set; }

        [JsonPropertyName("recentDatabases")]
        public List<string> Recent { get; set; } = new();

        [JsonPropertyName("sidebarCollapsed")]
        public bool SidebarCollapsed { get; set; }

        [JsonPropertyName("theme")]
        public string? ThemeMode { get; set; }

        /// <summary>Tidies values a hand-edited file might contain.</summary>
        public void Normalise()
        {
            LastDatabasePath = string.IsNullOrWhiteSpace(LastDatabasePath)
                ? null
                : LastDatabasePath.Trim();

            ThemeMode = string.IsNullOrWhiteSpace(ThemeMode) ? null : ThemeMode.Trim();

            Recent = (Recent ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentDatabases)
                .ToList();
        }
    }
}
