using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Pins the light/dark pairing. Two things have to hold for a live theme switch to work at all:
/// the dictionaries have to expose the same keys, and the shell has to actually repaint when one
/// is swapped for the other. Neither is visible in a build failure — both fail as a screen that is
/// half the wrong colour.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemeTests
{
    private readonly WpfAppFixture _wpf;

    public ThemeTests(WpfAppFixture wpf)
    {
        _wpf = wpf;
    }

    /// <summary>
    /// Every control style is written once against the key names, so a key present in one direction
    /// and missing from the other is a control that renders in one theme and not the other.
    /// </summary>
    [Fact]
    public void TheTwoDirectionsExposeTheSameKeys()
    {
        _wpf.Invoke(() =>
        {
            var light = Load(AppTheme.Light);
            var dark = Load(AppTheme.Dark);

            var lightKeys = Keys(light);
            var darkKeys = Keys(dark);

            Assert.Empty(lightKeys.Except(darkKeys, StringComparer.Ordinal));
            Assert.Empty(darkKeys.Except(lightKeys, StringComparer.Ordinal));

            // Same key, same kind of thing: a brush in one and a colour in the other would satisfy
            // the check above and still fail the first time a Background bound to it.
            foreach (var key in lightKeys)
                Assert.Equal(dark[key]!.GetType(), light[key]!.GetType());

            // And the two directions have to actually differ, or one of them is a copy of the other.
            var lightBackground = ((SolidColorBrush)light["AppBackground"]!).Color;
            var darkBackground = ((SolidColorBrush)dark["AppBackground"]!).Color;
            Assert.NotEqual(lightBackground, darkBackground);
        });
    }

    /// <summary>
    /// The Excel export takes its colours from whichever theme is merged at the time.
    /// </summary>
    /// <remarks>
    /// The failure this exists for is silent. <see cref="ExportPalette"/> looks its colours up by
    /// key and falls back when there is no application to ask, so a key that got renamed — or was
    /// never spelled right — does not throw and does not fail a build. It hands back the light
    /// fallback for both directions, and the only symptom is a dark-theme export that comes out
    /// light. Asserting the two differ is what catches that.
    /// </remarks>
    [Fact]
    public void TheExportPaletteFollowsTheMergedTheme()
    {
        _wpf.Invoke(() =>
        {
            var startingTheme = ThemeManager.Current;

            try
            {
                ThemeManager.Apply(AppTheme.Light, persist: false);
                var light = ExportPalette.Current();

                ThemeManager.Apply(AppTheme.Dark, persist: false);
                var dark = ExportPalette.Current();

                Assert.NotEqual(light.HeaderFill, dark.HeaderFill);
                Assert.NotEqual(light.HeaderText, dark.HeaderText);
                Assert.NotEqual(light.GridLine, dark.GridLine);

                // Not the fallback in either direction: that is what a mistyped key looks like.
                Assert.NotEqual(XlsxPalette.Default.HeaderFill, dark.HeaderFill);

                // Eight hex digits, or the workbook is written with the fallback instead.
                foreach (var colour in new[] { light.HeaderFill, light.HeaderText, light.GridLine,
                                               dark.HeaderFill, dark.HeaderText, dark.GridLine })
                {
                    Assert.Matches("^[0-9A-F]{8}$", colour);
                }

                // A dark header needs light text on it and the reverse, or the band is unreadable.
                Assert.True(Luminance(dark.HeaderFill) < Luminance(dark.HeaderText),
                    "the dark theme's header text is no lighter than the fill behind it");
                Assert.True(Luminance(light.HeaderFill) > Luminance(light.HeaderText),
                    "the light theme's header text is no darker than the fill behind it");
            }
            finally
            {
                ThemeManager.Apply(startingTheme, persist: false);
            }
        });
    }

    /// <summary>Rough brightness of an <c>AARRGGBB</c> colour, enough to tell dark from light.</summary>
    private static double Luminance(string argb)
    {
        var r = Convert.ToInt32(argb.Substring(2, 2), 16);
        var g = Convert.ToInt32(argb.Substring(4, 2), 16);
        var b = Convert.ToInt32(argb.Substring(6, 2), 16);

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>
    /// The switch is a dictionary swap, which only reaches the screen through
    /// <c>{DynamicResource}</c>. A style that still used <c>{StaticResource}</c> would keep its
    /// startup colour here while everything around it changed.
    /// </summary>
    [Fact]
    public void SwitchingTheThemeRepaintsTheShell()
    {
        _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-theme-{Guid.NewGuid():N}.db");

            var startingTheme = ThemeManager.Current;
            MainWindow? window = null;

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();

                window = new MainWindow
                {
                    DataContext = shell,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                };

                window.Show();
                Layout(window);

                var sidebar = (FrameworkElement)window.FindName("Sidebar");

                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Light })
                {
                    // persist: false — a test must not write the developer's own theme choice into
                    // the config file sitting beside the test binaries.
                    ThemeManager.Apply(theme, persist: false);
                    Layout(window);

                    var expected = Load(theme);

                    Assert.Equal(Colour(expected["AppBackground"]), Colour(window.Background));
                    Assert.Equal(Colour(expected["SurfaceAltBackground"]), Colour(((System.Windows.Controls.Border)sidebar).Background));

                    // The view model behind the Settings control has to agree with what is on
                    // screen, or the segmented pair will show the mode the app just left.
                    Assert.Equal(theme, shell.Settings.Theme);
                }

                shell.Dispose();
            }
            finally
            {
                window?.Close();
                DrainDispatcher();

                ThemeManager.Apply(startingTheme, persist: false);

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath)) File.Delete(databasePath);
            }
        });
    }

    /// <summary>
    /// Exactly one theme dictionary stays merged, however many times the theme is switched.
    /// </summary>
    /// <remarks>
    /// This is the failure that does not look like one. WPF searches merged dictionaries in reverse
    /// order, so a stale theme left behind at a higher index quietly outvotes the one just added:
    /// the switch reports success, the setting is written to disk, and the window does not change
    /// colour. Counting the dictionaries is the only place that shows up before a person does.
    /// </remarks>
    [Fact]
    public void SwitchingLeavesExactlyOneThemeMerged()
    {
        _wpf.Invoke(() =>
        {
            var startingTheme = ThemeManager.Current;

            try
            {
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Light, AppTheme.Light })
                {
                    ThemeManager.Apply(theme, persist: false);

                    var themes = Application.Current.Resources.MergedDictionaries
                        .Where(d => d.Contains("AppBackgroundColor"))
                        .ToList();

                    Assert.Single(themes);

                    // And the survivor has to be the one that was asked for, not merely a survivor.
                    var expected = ((SolidColorBrush)Load(theme)["AppBackground"]!).Color;
                    Assert.Equal(expected, Colour(Application.Current.TryFindResource("AppBackground")));
                }
            }
            finally
            {
                ThemeManager.Apply(startingTheme, persist: false);
            }
        });
    }

    /// <summary>
    /// A theme dictionary the swap cannot recognise by its file name still gets replaced.
    /// </summary>
    /// <remarks>
    /// This is the shape of the bug that shipped. The dictionary App.xaml merges does not
    /// necessarily report the same <c>Source</c> as the ones merged at runtime, so matching on the
    /// file name alone can miss it — and a missed dictionary is not inert. WPF searches merged
    /// dictionaries in reverse order, so the one left behind sat *after* the new theme and quietly
    /// outvoted it: the app wrote "Light" to its settings file and stayed dark.
    ///
    /// The stand-in here has no <c>Source</c> at all, which is the worst case of the same thing.
    /// </remarks>
    [Fact]
    public void AnUnrecognisableThemeDictionaryIsStillReplaced()
    {
        _wpf.Invoke(() =>
        {
            var startingTheme = ThemeManager.Current;
            var merged = Application.Current.Resources.MergedDictionaries;

            // Nameless, and loud enough that surviving would be unmistakable.
            var impostor = new ResourceDictionary
            {
                { "AppBackgroundColor", Colors.Magenta },
                { "AppBackground", new SolidColorBrush(Colors.Magenta) },
            };

            merged.Add(impostor);

            try
            {
                ThemeManager.Apply(AppTheme.Light, persist: false);

                Assert.DoesNotContain(impostor, merged);
                Assert.Single(merged, d => d.Contains("AppBackgroundColor"));

                var expected = ((SolidColorBrush)Load(AppTheme.Light)["AppBackground"]!).Color;
                Assert.Equal(expected, Colour(Application.Current.TryFindResource("AppBackground")));
            }
            finally
            {
                merged.Remove(impostor);
                ThemeManager.Apply(startingTheme, persist: false);
            }
        });
    }

    /// <summary>
    /// Choosing a mode has to outlast the process, and "never chosen" has to stay distinguishable
    /// from "chose light" — otherwise a first run can never follow Windows.
    /// </summary>
    [Fact]
    public void TheChosenThemeIsRemembered()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hlafomreader-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Null(AppConfig.GetThemeMode(directory));

            AppConfig.SetThemeMode("Light", directory);
            Assert.Equal("Light", AppConfig.GetThemeMode(directory));

            AppConfig.SetThemeMode("Dark", directory);
            Assert.Equal("Dark", AppConfig.GetThemeMode(directory));

            // Setting the theme must not disturb the database the config file is really for.
            AppConfig.SetLastDatabasePath(Path.Combine(directory, "registry.db"), directory);
            AppConfig.SetThemeMode("Light", directory);
            Assert.Equal(Path.Combine(directory, "registry.db"), AppConfig.GetLastDatabasePath(directory));

            AppConfig.SetThemeMode(null, directory);
            Assert.Null(AppConfig.GetThemeMode(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ResourceDictionary Load(AppTheme theme) => new()
    {
        Source = new Uri(
            $"pack://application:,,,/HLAFomReader;component/Themes/Precision.{theme}.xaml",
            UriKind.Absolute),
    };

    private static List<string> Keys(ResourceDictionary dictionary) =>
        dictionary.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static Color Colour(object? brush) =>
        brush is SolidColorBrush solid ? solid.Color : throw new InvalidOperationException($"not a solid brush: {brush}");

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        DrainDispatcher();
        window.UpdateLayout();
    }

    private static void DrainDispatcher()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(15);
        }
    }

    /// <summary>Dialog service that never shows UI, so the test cannot block.</summary>
    private sealed class SilentDialogs : IDialogService
    {
        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;
        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => null;
        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public void ShowInfo(string title, string message) { }
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) { }

        /// <summary>Cancelled rather than answered, so no test can start an export unattended.</summary>
        public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model) => null;
    }
}
