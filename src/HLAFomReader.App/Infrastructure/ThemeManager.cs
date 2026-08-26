using System;
using System.Linq;
using System.Windows;
using HLAFomReader.Core.Registry;
using Microsoft.Win32;

namespace HLAFomReader.App.Infrastructure;

/// <summary>The two directions of the Precision theme.</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Owns which theme dictionary is merged into the application, and swaps it live.
/// </summary>
/// <remarks>
/// <para>
/// The two dictionaries expose an identical key set, so every style is written once against the
/// keys and works in either direction. That only holds if the styles reference those keys with
/// <c>{DynamicResource}</c> — a <c>{StaticResource}</c> is resolved once at load and would keep
/// painting the theme that happened to be merged at startup.
/// </para>
/// <para>
/// Swapping the dictionary is the whole mechanism; the brushes themselves are never mutated. That
/// matters because WPF freezes a <see cref="Freezable"/> used as a <see cref="Setter"/> value when
/// the style is sealed, so the tempting shortcut of recolouring the existing brushes in place
/// throws the first time it meets a sealed style.
/// </para>
/// </remarks>
public static class ThemeManager
{
    /// <summary>
    /// Absolute pack URI base for the theme dictionaries.
    /// </summary>
    /// <remarks>
    /// Absolute, and naming this assembly, rather than the relative "/Themes/…" the handoff
    /// suggests. A relative pack URI resolves against the <em>entry</em> assembly, which is the app
    /// when the app is running and the test runner when it is not — the relative form silently
    /// stops finding the dictionaries under any other host.
    /// </remarks>
    private static readonly string DictionaryBase =
        $"pack://application:,,,/{typeof(ThemeManager).Assembly.GetName().Name};component/Themes/";

    private static bool _initialised;

    /// <summary>The dictionary this class last merged, so the next swap can drop it by identity.</summary>
    private static ResourceDictionary? _applied;

    /// <summary>The theme currently merged into the application.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Whether the current theme came from Windows rather than from a choice the user made here.
    /// </summary>
    public static bool FollowsSystem { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changes and the new dictionary is in place.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Applies the theme this installation should start in: the user's remembered choice, or the
    /// Windows app preference when they have never made one.
    /// </summary>
    public static void Initialize()
    {
        var stored = Parse(AppConfig.GetThemeMode());

        FollowsSystem = stored is null;
        _initialised = true;

        Apply(stored ?? ReadSystemPreference(), persist: false);
    }

    /// <summary>
    /// Switches the application to <paramref name="theme"/> and remembers the choice.
    /// </summary>
    /// <param name="theme">The theme to merge.</param>
    /// <param name="persist">
    /// False while restoring a remembered or system-derived theme at startup — there is nothing new
    /// to record, and writing then would turn "follow Windows" into an explicit choice the user
    /// never made.
    /// </param>
    public static void Apply(AppTheme theme, bool persist = true)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        // A repeated apply is not free — it re-runs every DynamicResource lookup in the window — so
        // the no-op case is filtered out here rather than at each of the call sites.
        if (_initialised && theme == Current && HasThemeDictionary(resources))
        {
            if (persist) Remember(theme);
            return;
        }

        var next = new ResourceDictionary
        {
            Source = new Uri($"{DictionaryBase}Precision.{theme}.xaml", UriKind.Absolute),
        };

        var merged = resources.MergedDictionaries;

        // Everything currently claiming to be a theme, collected before the new one goes in so it
        // cannot collect itself. All of them go, not just the first: leaving one behind is exactly
        // the bug this replaced.
        var stale = merged.Where(IsThemeDictionary).ToList();
        if (_applied is not null && !stale.Contains(_applied)) stale.Add(_applied);

        // Appended rather than inserted at the front, and appended *before* the old ones are
        // removed. Both halves of that matter. WPF searches merged dictionaries in reverse order,
        // so the last one added is the one that wins — put at index 0 it would lose to any stale
        // theme still in the collection, which is a silent no-op rather than an error. Adding
        // first also means a DynamicResource lookup running between the two operations never sees
        // an application with no theme at all.
        merged.Add(next);

        foreach (var dictionary in stale)
            merged.Remove(dictionary);

        _applied = next;
        Current = theme;
        _initialised = true;

        if (persist) Remember(theme);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Remember(AppTheme theme)
    {
        FollowsSystem = false;
        AppConfig.SetThemeMode(theme.ToString());
    }

    private static bool HasThemeDictionary(ResourceDictionary resources) =>
        resources.MergedDictionaries.Any(IsThemeDictionary);

    /// <summary>
    /// Whether <paramref name="dictionary"/> is one of the theme dictionaries.
    /// </summary>
    /// <remarks>
    /// Two tests, because the file name is not reliable on its own: the dictionary App.xaml merges
    /// carries the relative URI it was authored with, while the ones merged here carry an absolute
    /// pack URI, and a renamed file would silently match neither. The marker key is the definitive
    /// answer — only a theme dictionary declares it, and it is declared directly rather than in a
    /// child, so this does not walk the whole tree.
    /// </remarks>
    private static bool IsThemeDictionary(ResourceDictionary dictionary) =>
        (dictionary.Source is { } source
         && source.OriginalString.Contains("Precision.", StringComparison.OrdinalIgnoreCase))
        || dictionary.Contains(MarkerKey);

    /// <summary>A key only the theme dictionaries define, used to recognise one by its contents.</summary>
    private const string MarkerKey = "AppBackgroundColor";

    private static AppTheme? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => null,
    };

    /// <summary>
    /// What Windows says apps should use. Absent or unreadable means light, which is the Windows
    /// default when the value has never been written.
    /// </summary>
    private static AppTheme ReadSystemPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return AppTheme.Light;
        }
    }
}
