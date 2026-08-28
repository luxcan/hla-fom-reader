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
/// Pins the one filter on the Attribute data tab whose control covers more than one row status.
/// </summary>
/// <remarks>
/// The grid labels a renamed datatype "Same" — a rename encodes identically, so it needs no work —
/// and the single "= Same" chip counts and hides both statuses at once. That makes
/// <see cref="AttributeMapViewModel.ShowSame"/> and <see cref="AttributeMapViewModel.ShowRenamed"/>
/// two halves of one control, and any state where they disagree is one the user cannot reach a
/// control for: the chip reads unticked while the rows it names are still on screen.
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

    /// <summary>
    /// Every route into and out of the chip has to leave both halves agreeing — including the route
    /// nobody takes, which is opening the screen and touching nothing.
    /// </summary>
    [Fact]
    public void TheSameChipMovesRenamedRowsWithIt()
    {
        _wpf.Invoke(() => WithMap(map =>
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
        _wpf.Invoke(() => WithMap(map =>
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

            // Scoping to one class must not reintroduce them: the scope narrows, it does not reset.
            var scoped = map.ObjectClasses.FirstOrDefault(option => !option.IsAll && option.RowCount > 1);
            if (scoped is not null)
            {
                map.SelectedObjectClass = scoped;
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
        _wpf.Invoke(() => WithMap(
            map =>
            {
                _output.WriteLine($"renamed {map.RenamedCount} · same {map.SameCount}");
                Assert.True(map.RenamedCount > 0, "the rename was not carried into the map");

                Assert.Contains(map.Rows, row => row.Status == AttributeMapStatus.Renamed);

                map.ShowSame = false;
                Assert.DoesNotContain(map.Rows, row => row.Status == AttributeMapStatus.Renamed);

                map.ShowSame = true;
                Assert.Contains(map.Rows, row => row.Status == AttributeMapStatus.Renamed);
            },
            RenameOneDatatype));
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

    /// <summary>The two statuses the grid draws as "Same". See AttributeMapView.xaml.</summary>
    private static bool LabelledSame(AttributeMapRow row) =>
        row.Status is AttributeMapStatus.Same or AttributeMapStatus.Renamed;

    /// <summary>
    /// Builds the tab over a throwaway database holding the two 1516-2010 samples, and hands it to
    /// <paramref name="body"/> with its map already built.
    /// </summary>
    /// <param name="body">Runs against the built tab.</param>
    /// <param name="mutateRight">
    /// Edits FOM B before it is registered. The samples happen to carry no renamed datatypes, so the
    /// half of the chip that covers renames has to have one made for it.
    /// </param>
    private static void WithMap(Action<AttributeMapViewModel> body, Action<FomDocument>? mutateRight = null)
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
    /// Pumps the dispatcher until the rebuild finishes. The body runs inside a blocking
    /// <c>Dispatcher.Invoke</c>, so the rebuild's continuation cannot run unless something drives it.
    /// </summary>
    private static void RunTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, "The map was not built within 60 seconds.");
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
