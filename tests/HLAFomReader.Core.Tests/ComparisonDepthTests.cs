using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// Comparison depth. The default is name-and-datatype, because that is what decides whether two FOMs
/// interoperate; sharing, ownership, accuracy and prose mostly generate noise. Nothing is hidden at
/// any depth — the rows are still recorded with both values, they just stop counting.
/// </summary>
public sealed class ComparisonDepthTests
{
    private readonly ITestOutputHelper _output;

    public ComparisonDepthTests(ITestOutputHelper output) => _output = output;

    private static string Samples
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "samples")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "samples");
        }
    }

    private static FomDocument Parse(string fileName) =>
        FomFileReader.ParseFile(Path.Combine(Samples, fileName));

    private static ComparisonResult Compare(string left, string right, ComparisonDepth depth) =>
        new FomComparer().Compare(Parse(left), Parse(right), new ComparisonOptions { Depth = depth });

    [Fact]
    public void TheDefaultIsNameAndDatatype()
    {
        Assert.Equal(ComparisonDepth.DataTypes, new ComparisonOptions().Depth);
    }

    [Fact]
    public void DeeperComparisonNeverReportsFewerDifferences()
    {
        var structure = Compare("RestaurantFOM-1.3.fed", "RestaurantFOM-1516-2010.xml", ComparisonDepth.Structure);
        var types = Compare("RestaurantFOM-1.3.fed", "RestaurantFOM-1516-2010.xml", ComparisonDepth.DataTypes);
        var full = Compare("RestaurantFOM-1.3.fed", "RestaurantFOM-1516-2010.xml", ComparisonDepth.Full);

        _output.WriteLine($"structure={structure.TotalDifferences} " +
                          $"types={types.TotalDifferences} full={full.TotalDifferences}");

        Assert.True(structure.TotalDifferences <= types.TotalDifferences);
        Assert.True(types.TotalDifferences < full.TotalDifferences);
    }

    [Fact]
    public void StructureDepthReportsOnlyPresenceAndAbsence()
    {
        var result = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.Structure);

        // Added and removed elements always count — depth governs how much of a MATCHED element is
        // inspected, never which elements are matched.
        Assert.Equal(9, result.AddedCount);
        Assert.Equal(2, result.RemovedCount);

        // ...but nothing is reported as modified, because no property is compared.
        Assert.Equal(0, result.ModifiedCount);

        Assert.DoesNotContain(
            result.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => p.IsDifferent);
    }

    [Fact]
    public void DataTypeDepthKeepsTheDatatypeChangeAndDropsTheRest()
    {
        var result = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.DataTypes);

        foreach (var difference in result.Differences())
            _output.WriteLine($"{difference.Kind,-9} {difference.Category,-18} {difference.Path}");

        Assert.Equal(9, result.AddedCount);
        Assert.Equal(2, result.RemovedCount);

        var differing = result.Root.DescendantsAndSelf()
            .SelectMany(n => n.Properties)
            .Where(p => p.IsDifferent)
            .ToList();

        // Customer.PartySize changed dataType; that must survive.
        Assert.Contains(differing, p => p.Property.Equals("DataType", StringComparison.Ordinal));

        // Transportation, Order and the identification version bump must not count at this depth.
        Assert.DoesNotContain(differing, p => p.Property.Contains("ransport", StringComparison.Ordinal));
        Assert.DoesNotContain(differing, p => p.Property.Equals("Order", StringComparison.Ordinal));
        Assert.DoesNotContain(differing, p => p.Property.Equals("Version", StringComparison.Ordinal));
    }

    [Fact]
    public void ShallowDepthsRecordEveryPropertyEvenWhenTheyDoNotCountIt()
    {
        var shallow = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.Structure);
        var full = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.Full);

        // Same rows on the detail pane either way — depth changes what counts, not what is shown.
        var shallowRows = shallow.Root.DescendantsAndSelf().SelectMany(n => n.Properties).Count();
        var fullRows = full.Root.DescendantsAndSelf().SelectMany(n => n.Properties).Count();

        _output.WriteLine($"property rows: structure={shallowRows} full={fullRows}");
        Assert.Equal(fullRows, shallowRows);

        // And the values are still there to read.
        Assert.Contains(
            shallow.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => !string.IsNullOrEmpty(p.LeftValue) || !string.IsNullOrEmpty(p.RightValue));
    }

    [Fact]
    public void DatatypeDefinitionsAreStillComparedAtDataTypeDepth()
    {
        var result = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.DataTypes);

        var paths = result.Differences().Select(d => d.Path).ToList();

        // v2 adds an enumerator and changes an array's cardinality. A datatype's members ARE its
        // definition, so both must survive at this depth.
        Assert.Contains(paths, p => p.Contains("DrinkKindEnum", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.Contains("SectionArray", StringComparison.Ordinal));
    }

    /// <summary>
    /// Against a FED specifically, the default depth is quieter but still loud — and correctly so.
    /// A FED has no datatypes at all, so once DataType is the property being compared, every single
    /// attribute differs on it. That is a real interoperability signal, not noise: you cannot agree
    /// on a wire format the other side never declared. Pairing the default depth with the
    /// format-gap filter is what actually reduces it to the authored differences.
    /// </summary>
    [Fact]
    public void TheDefaultDepthIsQuieterAndPairsWithTheFormatGapFilter()
    {
        var full = Compare("RestaurantFOM-1.3.fed", "RestaurantFOM-1516-2010.xml", ComparisonDepth.Full);
        var normal = Compare("RestaurantFOM-1.3.fed", "RestaurantFOM-1516-2010.xml", ComparisonDepth.DataTypes);

        var focused = new FomComparer().Compare(
            Parse("RestaurantFOM-1.3.fed"),
            Parse("RestaurantFOM-1516-2010.xml"),
            new ComparisonOptions
            {
                Depth = ComparisonDepth.DataTypes,
                IgnoreInexpressibleProperties = true,
            });

        _output.WriteLine($"full={full.TotalDifferences} default={normal.TotalDifferences} " +
                          $"default+filter={focused.TotalDifferences}");

        Assert.True(normal.TotalDifferences < full.TotalDifferences,
            $"full={full.TotalDifferences} default={normal.TotalDifferences}");

        Assert.True(focused.TotalDifferences <= 12,
            $"expected the combination to leave only authored differences, got {focused.TotalDifferences}");
    }

    /// <summary>
    /// Between two documents that BOTH have datatypes — a 1.3 OMT and an Evolved FOM — the default
    /// depth is genuinely comparing like with like, which is the case it was designed for.
    /// </summary>
    [Fact]
    public void AgainstAnOmtTheDefaultDepthComparesRealTypesOnBothSides()
    {
        var result = Compare("RestaurantFOM-1.3.omt", "RestaurantFOM-1516-2010.xml", ComparisonDepth.DataTypes);

        var dataTypeRows = result.Root.DescendantsAndSelf()
            .SelectMany(n => n.Properties)
            .Where(p => p.Property.Equals("DataType", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(dataTypeRows);
        Assert.Contains(dataTypeRows, p => !string.IsNullOrWhiteSpace(p.LeftValue)
                                        && !string.IsNullOrWhiteSpace(p.RightValue));

        _output.WriteLine($"omt vs evolved at default depth: {result.TotalDifferences} differences");
    }

    [Fact]
    public void AnAdvisoryExplainsWhatTheShallowerDepthSetAside()
    {
        var result = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.DataTypes);

        _output.WriteLine(string.Join("\n", result.Advisories));

        Assert.Contains(result.Advisories,
            a => a.Contains("datatype", StringComparison.OrdinalIgnoreCase)
              || a.Contains("Full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FullDepthIsUnchangedByTheFeature()
    {
        // The safety net: Full must still produce exactly the counts pinned before depth existed.
        var result = Compare("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            ComparisonDepth.Full);

        Assert.Equal(9, result.AddedCount);
        Assert.Equal(2, result.RemovedCount);
        Assert.Equal(7, result.ModifiedCount);
        Assert.Equal(18, result.TotalDifferences);
    }
}
