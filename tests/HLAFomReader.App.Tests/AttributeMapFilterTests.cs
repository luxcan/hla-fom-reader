using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.App.ViewModels;
using HLAFomReader.App.Views;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.App.Tests;

/// <summary>
/// The Attribute data tab: two class pickers, and the filters over the comparison they produce.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are easy to get wrong and invisible when they are. The grid labels a renamed
/// datatype "Same" — a rename encodes identically, so it needs no work — and the single "= Same"
/// chip counts and hides both statuses at once, which makes
/// <see cref="AttributeMapViewModel.ShowSame"/> and <see cref="AttributeMapViewModel.ShowRenamed"/>
/// two halves of one control; any state where they disagree is one the user cannot reach a control
/// for.
/// </para>
/// <para>
/// The second is the pickers. Each is an editable ComboBox over a filtered view, and the rule that
/// makes it work — the current selection is always admitted by the predicate — is invisible in the
/// XAML and would be silently removable without it.
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class AttributeMapFilterTests
{
    private readonly ITestOutputHelper _output;
    private readonly WpfAppFixture _wpf;

    public AttributeMapFilterTests(ITestOutputHelper output, WpfAppFixture wpf)
    {
        _output = output;
        _wpf = wpf;
    }

    // ---- the Same chip ------------------------------------------------------------------------

    /// <summary>
    /// Every route into and out of the chip has to leave both halves agreeing — including the route
    /// nobody takes, which is opening the screen and touching nothing.
    /// </summary>
    [Fact]
    public void TheSameChipMovesRenamedRowsWithIt()
    {
        _wpf.Invoke(() => WithPair(map =>
        {
            // The regression: the screen opened with the chip unticked and its rows displayed.
            Assert.True(map.ShowSame, "the Same chip opened unticked");
            Assert.Equal(map.ShowSame, map.ShowRenamed);

            // The chip is documented as the mirror of the attention checkbox, so the two have to
            // open on the same answer as well.
            Assert.False(map.OnlyDifferences);

            map.ShowSame = false;
            Assert.False(map.ShowRenamed);

            map.ShowSame = true;
            Assert.True(map.ShowRenamed);

            // The attention checkbox writes the pair directly, bypassing the chip's own setter.
            map.OnlyDifferences = true;
            Assert.False(map.ShowSame);
            Assert.False(map.ShowRenamed);

            map.OnlyDifferences = false;
            Assert.True(map.ShowSame);
            Assert.True(map.ShowRenamed);

            // ... and it has to bring a chip the user turned off back up with it, or the checkbox
            // reports "everything" while the renames stay hidden.
            map.ShowSame = false;
            map.OnlyDifferences = true;
            map.OnlyDifferences = false;
            Assert.True(map.ShowSame);
            Assert.True(map.ShowRenamed);
        }));
    }

    /// <summary>
    /// The same invariant read off the grid rather than off the properties: with the chip down, no
    /// row the grid would label "Same" survives the filter.
    /// </summary>
    [Fact]
    public void NoRowLabelledSameSurvivesTheChipBeingTurnedOff()
    {
        _wpf.Invoke(() => WithPair(map =>
        {
            var built = map.Map;
            Assert.NotNull(built);
            _output.WriteLine(
                $"{built!.Rows.Count} rows · same {map.SameCount} · renamed {map.RenamedCount} " +
                $"· changed {map.ChangedCount} · moved {map.MovedCount}");

            // A chip covering no rows would let this pass without testing anything.
            Assert.True(map.SameOrRenamedCount > 0, "the sample pair has no rows the chip covers");
            Assert.Contains(map.Rows, LabelledSame);

            map.ShowSame = false;
            Assert.DoesNotContain(map.Rows, LabelledSame);

            // Re-pairing against another class must not reintroduce them: choosing a new class is a
            // new comparison, not a reset of the filters the user set over it.
            var other = map.ClassOptionsB.FirstOrDefault(
                option => option.QualifiedName != map.SelectedClassB!.QualifiedName
                          && option.AttributeCount > 1);

            if (other is not null)
            {
                map.SelectedClassB = other;
                RunTask(map.PendingWork);
                Assert.DoesNotContain(map.Rows, LabelledSame);
            }

            map.ShowSame = true;
            Assert.Contains(map.Rows, LabelledSame);
        }));
    }

    /// <summary>
    /// The half of the chip the bug actually lived in: a datatype renamed with no change to what it
    /// encodes. The grid labels those rows "Same" too, and the chip has to hide them with the rest.
    /// </summary>
    [Fact]
    public void ARenamedDatatypeHidesWithTheChipThatCountsIt()
    {
        _wpf.Invoke(() => WithPair(
            map =>
            {
                _output.WriteLine($"renamed {map.RenamedCount} · same {map.SameCount}");
                Assert.True(map.RenamedCount > 0, "the rename was not carried into the comparison");

                Assert.Contains(map.Rows, row => row.Status == AttributeMapStatus.Renamed);

                map.ShowSame = false;
                Assert.DoesNotContain(map.Rows, row => row.Status == AttributeMapStatus.Renamed);

                map.ShowSame = true;
                Assert.Contains(map.Rows, row => row.Status == AttributeMapStatus.Renamed);
            },
            RenameOneDatatype,
            want: map => map.RenamedCount > 0));
    }

    // ---- the two pickers ----------------------------------------------------------------------

    /// <summary>
    /// Both pickers list their own FOM's classes, and nothing is compared until a class is chosen.
    /// </summary>
    [Fact]
    public void TheScreenListsBothFomsClassesAndComparesNothingUntilOneIsPicked()
    {
        _wpf.Invoke(() => WithLoadedPair(map =>
        {
            Assert.NotEmpty(map.ClassOptionsA);
            Assert.NotEmpty(map.ClassOptionsB);

            Assert.Null(map.Map);
            Assert.Empty(map.Rows);
            Assert.False(map.HasClassA);
            Assert.False(map.HasClassB);
            Assert.False(map.ComparesBothSides);

            // The empty state has to say what to do rather than blaming a filter.
            Assert.Contains("Pick a class in each FOM", map.EmptyMessage, StringComparison.Ordinal);
        }));
    }

    /// <summary>
    /// The state the Unpaired status exists for. A class chosen on one side alone is listed, not
    /// judged: nothing may be reported as missing from a FOM that was never consulted.
    /// </summary>
    [Fact]
    public void AClassPickedOnOneSideIsListedRatherThanJudged()
    {
        _wpf.Invoke(() => WithLoadedPair(map =>
        {
            var chosen = map.ClassOptionsA.OrderByDescending(o => o.AttributeCount).First();

            map.SelectedClassA = chosen;
            RunTask(map.PendingWork);

            Assert.NotEmpty(map.Rows);
            Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Unpaired, row.Status));

            // Every figure the chips and the headline read stays at zero: none of them has been
            // established. This is the whole point of the status.
            Assert.Equal(0, map.ChangedCount);
            Assert.Equal(0, map.OnlyLeftCount);
            Assert.Equal(0, map.OnlyRightCount);
            Assert.Equal(0, map.SameOrRenamedCount);
            Assert.False(map.ComparesBothSides);

            Assert.Contains("nothing chosen in B", map.Summary, StringComparison.Ordinal);

            // Turning a chip off must not empty a grid whose rows no chip describes ...
            map.ShowOnlyLeft = false;
            Assert.NotEmpty(map.Rows);

            // ... and neither must the attention checkbox, which is a statement about a comparison
            // that has not happened.
            map.OnlyDifferences = true;
            Assert.NotEmpty(map.Rows);
        }));
    }

    /// <summary>
    /// Typing narrows a picker by substring, which is the point of filtering it rather than leaning
    /// on WPF's prefix-only type-to-select.
    /// </summary>
    [Fact]
    public void TypingIntoAPickerNarrowsItBySubstring()
    {
        _wpf.Invoke(() => WithLoadedPair(map =>
        {
            var all = Visible(map.ClassesA).Count;
            Assert.True(all > 2, "the sample FOM has too few classes to filter");

            var target = map.ClassOptionsA.First(o => o.LeafName == "Chef");

            // The MIDDLE of the name. WPF's own type-to-select is prefix-only, so "hef" would never
            // reach Chef with it — which is exactly why the list is filtered instead.
            map.ClassFilterA = "hef";

            var narrowed = Visible(map.ClassesA);
            Assert.True(narrowed.Count < all, "the filter did not narrow the list");
            Assert.Contains(target, narrowed);

            // Typing must never pick anything: a filter that selected its first match would fire a
            // comparison on every keystroke.
            Assert.Null(map.SelectedClassA);

            // The other side is untouched — one view each, never a shared default view.
            Assert.Equal(map.ClassOptionsB.Count, Visible(map.ClassesB).Count);

            map.ClassFilterA = "";
            Assert.Equal(all, Visible(map.ClassesA).Count);
        }));
    }

    /// <summary>
    /// The load-bearing rule of the whole picker: the chosen class is always admitted by the filter.
    /// </summary>
    /// <remarks>
    /// WPF's Selector drops SelectedItem the instant the selected item leaves the items collection,
    /// and the ComboBox then rewrites its editable text box from the now-null selection — deleting
    /// the word the user is halfway through typing. Nothing in the XAML says so.
    /// </remarks>
    [Fact]
    public void TheChosenClassSurvivesAFilterThatExcludesIt()
    {
        _wpf.Invoke(() => WithLoadedPair(map =>
        {
            var chosen = map.ClassOptionsA.OrderByDescending(o => o.AttributeCount).First();
            map.SelectedClassA = chosen;
            RunTask(map.PendingWork);

            // Text that matches nothing at all, let alone the chosen class.
            map.ClassFilterA = "zzz-no-such-class";

            Assert.Contains(chosen, Visible(map.ClassesA));
            Assert.Same(chosen, map.SelectedClassA);
        }));
    }

    /// <summary>
    /// The filter and the selection are independent: narrowing the list never picks anything, and
    /// picking never narrows.
    /// </summary>
    /// <remarks>
    /// A filter that moved the selection would fire a class comparison on every keystroke, and a
    /// selection that set the filter would leave the list stuck on one class. The view pushes a
    /// filter only from real keystrokes, and drops it when the list closes; see AttributeMapView.xaml.cs.
    /// </remarks>
    [Fact]
    public void TheFilterAndTheSelectionDoNotMoveEachOther()
    {
        _wpf.Invoke(() => WithLoadedPair(map =>
        {
            map.ClassFilterA = "hef";
            Assert.Null(map.SelectedClassA);
            Assert.Single(Visible(map.ClassesA));

            var chosen = map.ClassOptionsA.First(o => o.LeafName == "Chef");
            map.SelectedClassA = chosen;
            RunTask(map.PendingWork);

            // Picking left the filter exactly as the user typed it.
            Assert.Equal("hef", map.ClassFilterA);

            map.ClassFilterA = "";
            Assert.Same(chosen, map.SelectedClassA);
            Assert.Equal(map.ClassOptionsA.Count, Visible(map.ClassesA).Count);
        }));
    }

    /// <summary>Unpicking a class empties the comparison rather than leaving a stale one on screen.</summary>
    [Fact]
    public void UnpickingBothClassesClearsTheComparison()
    {
        _wpf.Invoke(() => WithPair(map =>
        {
            Assert.NotEmpty(map.Rows);

            Assert.True(map.ClearClassACommand.CanExecute(null));
            map.ClearClassACommand.Execute(null);
            RunTask(map.PendingWork);

            // One side left: listed, not judged.
            Assert.All(map.Rows, row => Assert.Equal(AttributeMapStatus.Unpaired, row.Status));

            map.ClearClassBCommand.Execute(null);
            RunTask(map.PendingWork);

            Assert.Empty(map.Rows);
            Assert.Null(map.Map);
            Assert.False(map.ClearClassACommand.CanExecute(null));
            Assert.False(map.ClearClassBCommand.CanExecute(null));
        }));
    }

    /// <summary>
    /// The export writes what is on screen, unfolded through each datatype — so the file is at least
    /// as long as the grid, and always carries the header.
    /// </summary>
    [Fact]
    public void TheExportWritesTheVisibleRowsUnfolded()
    {
        _wpf.Invoke(() => WithPair(map =>
        {
            var built = map.Map;
            Assert.NotNull(built);

            var sheet = AttributePairExporter.Build(
                built!, map.Rows.ToList(), Document(built), Document(built));

            Assert.True(sheet.Rows.Count >= map.Rows.Count);
            Assert.Equal(map.Rows.Count, sheet.Rows.Count(r => r.Depth == 1));

            // Reachable from the screen exactly when there is something to write.
            Assert.True(map.ExportCommand.CanExecute(null));
        }));

        // The exporter needs the two documents; the view model holds them privately, so this
        // reads them back the same way any other consumer would.
        static FomDocument Document(AttributeDataMap _) =>
            FomFileReader.ParseFile(
                Directory.GetFiles(SamplesDirectory, "*1516-2010.xml").First());
    }

    // ---- harness ------------------------------------------------------------------------------

    /// <summary>The two statuses the grid draws as "Same". See AttributeMapView.xaml.</summary>
    private static bool LabelledSame(AttributeMapRow row) =>
        row.Status is AttributeMapStatus.Same or AttributeMapStatus.Renamed;

    /// <summary>What a picker's filtered view actually shows right now.</summary>
    private static List<ObjectClassOption> Visible(System.ComponentModel.ICollectionView view) =>
        view.Cast<ObjectClassOption>().ToList();

    /// <summary>
    /// Builds the tab over a throwaway database holding the two 1516-2010 samples, reads both FOMs,
    /// and hands it to <paramref name="body"/> with the pickers filled and nothing chosen.
    /// </summary>
    private static void WithLoadedPair(
        Action<AttributeMapViewModel> body, Action<FomDocument>? mutateRight = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-filter-{Guid.NewGuid():N}.db");

        try
        {
            using var repository = new SqliteFomRepository(databasePath);

            foreach (var file in Directory.GetFiles(SamplesDirectory, "*1516-2010*.xml"))
            {
                var parsed = FomFileReader.ParseFile(file);

                if (mutateRight is not null && file.Contains("v2", StringComparison.Ordinal))
                    mutateRight(parsed);

                repository.Register(parsed, Path.GetFileNameWithoutExtension(file), file);
            }

            var entries = repository.ListEntries().ToList();
            Assert.Equal(2, entries.Count);

            var map = new AttributeMapViewModel(repository, new ThrowingDialogs());
            map.SetPair(
                entries.First(entry => !entry.FileName.Contains("v2", StringComparison.Ordinal)),
                entries.First(entry => entry.FileName.Contains("v2", StringComparison.Ordinal)));

            RunTask(map.ActivateAsync(showBusy: false));

            body(map);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }

    /// <summary>
    /// The same, with a class chosen on each side so there is a comparison to filter.
    /// </summary>
    /// <param name="want">
    /// What the comparison has to contain for the test to mean anything. Shared classes are tried
    /// richest first until one satisfies it, so a test asking about renames is never handed a class
    /// pair that has none and passes vacuously.
    /// </param>
    private static void WithPair(
        Action<AttributeMapViewModel> body,
        Action<FomDocument>? mutateRight = null,
        Func<AttributeMapViewModel, bool>? want = null)
    {
        WithLoadedPair(
            map =>
            {
                var shared = map.ClassOptionsA
                    .Where(a => map.ClassOptionsB.Any(
                        b => string.Equals(b.QualifiedName, a.QualifiedName, StringComparison.Ordinal)))
                    .OrderByDescending(a => a.AttributeCount)
                    .ToList();

                Assert.NotEmpty(shared);

                foreach (var option in shared)
                {
                    map.SelectedClassA = option;
                    map.SelectedClassB = map.ClassOptionsB.First(
                        b => string.Equals(b.QualifiedName, option.QualifiedName, StringComparison.Ordinal));

                    RunTask(map.PendingWork);

                    if (want is null || want(map))
                    {
                        body(map);
                        return;
                    }
                }

                Assert.Fail("no shared class produced the comparison this test needs");
            },
            mutateRight);
    }

    /// <summary>
    /// Copies one simple datatype in FOM B under a new name and repoints every attribute using it,
    /// which is exactly what a version step does: a different name over identical bits.
    /// </summary>
    private static void RenameOneDatatype(FomDocument document)
    {
        // Only a datatype an object-class attribute is actually typed as produces a row here. Most
        // of the simple table is reached through records and arrays instead, and renaming one of
        // those changes an encoding rather than a name.
        var attributeTypes = document.AllObjectClasses()
            .SelectMany(objectClass => objectClass.Attributes)
            .Select(attribute => attribute.DataType)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var original = document.DataTypes.SimpleDataTypes
            .FirstOrDefault(type => attributeTypes.Contains(type.Name));

        Assert.True(original is not null, "no object-class attribute in FOM B is typed as a simple datatype");

        var alias = new SimpleDataType
        {
            Name = original!.Name + "Renamed",
            QualifiedName = original.QualifiedName + "Renamed",
            Representation = original.Representation,
            Units = original.Units,
            Resolution = original.Resolution,
            Accuracy = original.Accuracy,
        };

        document.DataTypes.SimpleDataTypes.Add(alias);

        foreach (var objectClass in document.AllObjectClasses())
            foreach (var attribute in objectClass.Attributes)
                if (string.Equals(attribute.DataType, original.Name, StringComparison.Ordinal))
                    attribute.DataType = alias.Name;
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

    /// <summary>
    /// Pumps the dispatcher until the work finishes. The body runs inside a blocking
    /// <c>Dispatcher.Invoke</c>, so a continuation cannot run unless something drives it.
    /// </summary>
    private static void RunTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, "The comparison did not finish within 60 seconds.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Any dialog here is a failure: this test never asks a question.</summary>
    private sealed class ThrowingDialogs : IDialogService
    {
        public string[]? OpenFiles(string title, string filter, bool multiSelect = true) => null;
        public IReadOnlyList<FomRegistrationRequest>? RequestRegistrations() => null;
        public string? SaveFile(string title, string filter, string defaultFileName, string defaultExt) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => throw new InvalidOperationException($"{title}: {message}");
        public void ShowInfo(string title, string message) { }
        public void ShowDataTypeDetail(DataTypeDetailViewModel model) { }
        public ClassExportSelection? RequestExportSelection(ExportSelectionViewModel model) => null;
    }
}
