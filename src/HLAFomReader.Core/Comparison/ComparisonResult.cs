using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Comparison;

/// <summary>The outcome of comparing two FOM documents.</summary>
public sealed class ComparisonResult
{
    public required FomDocument Left { get; init; }
    public required FomDocument Right { get; init; }
    public required ComparisonOptions Options { get; init; }
    public required DiffNode Root { get; init; }

    public string LeftLabel { get; set; } = "FOM A";
    public string RightLabel { get; set; } = "FOM B";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True when the two documents come from different HLA standards.</summary>
    public bool IsCrossStandard => Left.Standard != Right.Standard;

    public int AddedCount => Root.AddedCount;
    public int RemovedCount => Root.RemovedCount;
    public int ModifiedCount => Root.ModifiedCount;
    public int UnchangedCount => Root.UnchangedCount;
    public int TotalDifferences => AddedCount + RemovedCount + ModifiedCount;
    public bool AreIdentical => TotalDifferences == 0;

    /// <summary>Flat list of every node that differs, in tree order.</summary>
    public IEnumerable<DiffNode> Differences() =>
        Root.DescendantsAndSelf().Where(n => n.Kind != DiffKind.Unchanged && n.Category != DiffCategory.Root);

    /// <summary>Notes about fidelity loss, e.g. comparing a 1.3 FED against a 1516 FOM.</summary>
    public List<string> Advisories { get; } = new();

    /// <summary>
    /// How many compared properties are attributable to one standard being unable to express the
    /// concept, whether or not they are currently being counted as differences. This is the "how
    /// much noise is available to remove" figure, so it deliberately ignores
    /// <see cref="ComparisonOptions.IgnoreInexpressibleProperties"/> and <see cref="ComparisonOptions.Depth"/>.
    /// </summary>
    public int FormatGapPropertyCount =>
        Root.DescendantsAndSelf().SelectMany(n => n.Properties).Count(p => p.Reason is not null);

    /// <summary>
    /// Format gaps that are actually being reported as differences right now.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="FormatGapPropertyCount"/>, and the one to quote alongside a total.
    /// Once a shallower <see cref="ComparisonOptions.Depth"/> or the inexpressible filter stops a row
    /// counting, quoting the raw tagged count against the reported total produces two figures that
    /// visibly disagree.
    /// </remarks>
    public int CountedFormatGapDifferences =>
        Root.DescendantsAndSelf().SelectMany(n => n.Properties)
            .Count(p => p.IsDifferent && p.Reason is not null);

    /// <summary>Property differences that someone actually authored, as opposed to format gaps.</summary>
    public int AuthoredPropertyDifferenceCount =>
        Root.DescendantsAndSelf().SelectMany(n => n.Properties)
            .Count(p => p.IsDifferent && p.Reason is null);
}
