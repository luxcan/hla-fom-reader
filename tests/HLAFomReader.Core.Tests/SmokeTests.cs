using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using HLAFomReader.Core.Registry;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// End-to-end checks over the shipped sample FOMs: detect, parse, store in SQLite, read back,
/// and compare. These are the tests that would catch a regression in any single layer.
/// </summary>
public sealed class SmokeTests
{
    private readonly ITestOutputHelper _output;

    public SmokeTests(ITestOutputHelper output) => _output = output;

    private static string SamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    private static string Sample(string name) => Path.Combine(SamplesDirectory, name);

    [Theory]
    [InlineData("RestaurantFOM-1.3.fed", FomStandard.Hla13)]
    [InlineData("RestaurantFOM-1516-2010.xml", FomStandard.Ieee1516_2010)]
    [InlineData("RestaurantFOM-1516-2010-v2.xml", FomStandard.Ieee1516_2010)]
    [InlineData("RestaurantFOM-1516-2000.xml", FomStandard.Ieee1516_2000)]
    public void DetectsTheStandardOfEverySample(string fileName, FomStandard expected)
    {
        var path = Sample(fileName);
        Assert.True(File.Exists(path), $"Missing sample: {path}");
        Assert.Equal(expected, FomFileReader.DetectStandard(path));
    }

    [Theory]
    [InlineData("RestaurantFOM-1.3.fed")]
    [InlineData("RestaurantFOM-1516-2010.xml")]
    [InlineData("RestaurantFOM-1516-2010-v2.xml")]
    [InlineData("RestaurantFOM-1516-2000.xml")]
    public void ParsesEverySampleWithoutErrors(string fileName)
    {
        var document = FomFileReader.ParseFile(Sample(fileName));

        foreach (var diagnostic in document.Diagnostics)
            _output.WriteLine(diagnostic.ToString());

        _output.WriteLine(
            $"{fileName}: {document.ObjectClassCount} classes, {document.AttributeCount} attributes, " +
            $"{document.InteractionClassCount} interactions, {document.ParameterCount} parameters, " +
            $"{document.DataTypeCount} datatypes, {document.DimensionCount} dimensions");

        Assert.False(document.HasErrors,
            "Parse errors: " + string.Join(" | ", document.Diagnostics.Select(d => d.ToString())));
        Assert.True(document.ObjectClassCount > 1, "Expected an object class tree");
        Assert.True(document.AttributeCount > 0, "Expected attributes");
        Assert.True(document.InteractionClassCount > 0, "Expected interaction classes");
    }

    [Fact]
    public void EvolvedSampleCarriesAllSixDatatypeTables()
    {
        var document = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010.xml"));
        var types = document.DataTypes;

        Assert.NotEmpty(types.BasicDataRepresentations);
        Assert.NotEmpty(types.SimpleDataTypes);
        Assert.NotEmpty(types.EnumeratedDataTypes);
        Assert.NotEmpty(types.ArrayDataTypes);
        Assert.NotEmpty(types.FixedRecordDataTypes);
        Assert.NotEmpty(types.VariantRecordDataTypes);
        Assert.NotEmpty(document.Dimensions);
        Assert.NotEmpty(document.Switches);
    }

    [Fact]
    public void Hla13SampleCarriesRoutingSpacesAndNoDatatypes()
    {
        var document = FomFileReader.ParseFile(Sample("RestaurantFOM-1.3.fed"));

        Assert.NotEmpty(document.RoutingSpaces);
        Assert.True(document.DataTypes.IsEmpty, "HLA 1.3 has no datatype table");
        Assert.Empty(document.Dimensions);
        Assert.Contains(document.AllObjectClasses(), c => c.Name == "ObjectRoot");
    }

    [Fact]
    public void ComparingTheEvolvedSampleWithItselfFindsNoDifferences()
    {
        var a = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010.xml"));
        var b = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010.xml"));

        var result = new FomComparer().Compare(a, b);

        foreach (var difference in result.Differences().Take(25))
            _output.WriteLine($"{difference.Kind} {difference.Category} {difference.Path}");

        Assert.True(result.AreIdentical, $"Expected no differences, found {result.TotalDifferences}");
    }

    [Fact]
    public void ComparingV1AgainstV2FindsTheAuthoredChanges()
    {
        var a = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010.xml"));
        var b = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010-v2.xml"));

        var result = new FomComparer().Compare(a, b);

        foreach (var difference in result.Differences())
            _output.WriteLine($"{difference.Kind,-9} {difference.Category,-18} {difference.Path}");

        Assert.False(result.AreIdentical);
        Assert.True(result.AddedCount > 0, "v2 adds classes/attributes");
        Assert.True(result.RemovedCount > 0, "v2 removes an attribute and a parameter");
        Assert.True(result.ModifiedCount > 0, "v2 changes datatypes and transportation");
    }

    [Fact]
    public void CrossStandardComparisonLinesUpTheRootsAndFlagsInexpressibleProperties()
    {
        var fed = FomFileReader.ParseFile(Sample("RestaurantFOM-1.3.fed"));
        var fom = FomFileReader.ParseFile(Sample("RestaurantFOM-1516-2010.xml"));

        var result = new FomComparer().Compare(fed, fom);

        _output.WriteLine($"Advisories: {string.Join(" | ", result.Advisories)}");
        _output.WriteLine($"+{result.AddedCount} -{result.RemovedCount} ~{result.ModifiedCount} ={result.UnchangedCount}");

        Assert.True(result.IsCrossStandard);
        Assert.NotEmpty(result.Advisories);

        // Root normalisation must have matched ObjectRoot with HLAobjectRoot rather than
        // reporting the whole tree twice.
        var objectsSection = result.Root.Children.FirstOrDefault(c => c.Path.StartsWith("objects", StringComparison.Ordinal));
        Assert.NotNull(objectsSection);
        Assert.Contains(objectsSection!.DescendantsAndSelf(),
            n => n.Kind != DiffKind.Added && n.Kind != DiffKind.Removed);

        // Strict mode: things HLA 1.3 cannot express are real differences and carry a reason.
        Assert.Contains(
            result.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => p.IsDifferent && p.Reason is not null && p.Reason.Contains("1.3", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterAndReloadRoundTripsThroughSqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hlafomreader-test-{Guid.NewGuid():N}.db");

        try
        {
            using var repository = new SqliteFomRepository(databasePath);

            var path = Sample("RestaurantFOM-1516-2010.xml");
            var parsed = FomFileReader.ParseFile(path);
            var entry = repository.Register(parsed, "Restaurant Evolved", path);

            Assert.True(entry.Id > 0);
            Assert.Equal(parsed.ObjectClassCount, entry.ObjectClassCount);
            Assert.Equal(parsed.AttributeCount, entry.AttributeCount);
            Assert.Equal(parsed.DataTypeCount, entry.DataTypeCount);
            Assert.False(string.IsNullOrWhiteSpace(entry.FileHash));

            var reloaded = repository.LoadDocument(entry.Id);

            Assert.Equal(parsed.Standard, reloaded.Standard);
            Assert.Equal(parsed.ObjectClassCount, reloaded.ObjectClassCount);
            Assert.Equal(parsed.AttributeCount, reloaded.AttributeCount);
            Assert.Equal(parsed.InteractionClassCount, reloaded.InteractionClassCount);
            Assert.Equal(parsed.ParameterCount, reloaded.ParameterCount);
            Assert.Equal(parsed.DataTypeCount, reloaded.DataTypeCount);
            Assert.Equal(parsed.DimensionCount, reloaded.DimensionCount);
            Assert.Equal(parsed.Identification.Name, reloaded.Identification.Name);

            // The round trip must be diff-clean, otherwise every comparison would show phantom changes.
            var result = new FomComparer().Compare(parsed, reloaded);
            foreach (var difference in result.Differences().Take(25))
                _output.WriteLine($"{difference.Kind} {difference.Category} {difference.Path}");

            Assert.True(result.AreIdentical,
                $"SQLite round trip changed the model: {result.TotalDifferences} differences");

            Assert.Single(repository.ListEntries());
            repository.Delete(entry.Id);
            Assert.Empty(repository.ListEntries());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}
