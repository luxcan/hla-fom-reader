using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Renders a screen offscreen to a PNG so its layout can be inspected without launching the app and
/// clicking through it. Not an assertion of pixels — it exists so a layout change can be eyeballed,
/// and so a broken render (a throw during measure/arrange) fails loudly.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class VisualCaptureTests
{
    private readonly ITestOutputHelper _output;
    private readonly WpfAppFixture _wpf;

    public VisualCaptureTests(ITestOutputHelper output, WpfAppFixture wpf)
    {
        _output = output;
        _wpf = wpf;
    }

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

    /// <summary>Where the capture lands. Overridable so a run can direct it somewhere specific.</summary>
    private static string OutputDirectory =>
        Environment.GetEnvironmentVariable("HLAFOMREADER_CAPTURE_DIR")
        ?? Path.Combine(Path.GetTempPath(), "hlafomreader-capture");

    [Fact]
    public void CaptureCompareScreen()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-shot-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));

                RunCommand(compare.CompareCommand);

                // Hosted in a real Window rather than measured detached: a TabControl's content
                // region only lays out the way the app shows it once it has a presentation source,
                // so a detached Measure/Arrange renders a screen nobody would ever see.
                var view = new CompareView { DataContext = compare };
                return RenderInWindow(view, 1280, 820, "compare-screen.png");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
        Assert.True(new FileInfo(file).Length > 10_000, "the render looks empty");
    }

    /// <summary>The Classes tab on a cross-standard pair, which is the case it reads worst on.</summary>
    [Fact]
    public void CaptureClassesTab()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-classes-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));

                RunCommand(compare.CompareCommand);

                var view = new CompareView { DataContext = compare };
                return RenderInWindow(view, 1280, 820, "classes-tab.png", selectTabIndex: 1);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// The Classes tab with the options moved off the run that produced the figures below them.
    /// </summary>
    [Fact]
    public void CaptureStaleNotice()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-stale-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));

                RunCommand(compare.CompareCommand);

                // After the run, not before. This is the state the notice exists for: real figures
                // on screen, produced under settings that are no longer the ones selected above them.
                compare.IsFullDepth = true;
                compare.IgnoreInexpressibleProperties = true;
                Assert.True(compare.IsResultStale);

                var view = new CompareView { DataContext = compare };
                return RenderInWindow(view, 1280, 820, "stale-notice.png", selectTabIndex: 1);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CaptureAttributeMap()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-map-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));

                RunTask(compare.AttributeMap.ActivateAsync());
                compare.AttributeMap.OnlyDifferences = false;

                var view = new AttributeMapView { DataContext = compare.AttributeMap };
                return Render(view, 1280, 700, "attribute-map.png");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// The datatype inspector, on a composite: the case the window exists for, where the encoding
    /// column can only show a truncated one-liner and the reader needs the whole layout.
    /// </summary>
    [Fact]
    public void CaptureDataTypeInspector()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-type-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogs());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));

                var map = compare.AttributeMap;
                RunTask(map.ActivateAsync());
                map.OnlyDifferences = false;

                // Prefer a row whose type unfolds — a record beats a scalar for seeing the layout.
                var rows = map.Map!.Rows;
                var row = rows.FirstOrDefault(r => r.LeftEncoding?.Contains("record(", StringComparison.Ordinal) == true)
                          ?? rows.FirstOrDefault(r => r.LeftEncoding?.Contains("array(", StringComparison.Ordinal) == true)
                          ?? rows.First(r => r.LeftEncoding is { Length: > 0 } e && e[0] != '?');

                var captured = new List<DataTypeDetailViewModel>();
                var recording = new RecordingDialogs(captured);
                var inspectable = new AttributeMapViewModel(repository, recording);
                inspectable.SetPair(
                    compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal)),
                    compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal)));
                RunTask(inspectable.ActivateAsync());

                inspectable.ShowLeftDataTypeCommand.Execute(
                    inspectable.Map!.Rows.First(r => r.QualifiedName == row.QualifiedName));

                var window = new DataTypeDetailWindow
                {
                    DataContext = captured.Single(),
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                };

                window.Show();
                try
                {
                    return Render(window, 720, 640, "datatype-inspector.png");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
    }

    /// <summary>Captures what the inspector was asked to show instead of opening a modal.</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        private readonly List<DataTypeDetailViewModel> _captured;

        public RecordingDialogs(List<DataTypeDetailViewModel> captured) => _captured = captured;

        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;
        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => null;
        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public void ShowInfo(string title, string message) { }
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) => _captured.Add(model);
    }

    [Fact]
    public void CaptureDetailScreen()
    {
        var file = _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-detail-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                var sample = Path.Combine(SamplesDirectory, "RestaurantFOM-1516-2010.xml");
                var entry = repository.Register(FomFileReader.ParseFile(sample), "Restaurant Evolved", sample);

                var detail = new FomDetailViewModel(repository, new SilentDialogs(), entry);

                // Land on a class that INHERITS, so the Declared in column has something to show.
                var nodes = detail.Tree.SelectMany(n => n.DescendantsAndSelf()).ToList();
                detail.SelectedNode =
                    nodes.FirstOrDefault(n => n.Members.Any(m => m.IsInherited))
                    ?? nodes.First(n => n.HasMembers);

                var view = new FomDetailView { DataContext = detail };
                return Render(view, 1280, 820, "detail-screen.png");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });

        _output.WriteLine($"capture written to {file}");
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// Builds the registration dialog and renders it. Constructing it at all proves the XAML parses
    /// and every StaticResource key it names resolves; laying it out proves the bindings bind. The
    /// constructor is private because callers are meant to use Prompt(), so the test reaches it
    /// reflectively rather than widening the production API for a test's benefit.
    /// </summary>
    [Fact]
    public void CaptureRegisterDialog()
    {
        var file = _wpf.Invoke(() =>
        {
            var window = (Window)Activator.CreateInstance(typeof(RegisterFomWindow), nonPublic: true)!;

            window.ApplyTemplate();
            if (window.Content is FrameworkElement content)
            {
                content.Measure(new Size(540, 720));
                content.Arrange(new Rect(0, 0, 540, content.DesiredSize.Height));
                content.UpdateLayout();

                var height = (int)Math.Max(320, Math.Ceiling(content.ActualHeight));
                return Render(content, 540, height, "register-dialog.png");
            }

            return null;
        });

        _output.WriteLine(file is null ? "no content to render" : $"capture written to {file}");
        Assert.NotNull(file);
        Assert.True(File.Exists(file!));
    }

    /// <summary>
    /// The registration dialog with several 1516 files chosen, which is the module-order state.
    /// </summary>
    /// <remarks>
    /// The selection is pushed in reflectively because the only other way to reach this state is an
    /// OpenFileDialog, and the private members it goes through are the same ones the other tests on
    /// this window use rather than widening the production API for a test's benefit.
    /// </remarks>
    [Fact]
    public void CaptureRegisterDialogWithModules()
    {
        var file = _wpf.Invoke(() =>
        {
            var window = (Window)Activator.CreateInstance(typeof(RegisterFomWindow), nonPublic: true)!;

            var paths = (List<string>)typeof(RegisterFomWindow)
                .GetField("_evolvedPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;

            paths.AddRange(new[]
            {
                Path.Combine(SamplesDirectory, "RestaurantFOM-1516-2000.xml"),
                Path.Combine(SamplesDirectory, "RestaurantFOM-1516-2010.xml"),
                Path.Combine(SamplesDirectory, "RestaurantFOM-1516-2010-v2.xml"),
            });

            foreach (var name in new[] { "RebuildModuleList", "UpdateState" })
            {
                typeof(RegisterFomWindow)
                    .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, null);
            }

            window.ApplyTemplate();
            if (window.Content is FrameworkElement content)
            {
                content.Measure(new Size(540, 900));
                content.Arrange(new Rect(0, 0, 540, content.DesiredSize.Height));
                content.UpdateLayout();

                var height = (int)Math.Max(320, Math.Ceiling(content.ActualHeight));
                return Render(content, 540, height, "register-dialog-modules.png");
            }

            return null;
        });

        _output.WriteLine(file is null ? "no content to render" : $"capture written to {file}");
        Assert.NotNull(file);
        Assert.True(File.Exists(file!));
    }

    /// <summary>
    /// Renders a screen the way the shell actually hosts it: inside a realised window, off-screen.
    /// </summary>
    /// <remarks>
    /// Some layout only settles once there is a presentation source behind it — a TabControl's
    /// content region is one — so measuring a detached control captures an arrangement the running
    /// app never produces.
    /// </remarks>

    /// <summary>
    /// The whole shell, every screen, in both themes and at both sidebar widths.
    /// </summary>
    /// <remarks>
    /// The light and dark dictionaries expose the same keys, so nothing here can fail to build —
    /// what goes wrong instead is a colour that was only ever checked against one background. This
    /// renders the pair so the other one can be looked at.
    /// </remarks>
    [Fact]
    public void CaptureShellInBothThemes()
    {
        var files = _wpf.Invoke(() =>
        {
            var written = new List<string>();
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-themes-{Guid.NewGuid():N}.db");
            var startingTheme = ThemeManager.Current;

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeManager.Apply(theme, persist: false);

                    var shell = new MainViewModel(repository, new SilentDialogs());
                    shell.Initialize();

                    var compare = shell.Compare;
                    compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                    compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                    RunCommand(compare.CompareCommand);

                    var mode = theme.ToString().ToLowerInvariant();

                    shell.IsSidebarCollapsed = false;
                    shell.Navigate(AppScreen.Registry);
                    written.Add(RenderShell(shell, $"shell-{mode}-registry.png"));

                    shell.Navigate(AppScreen.Compare);
                    written.Add(RenderShell(shell, $"shell-{mode}-compare.png"));

                    shell.Navigate(AppScreen.Settings);
                    written.Add(RenderShell(shell, $"shell-{mode}-settings.png"));

                    shell.IsSidebarCollapsed = true;
                    shell.Navigate(AppScreen.Registry);
                    written.Add(RenderShell(shell, $"shell-{mode}-rail.png"));

                    shell.Dispose();
                }
            }
            finally
            {
                ThemeManager.Apply(startingTheme, persist: false);

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }

            return written;
        });

        foreach (var file in files)
        {
            _output.WriteLine($"capture written to {file}");
            Assert.True(new FileInfo(file).Length > 10_000, $"the render looks empty: {file}");
        }
    }

    /// <summary>Renders the real shell window, chrome and all, rather than a screen on its own.</summary>
    private static string RenderShell(MainViewModel shell, string fileName)
    {
        const int width = 1280;
        const int height = 820;

        var window = new MainWindow
        {
            DataContext = shell,
            Width = width,
            Height = height,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };

        window.Show();
        try
        {
            // The sidebar animates its width, so the layout has to be given time to arrive before
            // anything is measured off it.
            for (var i = 0; i < 12; i++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Thread.Sleep(30);
            }

            var content = (FrameworkElement)window.Content;
            content.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            Directory.CreateDirectory(OutputDirectory);
            var path = Path.Combine(OutputDirectory, fileName);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);

            return path;
        }
        finally
        {
            window.Close();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>
    /// What to paint behind a control that is transparent in places. Read from the live theme
    /// rather than fixed, so a capture taken in the light theme is not sitting on a dark slab.
    /// </summary>
    private static Brush ThemeSurface =>
        Application.Current?.TryFindResource("SurfaceBackground") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x26));

    private static string RenderInWindow(
        FrameworkElement content, int width, int height, string fileName,
        int? selectTabIndex = null, bool scrim = false)
    {
        var window = new Window
        {
            Content = content,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Background = ThemeSurface,
        };

        window.Show();
        try
        {
            content.Width = double.NaN;
            content.Height = double.NaN;

            // Realised first, so the TabControl exists to be driven.
            if (selectTabIndex is { } index)
            {
                content.UpdateLayout();
                var tabs = FindTabControl(content);
                if (tabs is not null) tabs.SelectedIndex = index;

                for (var i = 0; i < 4; i++)
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Thread.Sleep(20);
                }

                content.UpdateLayout();
            }

            // Shows what the shell looks like while a modal is open over it.
            using (scrim ? ModalScrim.Cover(window) : null)
            {
                if (scrim)
                {
                    content.UpdateLayout();
                    for (var i = 0; i < 4; i++)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Thread.Sleep(20);
                    }
                }

                // The adorner layer is a sibling of the content inside the window's template, so a
                // VisualBrush of the content alone would omit the scrim entirely. Render the
                // template root instead, which contains both.
                var root = scrim && VisualTreeHelper.GetChildrenCount(window) > 0
                    ? (FrameworkElement)VisualTreeHelper.GetChild(window, 0)
                    : (FrameworkElement)window.Content;

                return Render(root, width, height, fileName);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static System.Windows.Controls.TabControl? FindTabControl(DependencyObject root)
    {
        if (root is System.Windows.Controls.TabControl found) return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindTabControl(VisualTreeHelper.GetChild(root, i)) is { } hit)
                return hit;

        return null;
    }

    private static string Render(FrameworkElement element, int width, int height, string fileName)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        for (var i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
        }

        element.UpdateLayout();

        // Paint the theme background first; the control itself is transparent in places.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(ThemeSurface, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null,
                new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        Directory.CreateDirectory(OutputDirectory);
        var path = Path.Combine(OutputDirectory, fileName);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);

        return path;
    }

    private static void RunCommand(AsyncRelayCommand command) => RunTask(command.ExecuteAsync());

    /// <summary>
    /// Drives the dispatcher until <paramref name="task"/> finishes.
    /// </summary>
    /// <remarks>
    /// The test body runs inside a blocking <c>Dispatcher.Invoke</c>, so a continuation posted back
    /// to that dispatcher cannot run until something pumps it — awaiting here would deadlock, and
    /// sleeping would only pass for as long as the sleep happens to outlast the work.
    /// </remarks>
    private static void RunTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, "The operation did not finish within 60 seconds.");
        task.GetAwaiter().GetResult();
    }

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
