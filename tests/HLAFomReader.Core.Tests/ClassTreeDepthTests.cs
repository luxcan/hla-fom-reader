using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// How deep the Classes tab reaches into a real class hierarchy.
/// </summary>
/// <remarks>
/// RPR nests five deep before it names a vehicle — <c>ObjectRoot.BaseEntity.PhysicalEntity.Platform.Aircraft</c>
/// — so a comparison that stops short of the leaves reports on containers and hides everything the
/// reader came to see. These pin the shape of the tree the view is built from, in Core, where the
/// hierarchy is actually assembled.
/// </remarks>
public sealed class ClassTreeDepthTests
{
    /// <summary>Builds the RPR spine down to the vehicle classes, with an optional edit at the leaf.</summary>
    private static FomDocument Rpr(string aircraftDataType, string platformSharing = "PublishSubscribe")
    {
        var aircraft = new FomObjectClass
        {
            Name = "Aircraft",
            QualifiedName = "ObjectRoot.BaseEntity.PhysicalEntity.Platform.Aircraft",
        };
        aircraft.Attributes.Add(new FomAttribute { Name = "AfterburnerOn", DataType = aircraftDataType });

        var platform = new FomObjectClass
        {
            Name = "Platform",
            QualifiedName = "ObjectRoot.BaseEntity.PhysicalEntity.Platform",
            Sharing = platformSharing,
        };
        platform.Children.Add(aircraft);

        var physical = new FomObjectClass
        {
            Name = "PhysicalEntity",
            QualifiedName = "ObjectRoot.BaseEntity.PhysicalEntity",
        };
        physical.Children.Add(platform);

        var baseEntity = new FomObjectClass { Name = "BaseEntity", QualifiedName = "ObjectRoot.BaseEntity" };
        baseEntity.Children.Add(physical);

        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        root.Children.Add(baseEntity);

        var document = new FomDocument { Standard = FomStandard.Ieee1516_2010 };
        document.ObjectClasses.Add(root);
        return document;
    }

    private static DiffNode Compare(FomDocument left, FomDocument right, ComparisonDepth? depth = null) =>
        new FomComparer().Compare(left, right,
            depth is null ? new ComparisonOptions() : new ComparisonOptions { Depth = depth.Value }).Root;

    private static DiffNode? Find(DiffNode root, string name) =>
        root.DescendantsAndSelf().FirstOrDefault(n => n.Name == name);

    [Fact]
    public void AClassFiveLevelsDownIsStillReached()
    {
        var root = Compare(Rpr("uint:8"), Rpr("uint:8"));

        var aircraft = Find(root, "Aircraft");

        Assert.NotNull(aircraft);
        Assert.Equal(DiffCategory.ObjectClass, aircraft!.Category);
    }

    [Fact]
    public void AChangeAtTheLeafIsReportedOnTheLeafAndNotOnlyOnItsContainers()
    {
        // Retype the attribute on Aircraft alone. Every class above it becomes Modified because
        // something beneath it moved, but the change itself has to land on Aircraft.
        var root = Compare(Rpr("uint:8"), Rpr("float:32"));

        var aircraft = Find(root, "Aircraft");
        Assert.NotNull(aircraft);
        Assert.Equal(DiffKind.Modified, aircraft!.Kind);

        // Only the attribute owns the edit; Aircraft is on the path to it.
        var attribute = Find(root, "AfterburnerOn");
        Assert.NotNull(attribute);
        Assert.True(attribute!.HasOwnChange);

        // The containers are marked so the path stays visible, but none of them owns a change.
        foreach (var name in new[] { "Platform", "PhysicalEntity", "BaseEntity", "ObjectRoot" })
        {
            var container = Find(root, name);
            Assert.NotNull(container);
            Assert.Equal(DiffKind.Modified, container!.Kind);
            Assert.False(container.HasOwnChange, $"{name} should not own the change");
        }
    }

    [Fact]
    public void AClassLevelEditIsOwnedByTheClassItWasMadeOn()
    {
        // Sharing is a property of Platform itself, so at a depth that compares it, Platform owns
        // this change — as opposed to a container merely sitting above something that moved.
        var root = Compare(Rpr("uint:8"), Rpr("uint:8", platformSharing: "Publish"), ComparisonDepth.Full);

        var platform = Find(root, "Platform");
        Assert.NotNull(platform);
        Assert.True(platform!.HasOwnChange);

        var aircraft = Find(root, "Aircraft");
        Assert.NotNull(aircraft);
        Assert.Equal(DiffKind.Unchanged, aircraft!.Kind);
    }

    [Fact]
    public void AtTheDefaultDepthNoClassLevelPropertyCountsAsADifference()
    {
        // Worth pinning because it decides what the Classes tab can possibly report. The default
        // depth compares which elements exist and how they are typed; sharing, semantics and notes
        // are shown in the detail pane but deliberately do not count. So on a pair that differs only
        // in class-level prose, no class owns a change — the honest answer, and the reason the tab's
        // Modified chip can read zero while the tree still shows the path to an attribute edit.
        var root = Compare(Rpr("uint:8"), Rpr("uint:8", platformSharing: "Publish"));

        var platform = Find(root, "Platform");
        Assert.NotNull(platform);
        Assert.False(platform!.HasOwnChange);

        // Raise the depth and the same edit is owned, counted and reported.
        var deep = Compare(Rpr("uint:8"), Rpr("uint:8", platformSharing: "Publish"), ComparisonDepth.Full);
        Assert.True(Find(deep, "Platform")!.HasOwnChange);
    }

    [Fact]
    public void AClassAddedOnOneSideIsOwnedWhateverTheDepth()
    {
        // Added and removed classes are structural, so they report at every depth — which is what
        // keeps the Classes tab useful at the default one.
        var left = Rpr("uint:8");
        var right = Rpr("uint:8");

        var platform = right.ObjectClasses[0].Children[0].Children[0].Children[0];
        platform.Children.Add(new FomObjectClass
        {
            Name = "Spacecraft",
            QualifiedName = "ObjectRoot.BaseEntity.PhysicalEntity.Platform.Spacecraft",
        });

        var root = Compare(left, right);

        var added = Find(root, "Spacecraft");
        Assert.NotNull(added);
        Assert.Equal(DiffKind.Added, added!.Kind);
    }

    [Fact]
    public void AnUnchangedHierarchyIsKeptInTheModelSoAFilterCanRevealIt()
    {
        // KeepUnchanged defaults on, so nothing is pruned away. An unchanged class is hidden by the
        // view's filter, not deleted by the comparer — which is what makes the "Unchanged" chip able
        // to bring it back.
        var root = Compare(Rpr("uint:8"), Rpr("uint:8"));

        Assert.NotNull(Find(root, "Aircraft"));
        Assert.Equal(DiffKind.Unchanged, Find(root, "Aircraft")!.Kind);
    }
}
