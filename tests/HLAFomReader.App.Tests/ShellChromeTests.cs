using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Builds the shell window itself — the one screen the other UI tests never touch, because they
/// host the views directly. What it pins is the sidebar: collapsing it has to actually hand the
/// space to the screen, and the rail left behind has to stay navigable.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ShellChromeTests
{
    /// <summary>Sidebar widths from the design reference: labels at 196 DIP, icon rail at 52.</summary>
    private const double ExpandedWidth = 196d;

    private const double CollapsedWidth = 52d;

    private readonly ITestOutputHelper _output;
    private readonly WpfAppFixture _wpf;

    public ShellChromeTests(ITestOutputHelper output, WpfAppFixture wpf)
    {
        _output = output;
        _wpf = wpf;
    }

    [Fact]
    public void CollapsingTheSidebarGivesItsWidthToTheScreen()
    {
        var failures = _wpf.Invoke(() =>
        {
            var problems = new List<string>();

            var listener = new CollectingTraceListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-shell-{Guid.NewGuid():N}.db");
            MainWindow? window = null;

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();

                // Shown off-screen: a Window does not lay out until it has a presentation source,
                // and the width the sidebar gives up is exactly what this test is about.
                window = new MainWindow
                {
                    DataContext = shell,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                };

                window.Show();

                var sidebar = (FrameworkElement)window.FindName("Sidebar");
                var screen = (FrameworkElement)window.FindName("ScreenHost");
                var navigation = (FrameworkElement)window.FindName("NavigationList");
                var toggle = (FrameworkElement)window.FindName("SidebarToggle");

                // The control that collapses the sidebar has to live on the sidebar. In the title
                // bar it read as part of the app's identity and went unnoticed.
                Assert.Contains(toggle, Descendants<FrameworkElement>(sidebar));

                // --- Expanded ----------------------------------------------------------------
                shell.IsSidebarCollapsed = false;
                Settle(window, sidebar);

                var expandedScreenWidth = screen.ActualWidth;

                Assert.Equal(ExpandedWidth, sidebar.ActualWidth, precision: 1);
                Assert.True(toggle.IsVisible, "nothing offers to collapse the sidebar");
                Assert.True(IsShowing(sidebar, "Registry"), "the sidebar label is missing when expanded");

                // The icon is not a collapsed-state substitute for the label — it is the row's own
                // mark, present at both widths, so the rail is the same list seen narrower.
                Assert.True(AnyIconShowing(navigation), "the navigation rows have lost their icons");

                // --- Collapsed ---------------------------------------------------------------
                shell.ToggleSidebarCommand.Execute(null);
                Settle(window, sidebar);

                Assert.True(shell.IsSidebarCollapsed);
                Assert.Equal(CollapsedWidth, sidebar.ActualWidth, precision: 1);

                // The point of the whole exercise: the records get the difference, all 144 of it.
                Assert.Equal(expandedScreenWidth + (ExpandedWidth - CollapsedWidth), screen.ActualWidth, precision: 1);

                // Collapsed still has to be navigable — every row still marked, and the screen
                // still named where a tooltip can reach it.
                Assert.False(IsShowing(sidebar, "Registry"), "the label survived the collapse");
                Assert.True(AnyIconShowing(navigation), "the rail has no icons to click");

                // The way back out has to be on the rail; otherwise collapsing is a one-way door.
                Assert.True(toggle.IsVisible, "nothing offers to expand the sidebar again");
                Assert.All(shell.NavigationItems, item =>
                    Assert.StartsWith(item.Title, item.Tooltip, StringComparison.Ordinal));

                // The database the sidebar used to name is named in the status bar at both widths.
                Assert.True(IsShowing(window, Path.GetFileName(databasePath)),
                    "nothing names the open database");

                // Navigation itself still works from the rail.
                shell.NavigateCommand.Execute(shell.NavigationItems[1]);
                Layout(window);
                Assert.Same(shell.Compare, shell.CurrentView);

                // --- Back again --------------------------------------------------------------
                shell.ToggleSidebarCommand.Execute(null);
                Settle(window, sidebar);

                Assert.False(shell.IsSidebarCollapsed);
                Assert.Equal(ExpandedWidth, sidebar.ActualWidth, precision: 1);
                Assert.Equal(expandedScreenWidth, screen.ActualWidth, precision: 1);
                Assert.True(IsShowing(sidebar, "Registry"));

                shell.Dispose();
            }
            finally
            {
                window?.Close();
                DrainDispatcher();

                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath)) File.Delete(databasePath);
            }

            problems.AddRange(listener.Messages);
            return problems;
        });

        foreach (var failure in failures)
            _output.WriteLine(failure);

        Assert.True(failures.Count == 0,
            $"WPF reported {failures.Count} binding/resource problem(s):\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Every control in a command bar has to keep its own space at the widths the app actually runs
    /// at.
    /// </summary>
    /// <remarks>
    /// A DockPanel hands each child the width it asks for, in declaration order, and whatever is
    /// left goes to the next one. Declaring a long, flexible run of text before the fixed controls
    /// therefore lets it eat their space: the ones docked after it are arranged into a rectangle
    /// that has already collapsed, and they land on top of each other. It renders as a search box
    /// sitting underneath the buttons rather than beside them, and nothing about it throws — which
    /// is why it needs pinning here rather than being left to a screenshot nobody diffs.
    ///
    /// The FOM detail bar is the one that reaches this first: its subtitle carries the standard, the
    /// version, the file name and five counts, so a real FOM pushes it past 500 DIP.
    /// </remarks>
    [Fact]
    public void CommandBarControlsDoNotOverlapEachOther()
    {
        var failures = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-bar-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                var sample = Path.Combine(SamplesDirectory, "RestaurantFOM-1516-2010.xml");
                var entry = repository.Register(FomFileReader.ParseFile(sample), "Restaurant Evolved", sample);

                // Restated as one of the vendor FOMs this app is actually pointed at. The samples in
                // the repo are small enough that their subtitle fits whatever is left over, which is
                // exactly why this went unnoticed: the bar only breaks once the subtitle is long, and
                // an RPR FED names a standard, a version, a file and five counts. The tree below still
                // comes from the sample — this is a test about the bar, not about the FOM.
                entry.DisplayName = "MAK-RPR1-1-1";
                entry.FileName = "MAK-RPR1-1-1.fed";
                entry.Standard = FomStandard.Hla13;
                entry.Version = "1-1";
                entry.ObjectClassCount = 48;
                entry.AttributeCount = 277;
                entry.InteractionClassCount = 138;
                entry.ParameterCount = 344;
                entry.DataTypeCount = 156;

                var detail = new FomDetailViewModel(repository, new SilentDialogs(), entry);
                var view = new FomDetailView { DataContext = detail };

                var found = new List<string>();

                // 1100 is the narrow end of what the shell is used at; 1600 a wide monitor. The bug
                // this pins is worse the narrower it gets, but it is not only a narrow-window bug —
                // a long enough subtitle overruns a wide bar too.
                foreach (var width in new[] { 900d, 1100d, 1280d, 1600d })
                {
                    view.Width = width;
                    view.Height = 820;
                    view.Measure(new Size(width, 820));
                    view.Arrange(new Rect(0, 0, width, 820));
                    view.UpdateLayout();

                    var bar = FindCommandBar(view);
                    Assert.NotNull(bar);

                    var placed = bar!.Children
                        .OfType<FrameworkElement>()
                        .Where(child => child.ActualWidth > 0 && child.Visibility == Visibility.Visible)
                        .Select(child => (Child: child, Bounds: BoundsIn(child, bar)))
                        .OrderBy(item => item.Bounds.Left)
                        .ToList();

                    for (var i = 1; i < placed.Count; i++)
                    {
                        var left = placed[i - 1];
                        var right = placed[i];

                        // A hairline of rounding is not an overlap; half a control sitting on top of
                        // the one beside it is.
                        var overlap = left.Bounds.Right - right.Bounds.Left;
                        if (overlap > 0.5)
                        {
                            found.Add(
                                $"at {width:0} DIP, {Describe(left.Child)} overruns {Describe(right.Child)} " +
                                $"by {overlap:0.#} DIP");
                        }
                    }

                    // Running off the end is the same defect wearing a different face: the controls
                    // stay in order, so nothing overlaps, but the last of them is arranged outside
                    // the bar and simply gets clipped away. Checking pairs alone would call that a
                    // pass while the search box sat entirely off-screen.
                    foreach (var item in placed)
                    {
                        if (item.Bounds.Right > bar.ActualWidth + 0.5)
                        {
                            found.Add(
                                $"at {width:0} DIP, {Describe(item.Child)} is arranged past the end of " +
                                $"the bar ({item.Bounds.Right:0} > {bar.ActualWidth:0})");
                        }
                    }

                    // A control squeezed to nothing is the last step of the same failure: it has not
                    // overlapped anything because it was left no width to overlap with.
                    foreach (var child in bar.Children.OfType<FrameworkElement>())
                    {
                        if (child.Visibility == Visibility.Visible && child.DesiredSize.Width > 0 && child.ActualWidth < 1)
                            found.Add($"at {width:0} DIP, {Describe(child)} was arranged to zero width");
                    }
                }

                return found;
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        foreach (var failure in failures) _output.WriteLine(failure);
        Assert.Empty(failures);
    }

    /// <summary>The command bar is the first DockPanel in the tree — the strip across the top.</summary>
    /// <summary>
    /// Nothing in the Compare filter strip is arranged past the end of the panel holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strip docks its search box to the right, which in a DockPanel means the box is handed
    /// whatever the items before it did not take. When that was nothing, the box did not shrink — it
    /// kept its 200 DIP width and was arranged beyond the panel's edge, so below roughly an 1150 DIP
    /// window it hung off the end and by 950 it was entirely off screen. The chips were never the
    /// casualty; the cause was the format-gap caveat, a sentence taking better than a quarter of the
    /// width, which now has a line of its own.
    /// </para>
    /// <para>
    /// Laid out inside a real window with the Classes tab selected, because a TabControl does not
    /// realise the content of a tab nobody is looking at — measured detached, every child of this
    /// strip reports a width of zero and the test passes without having looked at anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCompareFilterStripFitsInsideItsPanel()
    {
        var failures = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-strip-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var file in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(file),
                        Path.GetFileNameWithoutExtension(file), file);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;

                // The cross-standard pair at Full depth, which is the loudest this strip ever gets:
                // a five-figure headline and the longest form of the format-gap caveat.
                compare.Left = compare.Sources.First(e => e.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(e => e.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.IsFullDepth = true;

                var view = new CompareView { DataContext = compare };
                var window = new Window
                {
                    Content = view,
                    Width = 1400,
                    Height = 900,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                };

                window.Show();
                try
                {
                    RegistryHarness.Execute(compare.CompareCommand);
                    Assert.True(compare.HasFormatGapNote, "the pair produced no format-gap caveat");

                    Descendants<TabControl>(view).First().SelectedIndex = 1;
                    window.UpdateLayout();
                    DrainDispatcher();

                    var found = new List<string>();

                    foreach (var width in new[] { 860d, 950d, 1100d, 1280d, 1600d })
                    {
                        window.Width = width;
                        window.UpdateLayout();
                        DrainDispatcher();
                        window.UpdateLayout();

                        var strip = Descendants<DockPanel>(view)
                            .FirstOrDefault(panel => panel.Children.OfType<ToggleButton>().Count() == 4);

                        if (strip is null)
                        {
                            found.Add($"{width}: the filter strip was not found");
                            continue;
                        }

                        foreach (var child in strip.Children.OfType<FrameworkElement>())
                        {
                            if (child.Visibility != Visibility.Visible || child.ActualWidth <= 0) continue;

                            var bounds = BoundsIn(child, strip);
                            if (bounds.Right <= strip.ActualWidth + 0.5) continue;

                            found.Add(
                                $"{width}: {Describe(child)} is arranged to {bounds.Right:F0} in a "
                                + $"{strip.ActualWidth:F0} DIP strip — {bounds.Right - strip.ActualWidth:F0} over the end");
                        }
                    }

                    return found;
                }
                finally
                {
                    window.Close();
                    DrainDispatcher();
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static DockPanel? FindCommandBar(DependencyObject root)
    {
        if (root is DockPanel panel) return panel;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindCommandBar(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }

        return null;
    }

    private static Rect BoundsIn(FrameworkElement child, Visual reference)
    {
        var origin = child.TransformToAncestor(reference).Transform(new Point(0, 0));
        return new Rect(origin, new Size(child.ActualWidth, child.ActualHeight));
    }

    /// <summary>Names a control the way the XAML does, so a failure points at the line to edit.</summary>
    private static string Describe(FrameworkElement element) => element switch
    {
        ContentControl { Content: string text } => $"\"{text}\"",
        TextBlock block => $"text \"{Truncate(block.Text)}\"",
        _ => element.GetType().Name,
    };

    private static string Truncate(string? text) =>
        text is { Length: > 32 } ? text[..32] + "…" : text ?? "";

    private static string SamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    /// <summary>
    /// Settings is where the app answers "which build is this?", so what it shows has to be the
    /// build's own metadata rather than anything typed into the XAML.
    /// </summary>
    [Fact]
    public void TheSettingsScreenReportsThisBuild()
    {
        _wpf.Invoke(() =>
        {
            // Read from the app assembly, not the entry assembly — under a test host the latter is
            // the runner, and every value here would describe xunit instead.
            Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Version), "no version");
            Assert.False(string.IsNullOrWhiteSpace(BuildInfo.ProductName), "no product name");
            Assert.DoesNotContain("testhost", BuildInfo.ProductName, StringComparison.OrdinalIgnoreCase);

            // The number a user quotes must not carry the commit metadata SourceLink appends; the
            // commit belongs beside it, in brackets.
            Assert.DoesNotContain("+", BuildInfo.Version, StringComparison.Ordinal);
            Assert.StartsWith(BuildInfo.Version, BuildInfo.VersionSummary, StringComparison.Ordinal);

            if (BuildInfo.Commit is { } commit)
            {
                Assert.Equal(7, commit.Length);
                Assert.Contains($"({commit})", BuildInfo.VersionSummary, StringComparison.Ordinal);
            }

            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-about-{Guid.NewGuid():N}.db");
            MainWindow? window = null;

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();

                // Shown off-screen: this is here to lay the XAML out, so a mistyped resource key
                // fails now rather than the first time somebody opens Settings.
                window = new MainWindow
                {
                    DataContext = shell,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                };

                window.Show();

                shell.Navigate(AppScreen.Settings);
                Layout(window);

                Assert.Same(shell.Settings, shell.CurrentView);

                var texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();

                // Whatever the screen prints as the version has to be the assembly's own…
                Assert.Contains(texts, t => t.Contains(BuildInfo.Version, StringComparison.Ordinal));

                // …and the database it names has to be the one that is actually open, because this
                // is now the screen that offers to change it.
                Assert.Contains(texts, t => t.Contains(Path.GetFileName(databasePath), StringComparison.Ordinal));

                // Both directions readable at once is the whole point of the segmented treatment.
                Assert.True(IsShowing(window, "Light"), "the light option is not on the settings screen");
                Assert.True(IsShowing(window, "Dark"), "the dark option is not on the settings screen");

                shell.Dispose();
            }
            finally
            {
                window?.Close();
                DrainDispatcher();

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath)) File.Delete(databasePath);
            }
        });
    }

    /// <summary>True when some visible <see cref="TextBlock"/> under <paramref name="root"/> reads exactly this.</summary>
    private static bool IsShowing(DependencyObject root, string text) =>
        Descendants<TextBlock>(root).Any(t => t.IsVisible && t.Text.Equals(text, StringComparison.Ordinal));

    /// <summary>True when a navigation row is drawing its icon.</summary>
    private static bool AnyIconShowing(DependencyObject root) =>
        Descendants<System.Windows.Shapes.Path>(root).Any(p => p.IsVisible && p.Data is not null);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;

            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        DrainDispatcher();
        window.UpdateLayout();
    }

    /// <summary>
    /// Lays out until <paramref name="panel"/> stops moving.
    /// </summary>
    /// <remarks>
    /// The sidebar animates its width over 180 ms, so a single layout pass would measure it
    /// mid-flight. Waiting for the width to repeat rather than sleeping a fixed time keeps the test
    /// honest on a slow machine without making it slow on a fast one.
    /// </remarks>
    private static void Settle(Window window, FrameworkElement panel)
    {
        var previous = double.NaN;

        for (var i = 0; i < 40; i++)
        {
            Layout(window);

            var width = panel.ActualWidth;
            if (i > 0 && width == previous) return;

            previous = width;
        }
    }

    /// <summary>Lets queued dispatcher work finish, so a re-templated row is really re-templated.</summary>
    private static void DrainDispatcher()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(15);
        }
    }

    /// <summary>Collects WPF data-binding trace output so a broken binding fails the test.</summary>
    private sealed class CollectingTraceListener : TraceListener
    {
        private readonly List<string> _messages = new();
        private string _pending = "";

        public IReadOnlyList<string> Messages => _messages;

        public override void Write(string? message) => _pending += message;

        public override void WriteLine(string? message)
        {
            var full = (_pending + message).Trim();
            _pending = "";

            if (full.Length == 0) return;

            if (full.Contains("path error", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot find source", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot find governing", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot convert", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Add(full);
            }
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
    }
}
