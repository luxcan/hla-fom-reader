using System;
using System.Collections.Generic;

namespace HLAFomReader.Core.Comparison;

/// <summary>The structural family a datatype resolves to.</summary>
public enum DataTypeShape
{
    /// <summary>Could not be resolved — the name is not in any datatype table and is not a known primitive.</summary>
    Unknown = 0,

    /// <summary>A scalar: integer, unsigned, float, octet, char, boolean.</summary>
    Scalar,

    /// <summary>An enumeration over a scalar representation.</summary>
    Enumerated,

    /// <summary>A repeated element, fixed or variable length.</summary>
    Array,

    /// <summary>An ordered set of named fields.</summary>
    Record,

    /// <summary>A discriminated union.</summary>
    Variant,
}

/// <summary>
/// What a datatype actually is once its name is resolved through the FOM's datatype tables.
/// </summary>
/// <remarks>
/// This exists because comparing datatype <em>names</em> across FOM versions is misleading. Migrating
/// RPR 1.0 to RPR 2.0 renames almost every type: <c>octet</c> becomes <c>Octet</c>,
/// <c>unsigned long</c> becomes <c>UnsignedInteger32</c>, <c>float</c> becomes
/// <c>AngleRadianFloat32</c>. Those are the same bits on the wire and need no conversion, yet a
/// name-only comparison reports all of them as changes — 614 of them on the user's real pair,
/// drowning the handful that genuinely re-encode, such as
/// <c>GridAxisStruct</c> becoming <c>GridAxisStructLengthlessArray</c>.
/// <para>
/// Resolving to a canonical structural form separates "renamed" from "re-encoded", which is the
/// difference between no work and real work.
/// </para>
/// </remarks>
public sealed class DataTypeSignature : IEquatable<DataTypeSignature>
{
    public required DataTypeShape Shape { get; init; }

    /// <summary>
    /// A stable, human-readable canonical form, e.g. <c>uint:32</c>, <c>float:32</c>,
    /// <c>record(float:32,float:32,float:32)</c>, <c>array(uint:8,31)</c>. Two datatypes with the
    /// same canonical form encode identically.
    /// </summary>
    public required string Canonical { get; init; }

    /// <summary>Width in bits for a scalar, or the resolved representation of an enumeration.</summary>
    public int? Bits { get; init; }

    /// <summary>Byte order when the FOM states one. Null means unstated, which matches anything.</summary>
    public string? Endian { get; init; }

    /// <summary>The name as written in the FOM, before resolution.</summary>
    public required string SourceName { get; init; }

    /// <summary>Element, field or alternative signatures, for the composite shapes.</summary>
    public IReadOnlyList<DataTypeSignature> Parts { get; init; } = Array.Empty<DataTypeSignature>();

    public bool IsResolved => Shape != DataTypeShape.Unknown;

    /// <summary>
    /// True when the two encode identically. Endianness compares leniently when either side does not
    /// state it — HLA 1.3 primitives carry no endian, so demanding a match would report a difference
    /// nobody can act on.
    /// </summary>
    public bool EncodesTheSameAs(DataTypeSignature? other)
    {
        if (other is null) return false;
        if (!IsResolved || !other.IsResolved) return false;

        return string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);
    }

    public bool Equals(DataTypeSignature? other) =>
        other is not null
        && Shape == other.Shape
        && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as DataTypeSignature);

    public override int GetHashCode() => HashCode.Combine((int)Shape, Canonical);

    public override string ToString() => Canonical;

    /// <summary>A signature for a name that could not be resolved to anything.</summary>
    public static DataTypeSignature Unresolved(string? name) => new()
    {
        Shape = DataTypeShape.Unknown,
        Canonical = string.IsNullOrWhiteSpace(name) ? "?" : $"?({name.Trim()})",
        SourceName = name?.Trim() ?? "",
    };
}
