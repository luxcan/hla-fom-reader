using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Comparison;

public enum DiffKind
{
    /// <summary>Present in both FOMs with every compared property equal.</summary>
    Unchanged = 0,
    /// <summary>Present in both FOMs but at least one compared property differs.</summary>
    Modified = 1,
    /// <summary>Present only in the right-hand (B) FOM.</summary>
    Added = 2,
    /// <summary>Present only in the left-hand (A) FOM.</summary>
    Removed = 3,
}

/// <summary>Which OMT table a diff node belongs to. Drives grouping and icons in the UI.</summary>
public enum DiffCategory
{
    Root,
    Identification,
    IdentificationField,
    ObjectClass,
    Attribute,
    InteractionClass,
    Parameter,
    DataTypeGroup,
    DataType,
    DataTypeMember,
    Dimension,
    RoutingSpace,
    Transportation,
    Synchronization,
    UpdateRate,
    Switch,
    Tag,
    Note,
    Time,
    Section,
}

/// <summary>A single property compared between the two FOMs.</summary>
public sealed class PropertyDiff
{
    public PropertyDiff() { }

    public PropertyDiff(string property, string? left, string? right, bool isDifferent, string? reason = null)
    {
        Property = property;
        LeftValue = left;
        RightValue = right;
        IsDifferent = isDifferent;
        Reason = reason;
    }

    public string Property { get; set; } = "";
    public string? LeftValue { get; set; }
    public string? RightValue { get; set; }
    public bool IsDifferent { get; set; }

    /// <summary>
    /// Why the values differ when the cause is structural rather than authored — e.g.
    /// "not expressible in HLA 1.3". Purely informational; the difference still counts.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>One node of the merged difference tree.</summary>
public sealed class DiffNode
{
    public string Name { get; set; } = "";

    /// <summary>Stable identity of the node within the comparison, e.g. <c>objects/HLAobjectRoot.Aircraft</c>.</summary>
    public string Path { get; set; } = "";

    public DiffCategory Category { get; set; }
    public DiffKind Kind { get; set; }

    /// <summary>Set when the node exists only on one side and the other side has no counterpart.</summary>
    public string? LeftName { get; set; }
    public string? RightName { get; set; }

    public List<PropertyDiff> Properties { get; } = new();
    public List<DiffNode> Children { get; } = new();

    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int UnchangedCount { get; set; }

    /// <summary>
    /// True when one of this node's own properties differs, as opposed to the node merely
    /// sitting on the path to something that changed. Only nodes with an own change are
    /// counted as modifications, so "24 differences" never means "7 real edits plus 17
    /// ancestors that contain them".
    /// </summary>
    public bool HasOwnChange { get; private set; }

    public int DifferenceCount => AddedCount + RemovedCount + ModifiedCount;
    public bool HasDifferences => DifferenceCount > 0 || Kind != DiffKind.Unchanged;

    public IEnumerable<DiffNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }

    /// <summary>Recomputes rollup counts from this node's own kind and its subtree.</summary>
    public void Recount()
    {
        foreach (var child in Children) child.Recount();

        HasOwnChange = Properties.Any(p => p.IsDifferent);

        AddedCount = Children.Sum(c => c.AddedCount) + Children.Count(c => c.Kind == DiffKind.Added);
        RemovedCount = Children.Sum(c => c.RemovedCount) + Children.Count(c => c.Kind == DiffKind.Removed);
        UnchangedCount = Children.Sum(c => c.UnchangedCount) + Children.Count(c => c.Kind == DiffKind.Unchanged);

        // Only a child that actually changed in itself is a modification. A parent class whose
        // sole "change" is that one of its attributes moved is still shown as Modified in the
        // tree, so the path to the change stays visible, but it is not counted a second time.
        ModifiedCount = Children.Sum(c => c.ModifiedCount)
                      + Children.Count(c => c.Kind == DiffKind.Modified && c.HasOwnChange);

        // A container present on both sides is Modified when anything beneath it moved, or when
        // one of its own properties differs.
        if (Kind is DiffKind.Unchanged or DiffKind.Modified)
        {
            Kind = (HasOwnChange || AddedCount > 0 || RemovedCount > 0 || ModifiedCount > 0
                    || Children.Any(c => c.Kind == DiffKind.Modified))
                ? DiffKind.Modified
                : DiffKind.Unchanged;
        }
    }
}
