using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Parsing;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// samples/README.md documents exactly twelve authored changes between v1 and v2 of the Evolved
/// sample. Expanded to leaf elements that is nine additions, two removals and seven modifications.
/// These tests pin those numbers so a regression in matching or in the rollup shows up immediately.
/// </summary>
public sealed class DifferenceCountTests
{
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

    /// <summary>
    /// These tests pin exhaustive counts, so they ask for <see cref="ComparisonDepth.Full"/>
    /// explicitly rather than riding on the default — which is deliberately shallower.
    /// </summary>
    private static ComparisonResult CompareSamples(string left, string right, ComparisonOptions? options = null)
    {
        options ??= new ComparisonOptions();
        options.Depth = ComparisonDepth.Full;

        return new FomComparer().Compare(
            FomFileReader.ParseFile(Path.Combine(Samples, left)),
            FomFileReader.ParseFile(Path.Combine(Samples, right)),
            options);
    }

    [Fact]
    public void EvolvedV1VsV2ReportsExactlyTheAuthoredLeafChanges()
    {
        var result = CompareSamples("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml");

        Assert.Equal(9, result.AddedCount);
        Assert.Equal(2, result.RemovedCount);
        Assert.Equal(7, result.ModifiedCount);
        Assert.Equal(18, result.TotalDifferences);
    }

    [Fact]
    public void ContainersOnThePathToAChangeAreShownButNotCounted()
    {
        var result = CompareSamples("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml");

        // Employee itself is untouched; only its Chef/Waiter/Dishwasher children changed.
        var employee = result.Root.DescendantsAndSelf()
            .Single(n => n.Path == "objects/HLAobjectRoot.Employee");

        Assert.Equal(DiffKind.Modified, employee.Kind);   // visible in the tree
        Assert.False(employee.HasOwnChange);              // but not itself a difference
        Assert.DoesNotContain(employee.Properties, p => p.IsDifferent);

        // The attribute that really changed does carry an own change.
        var tipTotal = result.Root.DescendantsAndSelf()
            .Single(n => n.Path == "objects/HLAobjectRoot.Employee.Waiter/TipTotal");

        Assert.True(tipTotal.HasOwnChange);
        Assert.Contains(tipTotal.Properties,
            p => p.Property.Contains("ransport", StringComparison.Ordinal) && p.IsDifferent);
    }

    [Fact]
    public void EachAuthoredChangeAppearsExactlyOnceInTheDiff()
    {
        var result = CompareSamples("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml");
        var paths = result.Differences().Select(d => d.Path).ToList();

        Assert.Equal(paths.Count, paths.Distinct().Count());

        Assert.Contains("objects/HLAobjectRoot.Employee.Manager", paths);
        Assert.Contains("objects/HLAobjectRoot.Customer/LoyaltyPoints", paths);
        Assert.Contains("objects/HLAobjectRoot.Employee.Chef/YearsExperience", paths);
        Assert.Contains("interactions/HLAinteractionRoot.Communication.TableAssignment", paths);
        Assert.Contains("interactions/HLAinteractionRoot.Communication.ServiceComplaint/Severity", paths);
        Assert.Contains("switches/conveyProducingFederate", paths);
    }

    [Fact]
    public void SkippingDatatypesRemovesTheDatatypeDifferencesAndNothingElse()
    {
        var withTypes = CompareSamples("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml");
        var withoutTypes = CompareSamples("RestaurantFOM-1516-2010.xml", "RestaurantFOM-1516-2010-v2.xml",
            new ComparisonOptions { IgnoreDataTypes = true });

        // v2 adds one enumerator and changes one array cardinality.
        Assert.Equal(withTypes.AddedCount - 1, withoutTypes.AddedCount);
        Assert.Equal(withTypes.ModifiedCount - 1, withoutTypes.ModifiedCount);
        Assert.Equal(withTypes.RemovedCount, withoutTypes.RemovedCount);
    }

    [Fact]
    public void The1516_2000SampleDiffersFromEvolvedOnlyInTheVersionSpecificTables()
    {
        var result = CompareSamples("RestaurantFOM-1516-2000.xml", "RestaurantFOM-1516-2010.xml");

        Assert.True(result.IsCrossStandard);

        // The class and interaction trees are the same federation, so nothing there should move.
        var classSections = result.Root.Children
            .Where(c => c.Path is "objects" or "interactions")
            .ToList();

        Assert.All(classSections, section =>
        {
            Assert.Equal(0, section.AddedCount);
            Assert.Equal(0, section.RemovedCount);
        });
    }
}
