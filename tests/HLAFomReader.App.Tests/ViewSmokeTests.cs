using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.Core.Reporting;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Builds both screens for real on an STA thread with the app's resource dictionaries loaded, then
/// runs a full layout pass. This catches what XAML compilation cannot: missing StaticResource keys,
/// broken ControlTemplate parts, and binding paths that do not exist on the view model.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ViewSmokeTests
{
    private readonly ITestOutputHelper _output;
    private readonly WpfAppFixture _wpf;

    public ViewSmokeTests(ITestOutputHelper output, WpfAppFixture wpf)
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

    [Fact]
    public void BothScreensLayOutWithNoResourceOrBindingFailures()
    {
        var failures = _wpf.Invoke(() =>
        {
            var problems = new List<string>();

            // Capture WPF's own binding diagnostics while the views are built.
            var listener = new CollectingTraceListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-ui-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var file in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".fed", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var parsed = FomFileReader.ParseFile(file);
                    repository.Register(parsed, Path.GetFileNameWithoutExtension(file), file);
                }

                var dialogs = new SilentDialogService();
                var shell = new MainViewModel(repository, dialogs);
                shell.Initialize();

                // --- Registry screen -------------------------------------------------------
                shell.Navigate(AppScreen.Registry);
                var registryView = new RegistryView { DataContext = shell.Registry };
                Layout(registryView);

                Assert.Equal(4, shell.Registry.Entries.Count);
                Assert.NotNull(shell.Registry.SelectedEntry);
                Assert.NotEmpty(shell.Registry.Structure);

                // Selecting each entry rebuilds the structure tree and detail pane.
                foreach (var entry in shell.Registry.Entries.ToList())
                {
                    shell.Registry.SelectedEntry = entry;
                    Layout(registryView);
                    Assert.NotNull(shell.Registry.SelectedDocument);
                }

                // --- Compare screen --------------------------------------------------------
                shell.Navigate(AppScreen.Compare);
                var compareView = new CompareView { DataContext = shell.Compare };
                Layout(compareView);

                var compare = shell.Compare;
                Assert.True(compare.HasEnoughSources);

                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));

                // Ask for the exhaustive depth so this keeps pinning the documented 18, and so the
                // depth radio buttons get exercised through the view model.
                compare.IsFullDepth = true;
                Assert.Equal(ComparisonDepth.Full, compare.Depth);

                RunCommand(compare.CompareCommand);

                Assert.True(compare.HasCompared);
                Assert.NotNull(compare.Result);

                // The comparison still covers every OMT table, and still finds the documented 18.
                Assert.Equal(18, compare.Result!.TotalDifferences);

                // The Classes tab reports only what it draws — one flat row per object or
                // interaction class — so its total counts classes needing attention, a different
                // unit from the comparison's property-level total and a smaller number.
                Assert.NotEmpty(compare.ClassRows);
                Assert.True(compare.TotalDifferences > 0, "the class list reported nothing");
                Assert.True(compare.TotalDifferences < compare.Result.TotalDifferences);

                // Every class appears exactly once, however deep it sits. This is what the tree
                // could not do: a class five levels down was hidden behind a chevron.
                var qualified = compare.ClassRows.Select(r => r.QualifiedName).ToList();
                Assert.Equal(qualified.Count, qualified.Distinct(StringComparer.Ordinal).Count());

                // Anything asking for work has to say what the work is, or the row is a coloured
                // square the reader cannot act on — the whole reason this replaced the tree.
                Assert.All(compare.ClassRows.Where(r => r.NeedsAttention), r =>
                    Assert.False(string.IsNullOrWhiteSpace(r.Why), $"{r.QualifiedName} is flagged with no reason"));

                // The chips filter the list, so each has to count the rows of its own kind the list
                // actually holds — otherwise ticking one shows a different number than it named.
                compare.ShowChanged = true;
                compare.ShowOnlyLeft = true;
                compare.ShowOnlyRight = true;
                compare.ShowSame = true;
                Layout(compareView);

                Assert.Equal(compare.ClassRows.Count(r => r.Status == ClassMapStatus.Changed), compare.ChangedCount);
                Assert.Equal(compare.ClassRows.Count(r => r.Status == ClassMapStatus.OnlyInLeft), compare.OnlyLeftCount);
                Assert.Equal(compare.ClassRows.Count(r => r.Status == ClassMapStatus.OnlyInRight), compare.OnlyRightCount);
                Assert.Equal(
                    compare.ClassRows.Count(r => r.Status is ClassMapStatus.Same or ClassMapStatus.Renamed),
                    compare.SameCount);

                // Selecting a row and rendering it must not throw.
                compare.SelectedClass = compare.ClassRows.First(r => r.NeedsAttention);
                Layout(compareView);
                Assert.NotNull(compare.SelectedClass);

                // Every chip off empties the list rather than throwing.
                compare.ShowChanged = false;
                compare.ShowOnlyLeft = false;
                compare.ShowOnlyRight = false;
                compare.ShowSame = false;
                Layout(compareView);
                Assert.Empty(compare.ClassRows);

                compare.ShowChanged = true;
                compare.ShowOnlyLeft = true;
                compare.ShowOnlyRight = true;
                Layout(compareView);

                compare.SearchText = "Manager";
                Layout(compareView);
                Assert.All(compare.ClassRows, r =>
                    Assert.True(
                        (r.LeftName?.Contains("Manager", StringComparison.OrdinalIgnoreCase) ?? false)
                        || (r.RightName?.Contains("Manager", StringComparison.OrdinalIgnoreCase) ?? false)
                        || r.Name.Contains("Manager", StringComparison.OrdinalIgnoreCase)
                        || r.Why.Contains("Manager", StringComparison.OrdinalIgnoreCase)));

                compare.SearchText = "";
                Layout(compareView);


                // --- Cross-standard pair ---------------------------------------------------
                compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                RunCommand(compare.CompareCommand);
                Layout(compareView);

                Assert.True(compare.Result!.IsCrossStandard);
                Assert.NotEmpty(compare.Advisories);

                // --- Stored rows tab -------------------------------------------------------
                // The TabControl only realises the selected tab, so build the control directly.
                var storedRows = compare.StoredRows;
                var storedRowsView = new StoredRowsView { DataContext = storedRows };
                Layout(storedRowsView);

                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));
                Layout(storedRowsView);

                Assert.NotEmpty(storedRows.Tables);

                // Every table in the catalogue must open without throwing.
                foreach (var table in storedRows.Tables.ToList())
                {
                    storedRows.SelectedTable = table;
                    RunTask(storedRows.PendingWork);
                    Layout(storedRowsView);
                }

                // Land on a table that really changed and inspect a row.
                var attributes = storedRows.Tables.First(t =>
                    t.Table.Name.Equals("ObjectAttributes", StringComparison.Ordinal));

                storedRows.SelectedTable = attributes;
                RunTask(storedRows.PendingWork);
                storedRows.OnlyDifferences = true;
                Layout(storedRowsView);

                Assert.NotNull(storedRows.Comparison);
                Assert.NotEmpty(storedRows.Rows);
                Assert.All(storedRows.Rows, r => Assert.True(r.IsDifferent));

                storedRows.SelectedRow = storedRows.Rows.First();
                Layout(storedRowsView);
                Assert.NotEmpty(storedRows.SelectedCells);

                // Filters and the case toggle must not throw or blank the view unexpectedly.
                storedRows.OnlyDifferences = false;
                Layout(storedRowsView);
                Assert.True(storedRows.Rows.Count >= 1);

                storedRows.SearchText = "PartySize";
                Layout(storedRowsView);
                Assert.All(storedRows.Rows, r =>
                    Assert.True(r.Key.Contains("PartySize", StringComparison.OrdinalIgnoreCase)
                                || r.Cells.Any(c =>
                                    (c.Left?.Contains("PartySize", StringComparison.OrdinalIgnoreCase) ?? false)
                                    || (c.Right?.Contains("PartySize", StringComparison.OrdinalIgnoreCase) ?? false))));

                storedRows.SearchText = "";
                storedRows.IgnoreCase = true;
                RunTask(storedRows.PendingWork);
                Layout(storedRowsView);
                storedRows.IgnoreCase = false;
                RunTask(storedRows.PendingWork);
                Layout(storedRowsView);

                // --- Attribute data tab ----------------------------------------------------
                var attributeMap = compare.AttributeMap;
                var attributeMapView = new AttributeMapView { DataContext = attributeMap };

                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));

                RunTask(attributeMap.ActivateAsync());
                Layout(attributeMapView);

                // Activating fills the two class pickers and compares nothing: the tab is
                // class-against-class now, and which two is the user's call.
                Assert.NotEmpty(attributeMap.ClassOptionsA);
                Assert.NotEmpty(attributeMap.ClassOptionsB);
                Assert.Null(attributeMap.Map);

                // Customer, because the search below is about one of its attributes.
                AttributeMapHarness.PickSharedClass(attributeMap, "Customer");
                Layout(attributeMapView);

                Assert.NotNull(attributeMap.Map);
                Assert.NotEmpty(attributeMap.Rows);
                Assert.True(attributeMap.ComparesBothSides);

                // Inherited attributes must be mapped against the subclass, not just the declarer.
                Assert.Contains(attributeMap.Map!.Rows, r => !string.IsNullOrWhiteSpace(r.LeftDeclaredIn));

                attributeMap.SelectedRow = attributeMap.Rows.First();
                Layout(attributeMapView);

                attributeMap.OnlyDifferences = false;
                Layout(attributeMapView);
                attributeMap.OnlyDifferences = true;
                Layout(attributeMapView);

                attributeMap.SearchText = "PartySize";
                Layout(attributeMapView);
                Assert.All(attributeMap.Rows, r =>
                    Assert.True(r.ClassName.Contains("PartySize", StringComparison.OrdinalIgnoreCase)
                             || r.AttributeName.Contains("PartySize", StringComparison.OrdinalIgnoreCase)
                             || (r.LeftDataType?.Contains("PartySize", StringComparison.OrdinalIgnoreCase) ?? false)
                             || (r.RightDataType?.Contains("PartySize", StringComparison.OrdinalIgnoreCase) ?? false)));

                attributeMap.SearchText = "";
                Layout(attributeMapView);

                // --- Datatype inspector (clicking an encoding cell) ------------------------
                // The encoding column says whether two attributes move the same bytes. The
                // inspector answers the question after it — what can this field hold — which the
                // canonical form cannot, since everything that would say so is what it drops.
                var typedRow = attributeMap.Rows.First(r => !string.IsNullOrWhiteSpace(r.LeftDataType));

                Assert.True(attributeMap.ShowLeftDataTypeCommand.CanExecute(typedRow));
                attributeMap.ShowLeftDataTypeCommand.Execute(typedRow);

                var inspected = Assert.Single(dialogs.Inspected);
                Assert.Equal("FOM A", inspected.SideLabel);
                Assert.Equal(typedRow.LeftDataType, inspected.Detail.Name);

                // The inspector must never contradict the column that opened it.
                Assert.Equal(typedRow.LeftEncoding, inspected.Canonical);

                // And the window it opens must lay out, so a mistyped resource key in that XAML
                // fails here rather than the first time somebody clicks a cell. Shown off-screen and
                // closed immediately: a Window cannot be measured until it has been realised.
                var inspectorWindow = new DataTypeDetailWindow
                {
                    DataContext = inspected,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                };

                inspectorWindow.Show();
                Layout(inspectorWindow);
                inspectorWindow.Close();
                DrainDispatcher();

                // Every row's every side must open without throwing, whatever it is typed as —
                // a struct, an enumeration, a name in no table at all.
                foreach (var row in attributeMap.Map!.Rows.Take(200))
                {
                    if (attributeMap.ShowLeftDataTypeCommand.CanExecute(row))
                        attributeMap.ShowLeftDataTypeCommand.Execute(row);

                    if (attributeMap.ShowRightDataTypeCommand.CanExecute(row))
                        attributeMap.ShowRightDataTypeCommand.Execute(row);
                }

                // A composite has to unfold, or the window is a wall of one line.
                Assert.Contains(dialogs.Inspected, m => m.HasMembers);

                // A scalar has to carry the bounds its width implies.
                Assert.Contains(dialogs.Inspected, m => m.HasRange);

                dialogs.Inspected.Clear();

                // --- Detail screen (double-click drill-down) -------------------------------
                shell.Navigate(AppScreen.Registry);
                shell.Registry.SelectedEntry = shell.Registry.Entries
                    .First(e => e.FileName.Contains("2010.xml", StringComparison.Ordinal));

                shell.Registry.OpenDetailCommand.Execute(null);

                var detail = Assert.IsType<FomDetailViewModel>(shell.CurrentView);
                var detailView = new FomDetailView { DataContext = detail };
                Layout(detailView);

                Assert.NotEmpty(detail.Tree);

                // Walk every node: each selection rebuilds the member table and the property list.
                foreach (var node in detail.Tree.SelectMany(n => n.DescendantsAndSelf()).Take(60))
                {
                    detail.SelectedNode = node;
                    Layout(detailView);
                }

                // --- Datatype inspector, Registry side -------------------------------------
                // Every datatype the FOM declares has to be openable from the detail screen too,
                // both from the datatype tree and from a class member's DataType column.
                dialogs.Inspected.Clear();

                var datatypeNodes = detail.Tree
                    .SelectMany(n => n.DescendantsAndSelf())
                    .Where(n => n.IsDataType)
                    .ToList();

                Assert.NotEmpty(datatypeNodes);

                foreach (var node in datatypeNodes.Take(40))
                {
                    Assert.True(detail.ShowDataTypeCommand.CanExecute(node.Name), $"{node.Name} is not openable");
                    detail.ShowDataTypeCommand.Execute(node.Name);
                }

                Assert.Equal(Math.Min(40, datatypeNodes.Count), dialogs.Inspected.Count);

                // What it opened must be the type that was clicked, resolved rather than guessed.
                Assert.All(dialogs.Inspected, m => Assert.True(m.Detail.IsResolved, $"{m.Detail.Name} did not resolve"));

                // A group heading names no datatype, so it must offer no click.
                var heading = detail.Tree
                    .SelectMany(n => n.DescendantsAndSelf())
                    .First(n => !n.IsDataType);
                Assert.False(heading.IsDataType);

                dialogs.Inspected.Clear();

                // A class with attributes must actually produce member rows.
                var withMembers = detail.Tree
                    .SelectMany(n => n.DescendantsAndSelf())
                    .First(n => n.HasMembers);

                detail.SelectedNode = withMembers;
                Layout(detailView);
                Assert.NotEmpty(detail.Members);

                // HLA classes inherit every ancestor attribute, so a subclass must show the whole
                // effective set — RPR's Aircraft declares none of its 45 — with each row saying
                // which class declares it.
                var inheriting = detail.Tree
                    .SelectMany(n => n.DescendantsAndSelf())
                    .First(n => n.Members.Any(m => m.IsInherited));

                detail.SelectedNode = inheriting;
                Layout(detailView);

                Assert.Contains(detail.Members, m => m.IsInherited);
                Assert.All(detail.Members, m => Assert.False(string.IsNullOrWhiteSpace(m.DeclaredIn)));
                Assert.Contains("inherited", detail.MemberSummary, StringComparison.Ordinal);

                // An attribute redeclared on a subclass must not appear twice.
                Assert.Equal(detail.Members.Count, detail.Members.Select(m => m.Name).Distinct().Count());

                detail.SearchText = "Customer";
                Layout(detailView);
                detail.SearchText = "";
                Layout(detailView);

                detail.ExpandAllCommand.Execute(null);
                Layout(detailView);
                detail.CollapseAllCommand.Execute(null);
                Layout(detailView);

                // Back returns to the registry list.
                detail.CloseCommand.Execute(null);
                Assert.Same(shell.Registry, shell.CurrentView);
            }
            finally
            {
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

    /// <summary>Applies templates and runs measure/arrange, which is what actually resolves resources.</summary>
    /// <summary>
    /// One press of Compare fills all three tabs, and leaves the strip on the attribute map.
    /// </summary>
    /// <remarks>
    /// The three tabs used to load on their own schedules: the attribute map built itself as soon as
    /// the screen appeared, the class list waited for the Compare button, and the stored rows waited
    /// to be clicked. With an overlay over the top that read as one operation finishing while two
    /// thirds of the screen was still empty — the user pressed Compare, watched a progress bar, then
    /// found nothing under Classes and had no way to tell whether that meant "not loaded" or "no
    /// differences". Compare now loads all three before its overlay lifts, so an empty tab afterwards
    /// means genuinely empty.
    ///
    /// The last two assertions are the other half of it. Stored rows has to be filled without being
    /// selected: its IsActive is TwoWay-bound to the TabItem, so loading it by raising that flag
    /// would drag the tab strip off the attribute map every time a comparison finished.
    /// </remarks>
    [Fact]
    public void CompareFillsAllThreeTabsBeforeItsOverlayLifts()
    {
        _wpf.Invoke(() =>
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-tabs-{Guid.NewGuid():N}.db");

            try
            {
                using var repository = new SqliteFomRepository(databasePath);

                foreach (var sample in Directory.GetFiles(SamplesDirectory)
                             .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    repository.Register(FomFileReader.ParseFile(sample),
                        Path.GetFileNameWithoutExtension(sample), sample);
                }

                var shell = new MainViewModel(repository, new SilentDialogService());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                var view = new CompareView { DataContext = compare };
                Layout(view);

                compare.Left = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("v2", StringComparison.Ordinal));
                Layout(view);

                // Arriving and picking a pair loads nothing. Anything built here would be built again
                // by the Compare below, and a half-filled screen is what this whole test is against.
                Assert.Empty(compare.ClassRows);
                Assert.Empty(compare.AttributeMap.Rows);
                Assert.True(compare.AttributeMap.IsAwaitingCompare);
                Assert.True(compare.StoredRows.IsAwaitingCompare);

                // Each tab says which button fills it rather than leaving the reader to guess.
                Assert.Contains("Press Compare", compare.EmptyTreeMessage, StringComparison.Ordinal);
                Assert.Contains("Press Compare", compare.AttributeMap.EmptyMessage, StringComparison.Ordinal);
                Assert.Contains("Press Compare", compare.StoredRows.EmptyMessage, StringComparison.Ordinal);

                RunCommand(compare.CompareCommand);
                Layout(view);

                // All three, from the one press.
                Assert.NotEmpty(compare.ClassRows);
                Assert.NotNull(compare.StoredRows.Comparison);
                Assert.Contains(compare.StoredRows.Tables, t => t.HasRows);

                // The attribute map is ready rather than filled: Compare reads both FOMs and lists
                // their classes, and stops there. Picking the class pair is the judgement that tab
                // exists for, and choosing one here would present a guess as an answer.
                Assert.NotEmpty(compare.AttributeMap.ClassOptionsA);
                Assert.NotEmpty(compare.AttributeMap.ClassOptionsB);
                Assert.Empty(compare.AttributeMap.Rows);
                Assert.Contains("Pick a class in each FOM", compare.AttributeMap.EmptyMessage,
                    StringComparison.Ordinal);

                // ... and one pick is all it then takes, with both documents already in hand.
                AttributeMapHarness.PickSharedClass(compare.AttributeMap);
                Assert.NotEmpty(compare.AttributeMap.Rows);

                Assert.False(compare.AttributeMap.IsAwaitingCompare);
                Assert.False(compare.StoredRows.IsAwaitingCompare);

                // Landed on the attribute map, and stored rows did not steal the tab strip.
                Assert.True(compare.AttributeMap.IsActive);
                Assert.False(compare.StoredRows.IsActive);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });
    }

    /// <summary>
    /// Changing an option stops the figures on screen claiming to answer for the current settings.
    /// </summary>
    /// <remarks>
    /// The counts, the headline and the class list are all built when Compare is pressed and then
    /// left alone. Moving the depth or the format-gap switch afterwards changed what the next run
    /// would report and nothing about what was already on screen, so the screen went on stating a
    /// figure for settings that were no longer selected — with nothing to tell the reader, because
    /// the number had been arrived at honestly and still looked it.
    ///
    /// Kept rather than cleared. A changed picker throws the result away, because it then answers
    /// about the wrong two FOMs; a changed option leaves it answering about the right pair under
    /// superseded rules, which is worth reading while the next run is decided on. That is also why
    /// this is a value comparison and not a flag — the middle section changes the depth back and
    /// expects the result to count as current again rather than needing a re-run.
    ///
    /// Deliberately run on the cross-standard pair at Full depth. Format gaps are a property-level
    /// phenomenon and the two 1516 samples produce none at all, so on any other pair the assertions
    /// about the gap note would pass against an empty string and prove nothing.
    /// </remarks>
    [Fact]
    public void ChangingAnOptionMarksTheFiguresAsFromThePreviousRun()
    {
        _wpf.Invoke(() =>
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

                var shell = new MainViewModel(repository, new SilentDialogService());
                shell.Initialize();
                shell.Navigate(AppScreen.Compare);

                var compare = shell.Compare;
                var view = new CompareView { DataContext = compare };
                Layout(view);

                var notice = (Border)view.FindName("StaleNotice");
                Assert.NotNull(notice);

                compare.Left = compare.Sources.First(s => s.FileName.EndsWith(".fed", StringComparison.OrdinalIgnoreCase));
                compare.Right = compare.Sources.First(s => s.FileName.Contains("2010.xml", StringComparison.Ordinal));
                Layout(view);

                // Nothing has been run, so there is nothing to be stale about.
                Assert.False(compare.IsResultStale);
                Assert.Equal("", compare.StaleNote);

                compare.IsFullDepth = true;
                RunCommand(compare.CompareCommand);
                Layout(view);

                Assert.False(compare.IsResultStale);
                Assert.Equal(Visibility.Collapsed, notice.Visibility);

                // This pair really does produce format gaps, which is what makes the third section
                // below a test of anything.
                Assert.True(compare.HasFormatGapNote, "the sample pair produced no format gaps");
                var gapNote = compare.FormatGapNote;
                Assert.Contains("are format gaps", gapNote, StringComparison.Ordinal);

                var counted = compare.TotalDifferences;
                var headline = compare.ResultHeadline;
                var rows = compare.ClassRows.Count;

                // --- the depth moves ------------------------------------------------------
                compare.IsStructureDepth = true;
                Layout(view);

                Assert.True(compare.IsResultStale);
                Assert.Equal(Visibility.Visible, notice.Visibility);
                Assert.Contains("previous run", compare.StaleNote, StringComparison.Ordinal);
                Assert.Contains("depth", compare.StaleNote, StringComparison.Ordinal);

                // Kept, not cleared: still the figures the last run produced.
                Assert.Equal(counted, compare.TotalDifferences);
                Assert.Equal(headline, compare.ResultHeadline);
                Assert.Equal(rows, compare.ClassRows.Count);

                // --- and moves back -------------------------------------------------------
                compare.IsFullDepth = true;
                Layout(view);

                Assert.False(compare.IsResultStale);
                Assert.Equal(Visibility.Collapsed, notice.Visibility);

                // --- the format-gap switch moves ------------------------------------------
                //
                // This one used to do worse than go quiet. The note beside the headline read the
                // live switch rather than the run that produced the figures, so ticking the box
                // announced that the format gaps had been hidden while the tree below was still
                // counting every one of them.
                compare.IgnoreInexpressibleProperties = true;
                Layout(view);

                Assert.True(compare.IsResultStale);
                Assert.Contains("format-gap", compare.StaleNote, StringComparison.Ordinal);
                Assert.Equal(gapNote, compare.FormatGapNote);

                // --- running again settles it, and only then does the note change ----------
                RunCommand(compare.CompareCommand);
                Layout(view);

                Assert.False(compare.IsResultStale);
                Assert.Equal(Visibility.Collapsed, notice.Visibility);
                Assert.Contains("hidden", compare.FormatGapNote, StringComparison.Ordinal);

                // --- a changed picker clears rather than staling ---------------------------
                var leftId = compare.Left!.Id;
                var rightId = compare.Right!.Id;
                compare.Right = compare.Sources.First(s => s.Id != leftId && s.Id != rightId);
                Layout(view);

                Assert.Null(compare.Result);
                Assert.False(compare.IsResultStale);
                Assert.Equal(Visibility.Collapsed, notice.Visibility);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }
        });
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();
        DrainDispatcher();
    }

    /// <summary>
    /// Runs an async command to completion, pumping the dispatcher so its continuation — which is
    /// posted back to the UI thread — actually gets to run.
    /// </summary>
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
    /// <summary>Lets queued dispatcher work (async commands, CollectionView refreshes) finish.</summary>
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

            // Path errors and missing sources are real defects. Everything else WPF logs at
            // warning level here is noise (e.g. "resolved using implicit DataContext").
            if (full.Contains("path error", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot find source", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot find governing", StringComparison.OrdinalIgnoreCase)
                || full.Contains("Cannot convert", StringComparison.OrdinalIgnoreCase))
            {
                _messages.Add(full);
            }
        }
    }

    /// <summary>Dialog service that never shows UI, so the smoke test cannot block.</summary>
    private sealed class SilentDialogService : IDialogService
    {
        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;
        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => null;
        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public void ShowInfo(string title, string message) { }

        /// <summary>Recorded rather than shown: a modal here would block the WPF host thread.</summary>
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) => Inspected.Add(model);

        /// <summary>Cancelled rather than answered, so no test can start an export unattended.</summary>
        public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model) => null;

        public List<DataTypeDetailViewModel> Inspected { get; } = new();
    }
}
