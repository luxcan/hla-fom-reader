using System;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Merging;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The attribute data map answers one question: what data does each class carry, and how is it typed
/// on each side? It works on the EFFECTIVE attribute set, because HLA classes inherit everything
/// their ancestors declare — a subclass that declares nothing still publishes the inherited set.
/// </summary>
public sealed class AttributeMapperTests
{
    private readonly ITestOutputHelper _output;

    public AttributeMapperTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public void AnIdenticalPairHasNothingToRemap()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010.xml"));

        Assert.NotEmpty(map.Rows);
        Assert.Equal(0, map.ActionableCount);
        Assert.All(map.Rows, r => Assert.Equal(AttributeMapStatus.Same, r.Status));
    }

    /// <summary>
    /// The crux. A subclass that declares nothing still carries everything its ancestors declare, so
    /// the map must list the inherited attributes against it.
    /// </summary>
    [Fact]
    public void InheritedAttributesAreMappedAgainstTheSubclass()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010.xml"));

        // Chef declares its own attributes but also inherits Employee's and HLAobjectRoot's.
        var chefRows = map.Rows.Where(r => r.ClassName.EndsWith("Chef", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(chefRows);
        Assert.Contains(chefRows, r => r.AttributeName == "EmployeeID");            // from Employee
        Assert.Contains(chefRows, r => r.LeftDeclaredIn == "Employee");
        Assert.Contains(chefRows, r => r.AttributeName.Contains("privilegeToDelete", StringComparison.OrdinalIgnoreCase));

        foreach (var row in chefRows)
            _output.WriteLine($"{row.AttributeName,-24} declared in {row.LeftDeclaredIn,-16} {row.LeftDataType}");
    }

    [Fact]
    public void ADatatypeChangeIsTheRowThatMeansWork()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010-v2.xml"));

        foreach (var row in map.Rows.Where(r => r.IsDifferent))
            _output.WriteLine($"{row.Status,-16} {row.QualifiedName,-46} {row.LeftDataType} -> {row.RightDataType}");

        // v2 retypes Customer.PartySize.
        var partySize = map.Rows.Where(r => r.AttributeName == "PartySize").ToList();
        Assert.NotEmpty(partySize);
        Assert.All(partySize, r => Assert.Equal(AttributeMapStatus.DataTypeChanged, r.Status));
        Assert.All(partySize, r => Assert.NotEqual(r.LeftDataType, r.RightDataType));

        // v2 adds Customer.LoyaltyPoints and removes Chef.YearsExperience.
        Assert.Contains(map.Rows, r => r.AttributeName == "LoyaltyPoints" && r.Status == AttributeMapStatus.OnlyInRight);
        Assert.Contains(map.Rows, r => r.AttributeName == "YearsExperience" && r.Status == AttributeMapStatus.OnlyInLeft);
    }

    [Fact]
    public void AWholeClassAppearingContributesItsAttributes()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010-v2.xml"));

        // v2 adds the Manager class; a remap has to account for every attribute it brings.
        var manager = map.Rows.Where(r => r.ClassName.Contains("Manager", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(manager);
        Assert.Contains(manager, r => r.AttributeName == "ShiftsSupervised");
        Assert.Contains(manager, r => r.Status == AttributeMapStatus.OnlyInRight);
    }

    /// <summary>
    /// A FED has no datatypes at all. Reporting every attribute as "datatype changed" against a 1516
    /// FOM would be technically true and completely useless, so the mapper says so once instead.
    /// </summary>
    [Fact]
    public void AFedWithNoDatatypesIsExplainedRatherThanFlaggedOnEveryRow()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1.3.fed"),
            Parse("RestaurantFOM-1516-2010.xml"));

        _output.WriteLine(string.Join("\n", map.Advisories));
        _output.WriteLine($"same={map.SameCount} changed={map.DataTypeChangedCount} " +
                          $"onlyA={map.OnlyInLeftCount} onlyB={map.OnlyInRightCount}");

        Assert.NotEmpty(map.Advisories);

        // The names line up, so the vast majority must NOT be reported as datatype changes.
        var matched = map.Rows.Count(r => r.Status is AttributeMapStatus.Same or AttributeMapStatus.Moved);
        Assert.True(matched > map.DataTypeChangedCount,
            $"expected the blank-datatype side to be explained, not flagged: " +
            $"{map.DataTypeChangedCount} changes against {matched} matched");
    }

    /// <summary>
    /// With the OMT merged in, both sides have real types — and this is the comparison that is
    /// actually worth acting on.
    /// </summary>
    [Fact]
    public void MergingTheOmtMakesTheDatatypeColumnMeaningful()
    {
        var merged = FomMerger.Merge(Parse("RestaurantFOM-1.3.fed"), Parse("RestaurantFOM-1.3.omt")).Document;

        var map = AttributeMapper.Build(merged, Parse("RestaurantFOM-1516-2010.xml"));

        var typedBothSides = map.Rows.Count(r =>
            !string.IsNullOrWhiteSpace(r.LeftDataType) && !string.IsNullOrWhiteSpace(r.RightDataType));

        _output.WriteLine($"rows typed on both sides: {typedBothSides} of {map.Rows.Count}");
        Assert.True(typedBothSides > 0, "the merged 1.3 side should carry datatypes");
    }

    [Fact]
    public void RowsAreGroupedByClassSoTheOutputReadsAsAWorksheet()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010-v2.xml"));

        // Every row for a given class must be contiguous — this is read top to bottom by a human.
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        string? previous = null;

        foreach (var row in map.Rows)
        {
            if (row.ClassName == previous) continue;

            Assert.True(seen.Add(row.ClassName),
                $"rows for {row.ClassName} are split into more than one block");
            previous = row.ClassName;
        }
    }

    /// <summary>
    /// An attribute that changes which class declares it is still available on the subclass through
    /// inheritance, so there is nothing to convert — but it is worth saying. Verified with a
    /// synthetic move because the real RPR 1.0 to 2.0 migration relocates none: of 872 attributes
    /// present on both sides, zero change their declaring class.
    /// </summary>
    [Fact]
    public void RelocatingAnAttributeUpOrDownTheHierarchyReportsAsMoved()
    {
        var left = Parse("RestaurantFOM-1516-2010.xml");
        var right = Parse("RestaurantFOM-1516-2010.xml");

        var employee = right.AllObjectClasses().Single(c => c.Name == "Employee");
        var chef = right.AllObjectClasses().Single(c => c.Name == "Chef");
        var hoursWorked = employee.Attributes.Single(a => a.Name == "HoursWorked");

        // Same attribute, same datatype, declared one level further down.
        employee.Attributes.Remove(hoursWorked);
        chef.Attributes.Add(hoursWorked);

        var map = AttributeMapper.Build(left, right);

        var chefRow = map.Rows.Single(r =>
            r.ClassName.EndsWith("Chef", StringComparison.Ordinal) && r.AttributeName == "HoursWorked");

        Assert.Equal(AttributeMapStatus.Moved, chefRow.Status);
        Assert.Equal("Employee", chefRow.LeftDeclaredIn);
        Assert.Equal("Chef", chefRow.RightDeclaredIn);
        Assert.Equal(chefRow.LeftDataType, chefRow.RightDataType);

        // Employee itself loses it outright — that side really is a removal.
        var employeeRow = map.Rows.Single(r =>
            r.ClassName.EndsWith("Employee", StringComparison.Ordinal) && r.AttributeName == "HoursWorked");
        Assert.Equal(AttributeMapStatus.OnlyInLeft, employeeRow.Status);
    }

    [Fact]
    public void TheMapNamesBothSides()
    {
        var map = AttributeMapper.Build(
            Parse("RestaurantFOM-1516-2010.xml"),
            Parse("RestaurantFOM-1516-2010-v2.xml"));

        Assert.False(string.IsNullOrWhiteSpace(map.LeftLabel));
        Assert.False(string.IsNullOrWhiteSpace(map.RightLabel));
    }
}
