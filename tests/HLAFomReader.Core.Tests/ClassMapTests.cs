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
/// Flattening a comparison into one row per class, with a reason a reader can act on.
/// </summary>
/// <remarks>
/// The screen this feeds replaced a tree whose only verdict was a coloured square. An amber square
/// on a class says "Modified", which on a real pair almost always means "one of its attributes
/// changed" — work for the attribute map, not for the class list — and there was no way to tell that
/// apart from a class whose own declaration was edited. These pin down that the flattened row says
/// which of the two it is.
/// </remarks>
public sealed class ClassMapTests
{
    private readonly ITestOutputHelper _output;

    public ClassMapTests(ITestOutputHelper output) => _output = output;

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

    private static ClassMap MapOf(FomDocument left, FomDocument right, ComparisonOptions? options = null) =>
        ClassMap.Build(new FomComparer().Compare(left, right, options ?? new ComparisonOptions()).Root);

    /// <summary>A two-class FOM: a root with one child carrying one attribute.</summary>
    private static FomDocument Simple(
        string childName = "Aircraft",
        string attributeName = "AfterburnerOn",
        string attributeType = "uint:8",
        string? childSharing = null,
        string rootName = "ObjectRoot")
    {
        var child = new FomObjectClass
        {
            Name = childName,
            QualifiedName = $"{rootName}.{childName}",
            Sharing = childSharing,
        };
        child.Attributes.Add(new FomAttribute { Name = attributeName, DataType = attributeType });

        var root = new FomObjectClass { Name = rootName, QualifiedName = rootName };
        root.Children.Add(child);

        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.ObjectClasses.Add(root);
        return document;
    }

    private static ClassMapRow Row(ClassMap map, string name) =>
        map.Rows.Single(r => r.Name == name);

    // ---- the five outcomes ----------------------------------------------------------------

    [Fact]
    public void AnIdenticalPairIsAllSameAndNeedsNoAttention()
    {
        var map = MapOf(Simple(), Simple());

        Assert.All(map.Rows, r => Assert.Equal(ClassMapStatus.Same, r.Status));
        Assert.Equal(0, map.ActionableCount);
        Assert.All(map.Rows, r => Assert.False(r.NeedsAttention));
    }

    [Fact]
    public void AClassOnlyInAIsReportedWithSomewhereForItsDataToGo()
    {
        var left = Simple();
        left.ObjectClasses[0].Children.Add(new FomObjectClass
        {
            Name = "BaseEntityOther",
            QualifiedName = "ObjectRoot.BaseEntityOther",
        });

        var map = MapOf(left, Simple());
        var row = Row(map, "BaseEntityOther");

        Assert.Equal(ClassMapStatus.OnlyInLeft, row.Status);
        Assert.True(row.NeedsAttention);
        Assert.Equal("ObjectRoot.BaseEntityOther", row.LeftName);
        Assert.Null(row.RightName);
        Assert.Contains("Nothing in FOM B", row.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassOnlyInBIsReportedWithNothingFeedingIt()
    {
        var right = Simple();
        right.ObjectClasses[0].Children.Add(new FomObjectClass
        {
            Name = "Spacecraft",
            QualifiedName = "ObjectRoot.Spacecraft",
        });

        var map = MapOf(Simple(), right);
        var row = Row(map, "Spacecraft");

        Assert.Equal(ClassMapStatus.OnlyInRight, row.Status);
        Assert.True(row.NeedsAttention);
        Assert.Contains("Nothing in FOM A", row.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedAttributeNamesTheAttributeCountAndSendsTheReaderToTheAttributeMap()
    {
        // The case the old tree could not explain: the class itself is untouched, and it is flagged
        // only because a member was retyped.
        var map = MapOf(Simple(attributeType: "uint:8"), Simple(attributeType: "float:32"));
        var row = Row(map, "Aircraft");

        Assert.Equal(ClassMapStatus.Changed, row.Status);
        Assert.Equal(1, row.ChangedMemberCount);
        Assert.Empty(row.ChangedProperties);

        Assert.Contains("1 attribute differs", row.Why, StringComparison.Ordinal);
        Assert.Contains("see Attribute data", row.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddedAttributeIsCountedSeparatelyFromARetypedOne()
    {
        var right = Simple();
        right.ObjectClasses[0].Children[0].Attributes.Add(
            new FomAttribute { Name = "TailNumber", DataType = "uint:32" });

        var map = MapOf(Simple(), right);
        var row = Row(map, "Aircraft");

        Assert.Equal(ClassMapStatus.Changed, row.Status);
        Assert.Equal(1, row.AddedMemberCount);
        Assert.Equal(0, row.ChangedMemberCount);
        Assert.Contains("only in FOM B", row.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassLevelEditIsNamedRatherThanCounted()
    {
        // "Sharing differs" is actionable; "1 property differs" is not.
        var map = MapOf(
            Simple(childSharing: "PublishSubscribe"),
            Simple(childSharing: "Publish"),
            new ComparisonOptions { Depth = ComparisonDepth.Full });

        var row = Row(map, "Aircraft");

        Assert.Equal(ClassMapStatus.Changed, row.Status);
        Assert.Contains("Sharing", row.ChangedProperties);
        Assert.Contains("Sharing differs", row.Why, StringComparison.Ordinal);
    }

    // ---- the thing the tree got wrong -------------------------------------------------------

    [Fact]
    public void AContainerIsNotReportedAsChangedJustBecauseSomethingBeneathItIs()
    {
        // This is the whole reason for the rewrite. In the tree, an edit on Aircraft turned every
        // ancestor amber, so a reader could not see which class was actually touched. Here the
        // ancestor stays Same and says why it is mentioned at all.
        var map = MapOf(Simple(attributeType: "uint:8"), Simple(attributeType: "float:32"));

        var root = Row(map, "ObjectRoot");
        Assert.Equal(ClassMapStatus.Same, root.Status);
        Assert.False(root.NeedsAttention);
        Assert.Contains("a class beneath it differs", root.Why, StringComparison.OrdinalIgnoreCase);

        var aircraft = Row(map, "Aircraft");
        Assert.Equal(ClassMapStatus.Changed, aircraft.Status);
    }

    [Fact]
    public void AClassCountsOnlyItsOwnMembersAndNotItsDescendants()
    {
        // The node's rollup counts cover the whole subtree, so reading them would let a root class
        // claim every change in the FOM as its own.
        var left = Simple();
        var right = Simple(attributeType: "float:32");

        var map = MapOf(left, right);

        Assert.Equal(0, Row(map, "ObjectRoot").ChangedMemberCount);
        Assert.Equal(1, Row(map, "Aircraft").ChangedMemberCount);
    }

    [Fact]
    public void ARenameWithNothingElseDifferentIsNotWork()
    {
        var map = MapOf(
            Simple(rootName: "ObjectRoot"),
            Simple(rootName: "HLAobjectRoot"),
            new ComparisonOptions { NormalizeRootNames = true });

        var root = map.Rows.Single(r => r.LeftName == "ObjectRoot");

        Assert.Equal(ClassMapStatus.Renamed, root.Status);
        Assert.False(root.NeedsAttention);
        Assert.Equal("HLAobjectRoot", root.RightName);
        Assert.Contains("nothing to convert", root.Why, StringComparison.OrdinalIgnoreCase);
    }

    // ---- against the real samples -----------------------------------------------------------

    [Fact]
    public void EveryClassOfARealPairGetsExactlyOneRowAndAReason()
    {
        var map = MapOf(Parse("RestaurantFOM-1516-2010.xml"), Parse("RestaurantFOM-1516-2010-v2.xml"));

        Assert.NotEmpty(map.Rows);

        // One row per class, no duplicates.
        var names = map.Rows.Select(r => r.QualifiedName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // Anything asking for work must say what the work is.
        Assert.All(map.Rows.Where(r => r.NeedsAttention), r =>
            Assert.False(string.IsNullOrWhiteSpace(r.Why), $"{r.QualifiedName} is flagged with no reason"));

        foreach (var row in map.Rows)
            _output.WriteLine($"{row.Status,-12} {row.QualifiedName,-52} {row.Why}");
    }

    [Fact]
    public void ACrossStandardPairFlattensWithoutThrowing()
    {
        var map = MapOf(Parse("RestaurantFOM-1.3.fed"), Parse("RestaurantFOM-1516-2010.xml"));

        Assert.NotEmpty(map.Rows);
        Assert.All(map.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void InteractionsAreReportedInParametersRatherThanAttributes()
    {
        var map = MapOf(Parse("RestaurantFOM-1516-2010.xml"), Parse("RestaurantFOM-1516-2010-v2.xml"));

        var interactions = map.Rows.Where(r => r.IsInteraction).ToList();
        Assert.NotEmpty(interactions);
        Assert.All(interactions, r => Assert.Equal("parameter", r.MemberLabel));

        // And a changed interaction must never send the reader to a tab that does not cover it.
        foreach (var row in interactions.Where(r => r.Status == ClassMapStatus.Changed))
            Assert.DoesNotContain("Attribute data", row.Why, StringComparison.Ordinal);
    }
}
