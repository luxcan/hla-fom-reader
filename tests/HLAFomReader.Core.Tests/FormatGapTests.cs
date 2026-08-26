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
/// A 1.3 FED against a 1516 FOM is dominated by differences nobody authored: HLA 1.3 simply has no
/// dataType, sharing, updateType, updateCondition, ownership, dimensions or semantics, so every
/// attribute differs on seven properties at once. Strict mode reports all of them, which is honest
/// but unreadable; IgnoreInexpressibleProperties is the escape hatch. These tests pin both modes.
/// </summary>
public sealed class FormatGapTests
{
    private readonly ITestOutputHelper _output;

    public FormatGapTests(ITestOutputHelper output) => _output = output;

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

    // Format gaps are a property-level phenomenon, so these measure at Full depth; the shallower
    // default would hide most of them for a different reason and confuse what is being tested.
    private static ComparisonResult CrossStandard(bool ignoreInexpressible) =>
        new FomComparer().Compare(
            Parse("RestaurantFOM-1.3.fed"),
            Parse("RestaurantFOM-1516-2010.xml"),
            new ComparisonOptions
            {
                IgnoreInexpressibleProperties = ignoreInexpressible,
                Depth = ComparisonDepth.Full,
            });

    [Fact]
    public void StrictModeStillReportsEveryFormatGap()
    {
        var result = CrossStandard(ignoreInexpressible: false);

        var message =
            $"+{result.AddedCount} -{result.RemovedCount} ~{result.ModifiedCount} " +
            $"| gaps={result.FormatGapPropertyCount} authored={result.AuthoredPropertyDifferenceCount} " +
            $"| total={result.TotalDifferences}";
        _output.WriteLine(message);

        // A cross-standard diff is dominated by structure, not by authored change.
        Assert.True(result.FormatGapPropertyCount > 200, message);
        Assert.True(result.FormatGapPropertyCount > result.AuthoredPropertyDifferenceCount, message);
        Assert.True(result.TotalDifferences > 100, message);

        // Of the nodes that exist on BOTH sides, almost every reported difference is a format gap:
        // 1.3 has no dataType, sharing, updateType, updateCondition, ownership, dimensions or
        // semantics, so each matched attribute differs on all of them at once. This is the ratio
        // that makes strict mode unreadable and motivates IgnoreInexpressibleProperties.
        var matchedProperties = result.Root.DescendantsAndSelf()
            .Where(n => n.Kind == DiffKind.Modified)
            .SelectMany(n => n.Properties)
            .Where(p => p.IsDifferent)
            .ToList();

        var gapsOnMatchedNodes = matchedProperties.Count(p => p.Reason is not null);

        _output.WriteLine($"matched-node differences: {matchedProperties.Count}, of which gaps: {gapsOnMatchedNodes}");
        Assert.True(gapsOnMatchedNodes > matchedProperties.Count * 0.9,
            $"Expected >90% of matched-node differences to be format gaps, got " +
            $"{gapsOnMatchedNodes}/{matchedProperties.Count}");
    }

    [Fact]
    public void FilteringFormatGapsLeavesTheAuthoredDifferences()
    {
        var strict = CrossStandard(ignoreInexpressible: false);
        var filtered = CrossStandard(ignoreInexpressible: true);

        foreach (var difference in filtered.Differences())
            _output.WriteLine($"{difference.Kind,-9} {difference.Category,-18} {difference.Path}");

        Assert.True(filtered.TotalDifferences < strict.TotalDifferences,
            "Filtering must remove differences, not add them.");

        // What survives is what the two files genuinely disagree about — a handful, not hundreds.
        Assert.True(filtered.TotalDifferences <= 12,
            $"Expected only authored differences to survive, got {filtered.TotalDifferences}");

        // Nothing that survives may be a format gap.
        Assert.DoesNotContain(
            filtered.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => p.IsDifferent && p.Reason is not null);

        // The reason text is kept on the row so the user can still see WHY a value is blank.
        Assert.Contains(
            filtered.Root.DescendantsAndSelf().SelectMany(n => n.Properties),
            p => p.Reason is not null);
    }

    [Fact]
    public void TransportationAndOrderStillCompareBecauseBothStandardsHaveThem()
    {
        var filtered = CrossStandard(ignoreInexpressible: true);

        var compared = filtered.Root.DescendantsAndSelf()
            .SelectMany(n => n.Properties)
            .Where(p => p.Property.Contains("ransport", StringComparison.Ordinal)
                     || p.Property.Equals("Order", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(compared);
        Assert.All(compared, p => Assert.Null(p.Reason));
    }

    [Fact]
    public void RealClassAndAttributeDifferencesSurviveFiltering()
    {
        // Strip an attribute out of the FED side; that absence is a genuine difference, not a
        // format gap, so it must survive even with filtering on.
        var fed = Parse("RestaurantFOM-1.3.fed");
        var fom = Parse("RestaurantFOM-1516-2010.xml");

        var customer = fom.AllObjectClasses().Single(c => c.Name == "Customer");
        var removed = customer.Attributes[0];
        customer.Attributes.RemoveAt(0);

        var filtered = new FomComparer().Compare(fed, fom,
            new ComparisonOptions { IgnoreInexpressibleProperties = true, Depth = ComparisonDepth.Full });

        Assert.Contains(filtered.Differences(),
            d => d.Kind == DiffKind.Removed && d.Name == removed.Name);
    }

    [Fact]
    public void TheOptionIsInertWhenBothSidesUseTheSameStandard()
    {
        var a = Parse("RestaurantFOM-1516-2010.xml");
        var b = Parse("RestaurantFOM-1516-2010-v2.xml");

        var strict = new FomComparer().Compare(a, b,
            new ComparisonOptions { Depth = ComparisonDepth.Full });
        var filtered = new FomComparer().Compare(a, b,
            new ComparisonOptions { IgnoreInexpressibleProperties = true, Depth = ComparisonDepth.Full });

        // No format gaps exist between two Evolved FOMs, so the option must change nothing.
        Assert.Equal(0, strict.FormatGapPropertyCount);
        Assert.Equal(strict.AddedCount, filtered.AddedCount);
        Assert.Equal(strict.RemovedCount, filtered.RemovedCount);
        Assert.Equal(strict.ModifiedCount, filtered.ModifiedCount);
        Assert.Equal(18, filtered.TotalDifferences);
    }

    [Fact]
    public void AnAdvisoryTellsTheUserHowMuchNoiseTheOptionWouldRemove()
    {
        var strict = CrossStandard(ignoreInexpressible: false);

        _output.WriteLine(string.Join("\n", strict.Advisories));

        Assert.Contains(strict.Advisories,
            a => a.Contains("format gap", StringComparison.OrdinalIgnoreCase)
              || a.Contains("cannot express", StringComparison.OrdinalIgnoreCase));
    }
}
