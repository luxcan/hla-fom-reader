using System;
using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Comparison;

/// <summary>How one attribute lines up between two FOMs, from a data point of view.</summary>
public enum AttributeMapStatus
{
    /// <summary>Present on both sides, same datatype. Nothing to remap.</summary>
    Same = 0,

    /// <summary>
    /// Present on both sides and the <b>encoding genuinely differs</b> — the two datatype names
    /// resolve, through each FOM's own datatype tables, to different structural forms, so values
    /// have to be converted. A mere change of datatype name is <see cref="Renamed"/>, not this.
    /// </summary>
    DataTypeChanged = 1,

    /// <summary>
    /// Present on both sides with different datatype <em>names</em> that resolve to the <b>same</b>
    /// encoding. Nothing to convert: the bytes on the wire are identical and the mapping is
    /// one-to-one, so this is informational rather than work.
    /// </summary>
    /// <remarks>
    /// This is the bulk of a real generational migration. RPR 1.0 to RPR 2.0 renames
    /// <c>octet</c> to <c>Octet</c>, <c>unsigned long</c> to <c>UnsignedInteger32</c> and
    /// <c>float</c> to <c>AngleRadianFloat32</c> — 614 rows of the user's 1690, none of which move
    /// a single bit. Separating them out is what lets the handful that really re-encode be seen.
    /// </remarks>
    Renamed = 5,

    /// <summary>
    /// Present on both sides with the same datatype, but declared on a different class in the
    /// hierarchy. Nothing to convert: inheritance means the attribute is still available on this
    /// class, so this is informational rather than work.
    /// </summary>
    Moved = 2,

    /// <summary>In FOM A only. Data for it has nowhere to go.</summary>
    OnlyInLeft = 3,

    /// <summary>In FOM B only. Nothing in A feeds it.</summary>
    OnlyInRight = 4,
}

/// <summary>One attribute of one object class, as it exists on each side.</summary>
public sealed class AttributeMapRow
{
    public required string ClassName { get; init; }
    public required string AttributeName { get; init; }

    /// <summary>The class that declares it in FOM A — may be an ancestor, since attributes are inherited.</summary>
    public string? LeftDeclaredIn { get; init; }
    public string? LeftDataType { get; init; }

    public string? RightDeclaredIn { get; init; }
    public string? RightDataType { get; init; }

    /// <summary>
    /// What <see cref="LeftDataType"/> actually encodes as, in canonical form — <c>uint:32</c>,
    /// <c>record(float:64,float:64,float:64)</c>, and so on. An unresolvable name is written
    /// <c>?(Name)</c>, so the column always says something rather than going blank on the reader.
    /// Null only when the A side carries no datatype for this attribute at all.
    /// </summary>
    public string? LeftEncoding { get; init; }

    /// <summary>What <see cref="RightDataType"/> encodes as; see <see cref="LeftEncoding"/>.</summary>
    public string? RightEncoding { get; init; }

    public AttributeMapStatus Status { get; init; }

    /// <summary>Why, when the reason is structural — e.g. a FED carrying no datatypes at all.</summary>
    public string? Note { get; init; }

    public bool IsDifferent => Status != AttributeMapStatus.Same;

    /// <summary>
    /// True when both sides resolved to an encoding and those encodings differ — the row moves
    /// different bytes, whatever the two names happen to be.
    /// </summary>
    /// <remarks>
    /// A side that could not be resolved is written <c>?(Name)</c> by
    /// <see cref="DataTypeSignature.Unresolved"/>, and an unresolved name is evidence of nothing:
    /// it neither proves the encoding changed nor proves it held. Such a row answers false here and
    /// says why in its <see cref="Note"/> instead of asserting a difference it cannot demonstrate.
    /// </remarks>
    public bool EncodingDiffers =>
        IsResolvedEncoding(LeftEncoding)
        && IsResolvedEncoding(RightEncoding)
        && !string.Equals(LeftEncoding, RightEncoding, StringComparison.Ordinal);

    /// <summary>
    /// True when this row is real remapping work: the value survives but its encoding changed, so
    /// somebody has to write a conversion. A rename is deliberately excluded — it costs nothing.
    /// </summary>
    public bool NeedsConversion => Status == AttributeMapStatus.DataTypeChanged;

    /// <summary>True for a canonical form that names an encoding rather than an unresolved name.</summary>
    private static bool IsResolvedEncoding(string? canonical) =>
        !string.IsNullOrEmpty(canonical) && canonical[0] != '?';

    /// <summary>Fully qualified name of the attribute on the class, for reading and for export.</summary>
    public string QualifiedName => $"{ClassName}.{AttributeName}";
}

/// <summary>
/// A flat, class-by-attribute view of what the two FOMs carry on the wire.
/// </summary>
/// <remarks>
/// Deliberately not a tree. When the question is "what data moves, and how do I remap it?", the useful
/// shape is one row per attribute a federate could publish or reflect, with the datatype on each side
/// beside it. Attributes are resolved to their <b>effective</b> set — everything inherited from
/// ancestors included — because that is what a federate publishing the class actually deals with,
/// regardless of which ancestor happens to declare it.
/// </remarks>
public sealed class AttributeDataMap
{
    public required IReadOnlyList<AttributeMapRow> Rows { get; init; }

    public string LeftLabel { get; init; } = "FOM A";
    public string RightLabel { get; init; } = "FOM B";

    /// <summary>Notes about fidelity, e.g. one side being a FED with no datatype table.</summary>
    public List<string> Advisories { get; } = new();

    public int SameCount => Rows.Count(r => r.Status == AttributeMapStatus.Same);

    /// <summary>Rows whose encoding genuinely differs. Renames are counted separately.</summary>
    public int DataTypeChangedCount => Rows.Count(r => r.Status == AttributeMapStatus.DataTypeChanged);

    /// <summary>Rows whose datatype name changed while the encoding stayed identical.</summary>
    public int RenamedCount => Rows.Count(r => r.Status == AttributeMapStatus.Renamed);

    public int MovedCount => Rows.Count(r => r.Status == AttributeMapStatus.Moved);
    public int OnlyInLeftCount => Rows.Count(r => r.Status == AttributeMapStatus.OnlyInLeft);
    public int OnlyInRightCount => Rows.Count(r => r.Status == AttributeMapStatus.OnlyInRight);

    /// <summary>
    /// Rows that need a decision when remapping: a real re-encoding, or an attribute one side has
    /// and the other does not.
    /// </summary>
    /// <remarks>
    /// Renames are excluded on purpose, as are moves. Neither costs the reader anything, and
    /// counting them here would restore exactly the noise the encoding resolution exists to remove.
    /// </remarks>
    public int ActionableCount => DataTypeChangedCount + OnlyInLeftCount + OnlyInRightCount;

    public static AttributeDataMap Empty() => new() { Rows = new List<AttributeMapRow>() };
}
