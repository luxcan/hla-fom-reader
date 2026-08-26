using System;
using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Comparison;

/// <summary>How one class lines up between two FOMs.</summary>
/// <remarks>
/// Deliberately the same vocabulary as <see cref="AttributeMapStatus"/>. The two screens answer
/// different questions — which classes exist, and what data they carry — but a reader moving between
/// them should not have to learn a second set of words for the same five outcomes.
/// </remarks>
public enum ClassMapStatus
{
    /// <summary>On both sides with nothing different about the class itself or anything in it.</summary>
    Same = 0,

    /// <summary>
    /// On both sides, spelled differently, and identical otherwise — the usual
    /// <c>ObjectRoot</c> to <c>HLAobjectRoot</c> step. Nothing to do, so it reads as
    /// <see cref="Same"/>.
    /// </summary>
    Renamed = 1,

    /// <summary>On both sides, but something about it or inside it differs.</summary>
    Changed = 2,

    /// <summary>In FOM A only. Nothing in B receives what it publishes.</summary>
    OnlyInLeft = 3,

    /// <summary>In FOM B only. Nothing in A feeds it.</summary>
    OnlyInRight = 4,
}

/// <summary>One class of one FOM pair, as a single flat row.</summary>
public sealed class ClassMapRow
{
    /// <summary>The leaf name — <c>Aircraft</c>, not the whole dotted path.</summary>
    public required string Name { get; init; }

    /// <summary>Fully qualified name in FOM A, or null when the class is not there.</summary>
    public string? LeftName { get; init; }

    /// <summary>Fully qualified name in FOM B, or null when the class is not there.</summary>
    public string? RightName { get; init; }

    public required ClassMapStatus Status { get; init; }

    /// <summary>True for an interaction class, false for an object class.</summary>
    public required bool IsInteraction { get; init; }

    /// <summary>What the members are called on this row: attributes, or parameters.</summary>
    public string MemberLabel => IsInteraction ? "parameter" : "attribute";

    /// <summary>Members declared directly on the class in FOM A.</summary>
    public int LeftMemberCount { get; init; }

    /// <summary>Members declared directly on the class in FOM B.</summary>
    public int RightMemberCount { get; init; }

    /// <summary>Members on both sides whose own properties differ.</summary>
    public int ChangedMemberCount { get; init; }

    /// <summary>Members in FOM B only.</summary>
    public int AddedMemberCount { get; init; }

    /// <summary>Members in FOM A only.</summary>
    public int RemovedMemberCount { get; init; }

    /// <summary>Class-level properties that differ, e.g. <c>Sharing</c>. Empty is the normal case.</summary>
    public IReadOnlyList<string> ChangedProperties { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Why the row is flagged, in words.
    /// </summary>
    /// <remarks>
    /// This column is the whole point of the screen. A coloured badge saying "Modified" answers
    /// nothing on its own — a class is most often flagged because its members changed, which is work
    /// for the attribute map rather than for the class list, and a reader staring at an amber square
    /// has no way to tell that from a class whose own declaration was edited. So the reason is
    /// spelled out and says where to go next.
    /// </remarks>
    public required string Why { get; init; }

    /// <summary>True for a row that needs somebody to do something.</summary>
    /// <remarks>
    /// A rename is excluded on purpose, exactly as it is in the attribute map: the class matched, the
    /// spelling moved, and nothing has to be built.
    /// </remarks>
    public bool NeedsAttention =>
        Status is ClassMapStatus.Changed or ClassMapStatus.OnlyInLeft or ClassMapStatus.OnlyInRight;

    /// <summary>The name to sort and search on — whichever side has one.</summary>
    public string QualifiedName => LeftName ?? RightName ?? Name;
}

/// <summary>
/// A flat, one-row-per-class view of how two FOMs line up structurally.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a tree, for the same reason <see cref="AttributeDataMap"/> is not one. A
/// hierarchy is the right shape for reading a single FOM, and the wrong shape for comparing two:
/// the interesting rows are scattered across five levels, most of the nodes on screen are
/// containers that differ only because something under them does, and a badge on a container
/// cannot say what is actually wrong. Flattening puts every class on one line with its verdict
/// and its reason beside it.
/// </para>
/// <para>
/// Built from a finished <see cref="ComparisonResult"/> rather than from the two documents, so the
/// matching — including root-name normalisation and every option the user set — is the comparer's
/// and cannot drift from what the difference tree reports.
/// </para>
/// </remarks>
public sealed class ClassMap
{
    public required IReadOnlyList<ClassMapRow> Rows { get; init; }

    public int SameCount => Rows.Count(r => r.Status is ClassMapStatus.Same or ClassMapStatus.Renamed);
    public int ChangedCount => Rows.Count(r => r.Status == ClassMapStatus.Changed);
    public int OnlyInLeftCount => Rows.Count(r => r.Status == ClassMapStatus.OnlyInLeft);
    public int OnlyInRightCount => Rows.Count(r => r.Status == ClassMapStatus.OnlyInRight);

    /// <summary>Rows somebody has to act on.</summary>
    public int ActionableCount => Rows.Count(r => r.NeedsAttention);

    public static ClassMap Empty { get; } = new() { Rows = Array.Empty<ClassMapRow>() };

    /// <summary>
    /// Flattens the class sections of a finished comparison into one row per class.
    /// </summary>
    /// <param name="root">The comparison's root node.</param>
    /// <returns>Rows in the order the comparer emitted them — the left FOM's class tree, root first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public static ClassMap Build(DiffNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var rows = new List<ClassMapRow>();

        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.Category is not (DiffCategory.ObjectClass or DiffCategory.InteractionClass))
                continue;

            rows.Add(BuildRow(node));
        }

        return new ClassMap { Rows = rows };
    }

    private static ClassMapRow BuildRow(DiffNode node)
    {
        var isInteraction = node.Category == DiffCategory.InteractionClass;
        var memberCategory = isInteraction ? DiffCategory.Parameter : DiffCategory.Attribute;

        // Direct members only. The node's own rollup counts cover the whole subtree, which for a
        // class means every descendant class's members too — so a root class would claim the entire
        // FOM's changes as its own.
        var members = node.Children.Where(c => c.Category == memberCategory).ToList();

        var changedProperties = node.Properties
            .Where(p => p.IsDifferent)
            .Select(p => p.Property)
            .ToList();

        var added = members.Count(m => m.Kind == DiffKind.Added);
        var removed = members.Count(m => m.Kind == DiffKind.Removed);
        var changed = members.Count(m => m.Kind == DiffKind.Modified);

        var status = Classify(node, changedProperties.Count, added, removed, changed);

        return new ClassMapRow
        {
            Name = node.Name,
            LeftName = node.LeftName,
            RightName = node.RightName,
            Status = status,
            IsInteraction = isInteraction,
            LeftMemberCount = members.Count(m => m.Kind != DiffKind.Added),
            RightMemberCount = members.Count(m => m.Kind != DiffKind.Removed),
            ChangedMemberCount = changed,
            AddedMemberCount = added,
            RemovedMemberCount = removed,
            ChangedProperties = changedProperties,
            Why = Explain(status, isInteraction, changedProperties, added, removed, changed, node),
        };
    }

    private static ClassMapStatus Classify(
        DiffNode node, int changedProperties, int added, int removed, int changed)
    {
        if (node.Kind == DiffKind.Added) return ClassMapStatus.OnlyInRight;
        if (node.Kind == DiffKind.Removed) return ClassMapStatus.OnlyInLeft;

        var somethingDiffers = changedProperties > 0 || added > 0 || removed > 0 || changed > 0
                               || node.Kind == DiffKind.Modified;

        if (!somethingDiffers)
            return IsRenamed(node) ? ClassMapStatus.Renamed : ClassMapStatus.Same;

        // A class flagged only because a DESCENDANT class changed is not itself changed. Without this
        // every ancestor up to the root would read "Changed", which on RPR means the whole spine is
        // amber and the reader cannot see which class was actually edited.
        var ownChange = changedProperties > 0 || added > 0 || removed > 0 || changed > 0;
        if (!ownChange)
            return IsRenamed(node) ? ClassMapStatus.Renamed : ClassMapStatus.Same;

        return ClassMapStatus.Changed;
    }

    /// <summary>True when the two sides spell the class differently but match otherwise.</summary>
    private static bool IsRenamed(DiffNode node) =>
        node.LeftName is { Length: > 0 } left
        && node.RightName is { Length: > 0 } right
        && !string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// Says, in one line, why the row carries the badge it carries — and where to go next when the
    /// answer is not on this screen.
    /// </summary>
    private static string Explain(
        ClassMapStatus status,
        bool isInteraction,
        IReadOnlyList<string> changedProperties,
        int added,
        int removed,
        int changed,
        DiffNode node)
    {
        var member = isInteraction ? "parameter" : "attribute";
        var elsewhere = isInteraction ? "" : " — see Attribute data";

        switch (status)
        {
            case ClassMapStatus.OnlyInLeft:
                return "Nothing in FOM B receives this class.";

            case ClassMapStatus.OnlyInRight:
                return "Nothing in FOM A feeds this class.";

            case ClassMapStatus.Renamed:
                return "Same class under a different name; nothing to convert.";

            case ClassMapStatus.Same:
                // A container whose descendants changed is worth saying so, or the row reads as
                // "nothing here" while the tab's counts say otherwise.
                return node.Kind == DiffKind.Modified
                    ? "Unchanged in itself; a class beneath it differs."
                    : "";

            default:
                var parts = new List<string>(4);

                if (changed > 0) parts.Add($"{changed} {member}{Plural(changed)} differ{(changed == 1 ? "s" : "")}{elsewhere}");
                if (removed > 0) parts.Add($"{removed} {member}{Plural(removed)} only in FOM A");
                if (added > 0) parts.Add($"{added} {member}{Plural(added)} only in FOM B");

                // Named rather than counted: there are only a handful of class-level properties, and
                // "Sharing differs" is actionable in a way that "1 property differs" is not.
                if (changedProperties.Count > 0)
                    parts.Add($"{string.Join(", ", changedProperties)} differ{(changedProperties.Count == 1 ? "s" : "")}");

                return parts.Count == 0 ? "Something in this class differs." : Capitalise(string.Join("; ", parts)) + ".";
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
