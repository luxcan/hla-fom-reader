using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace HLAFomReader.Core.Comparison;

/// <summary>Which of the six OMT datatype tables a name was found in.</summary>
public enum DataTypeTable
{
    /// <summary>In no table and no lexicon — the name resolves to nothing.</summary>
    None = 0,
    Basic,
    Simple,
    Enumerated,
    Array,
    FixedRecord,
    VariantRecord,

    /// <summary>Not tabulated anywhere: an HLA 1.3 C/IDL primitive read from the built-in lexicon.</summary>
    Primitive,
}

/// <summary>What role a member plays inside its parent datatype.</summary>
public enum DataTypeMemberRole
{
    /// <summary>A field of a fixed record.</summary>
    Field,

    /// <summary>The element type of an array.</summary>
    Element,

    /// <summary>The representation a simple or enumerated type is carried in.</summary>
    Representation,

    /// <summary>The discriminant of a variant record.</summary>
    Discriminant,

    /// <summary>One alternative of a variant record.</summary>
    Alternative,

    /// <summary>One declared value of an enumeration.</summary>
    Enumerator,
}

/// <summary>The wording for a member's role wherever a person sees it.</summary>
/// <remarks>
/// One statement of the vocabulary, because two things now show it: the datatype inspector's badge
/// beside each node, and the Kind column of the exported side-by-side worksheet. A sheet that named
/// a record field something other than the inspector does would be read as describing a different
/// thing.
/// </remarks>
public static class DataTypeMemberRoleText
{
    /// <summary>The label for one role.</summary>
    public static string Label(DataTypeMemberRole role) => role switch
    {
        DataTypeMemberRole.Field => "Field",
        DataTypeMemberRole.Element => "Element",
        DataTypeMemberRole.Representation => "Represented as",
        DataTypeMemberRole.Discriminant => "Discriminant",
        DataTypeMemberRole.Alternative => "Alternative",
        DataTypeMemberRole.Enumerator => "Enumerator",
        _ => "",
    };
}

/// <summary>
/// The span of values a datatype can carry, and what establishes it.
/// </summary>
/// <remarks>
/// The OMT does not tabulate bounds for a simple datatype — <c>&lt;simpleData&gt;</c> carries a
/// representation, units, resolution and accuracy, and nothing else — so a min and max cannot simply
/// be read off the file. They are derived instead, from the two places the FOM does pin a value
/// down: the width and interpretation of the basic representation the type bottoms out on, and, for
/// an enumeration, the set of enumerators actually declared. <see cref="Basis"/> says which, so a
/// derived bound is never mistaken for an authored one.
/// </remarks>
public sealed class ValueRange
{
    public required string Minimum { get; init; }
    public required string Maximum { get; init; }

    /// <summary>What the bounds were read from, e.g. "32-bit signed integer" or "12 declared enumerators".</summary>
    public required string Basis { get; init; }

    /// <summary>Extra qualification, e.g. the precision of a float or the step of a resolution.</summary>
    public string? Note { get; init; }

    public override string ToString() => $"{Minimum} … {Maximum}";

    /// <summary>
    /// Derives the representable range from a canonical scalar form — <c>uint:8</c>, <c>int:32</c>,
    /// <c>float:64</c>, <c>char:16</c>. Returns null for a composite or width-less form, which has
    /// no single span to state.
    /// </summary>
    /// <remarks>
    /// Reads the canonical rather than the FOM's own interpretation column on purpose: the canonical
    /// is what <see cref="DataTypeResolver"/> already decided the type is, so a range shown here can
    /// never contradict the encoding shown beside it.
    /// </remarks>
    public static ValueRange? FromCanonical(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return null;

        var colon = canonical.IndexOf(':');
        if (colon <= 0) return null;

        var kind = canonical[..colon];
        var width = canonical[(colon + 1)..];

        // "uint:?(Foo)" and "string:n" state no width, so they bound nothing.
        if (!int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits <= 0)
            return null;

        return kind switch
        {
            "uint" => Unsigned(bits),
            "int" => Signed(bits),
            "float" => Float(bits),
            "char" => Character(bits),
            "bool" => new ValueRange { Minimum = "0", Maximum = "1", Basis = $"{bits}-bit boolean" },
            _ => null,
        };
    }

    private static ValueRange Unsigned(int bits) => new()
    {
        Minimum = "0",
        Maximum = Format(BigInteger.Pow(2, bits) - 1),
        Basis = $"{bits}-bit unsigned integer",
    };

    private static ValueRange Signed(int bits) => new()
    {
        Minimum = Format(-BigInteger.Pow(2, bits - 1)),
        Maximum = Format(BigInteger.Pow(2, bits - 1) - 1),
        Basis = $"{bits}-bit signed integer",
    };

    /// <summary>
    /// IEEE 754 magnitudes, quoted only for the two widths the standard actually defines. A basic
    /// type calling itself a float at some other width is left unbounded rather than guessed at.
    /// </summary>
    private static ValueRange? Float(int bits) => bits switch
    {
        32 => new ValueRange
        {
            Minimum = "-3.402823e+38",
            Maximum = "+3.402823e+38",
            Basis = "IEEE 754 single precision",
            Note = "about 7 significant decimal digits; smallest normal magnitude 1.175494e-38",
        },
        64 => new ValueRange
        {
            Minimum = "-1.797693e+308",
            Maximum = "+1.797693e+308",
            Basis = "IEEE 754 double precision",
            Note = "about 15 significant decimal digits; smallest normal magnitude 2.225074e-308",
        },
        _ => null,
    };

    private static ValueRange Character(int bits) => new()
    {
        Minimum = "0",
        Maximum = Format(BigInteger.Pow(2, bits) - 1),
        Basis = $"{bits}-bit character code unit",
    };

    /// <summary>
    /// Derives the range from what an enumeration actually declares. This is the tighter and more
    /// useful answer: the representation says a value could be any of 4,294,967,296 things, while
    /// the enumerator list says which handful are legal.
    /// </summary>
    /// <param name="values">The literal values as written, in declaration order.</param>
    public static ValueRange? FromEnumerators(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0) return null;

        var numbers = new List<BigInteger>(values.Count);
        foreach (var value in values)
        {
            // An enumerator may list several literals for one label; each is a legal value.
            foreach (var literal in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (BigInteger.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                    numbers.Add(parsed);
        }

        var count = values.Count;
        var label = $"{count} declared enumerator{(count == 1 ? "" : "s")}";

        // Non-numeric enumerators are legal in a malformed or unusual FOM. Say how many values there
        // are rather than inventing bounds from the ones that happened to parse.
        if (numbers.Count == 0)
            return new ValueRange { Minimum = values[0], Maximum = values[^1], Basis = label, Note = "values are not numeric; first and last shown in declaration order" };

        return new ValueRange
        {
            Minimum = Format(numbers.Min()),
            Maximum = Format(numbers.Max()),
            Basis = label,
            Note = numbers.Count == count ? null : $"{count - numbers.Count} enumerator(s) had non-numeric values and are excluded from the bounds",
        };
    }

    /// <summary>Describes how many elements an array may hold, from its cardinality column.</summary>
    /// <param name="cardinality">The cardinality exactly as the FOM writes it.</param>
    public static ValueRange? FromCardinality(string? cardinality)
    {
        if (string.IsNullOrWhiteSpace(cardinality)) return null;

        var text = cardinality.Trim();

        if (text.Equals("Dynamic", StringComparison.OrdinalIgnoreCase) || text == "-" ||
            text.Equals("n", StringComparison.OrdinalIgnoreCase))
            return new ValueRange { Minimum = "0", Maximum = "unbounded", Basis = "variable-length array" };

        // "2..5" and "2-5" both appear in real FOMs for a bounded range.
        var separator = text.Contains("..", StringComparison.Ordinal) ? ".." : "-";
        var parts = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 2)
            return new ValueRange { Minimum = parts[0], Maximum = parts[1], Basis = "array cardinality" };

        return new ValueRange { Minimum = text, Maximum = text, Basis = "fixed-length array" };
    }

    /// <summary>Groups the digits so a 64-bit bound can be read at a glance rather than counted.</summary>
    private static string Format(BigInteger value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One member of a composite datatype, with the role it plays in its parent.</summary>
public sealed class DataTypeDetailMember
{
    /// <summary>The field, enumerator or alternative name as the FOM writes it; blank when unnamed.</summary>
    public required string Name { get; init; }

    public required DataTypeMemberRole Role { get; init; }

    /// <summary>The enumerator's literal value, or the alternative's selecting enumerator.</summary>
    public string? Value { get; init; }

    /// <summary>The member's own datatype, resolved. Null for an enumerator, which carries no type.</summary>
    public DataTypeDetail? Type { get; init; }

    /// <summary>OMT semantics prose on the member itself.</summary>
    public string? Semantics { get; init; }
}

/// <summary>
/// Everything a FOM says about one datatype, kept for reading rather than for comparing.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart to <see cref="DataTypeSignature"/>, and it exists because that type
/// deliberately throws away almost all of this. A signature answers "do these two encode the same
/// bytes?", so it drops units, resolution, accuracy, field names, enumerator labels and semantics —
/// every one of which is a property of what the value <em>means</em> rather than of how it is laid
/// out, and every one of which would produce a false difference if compared.
/// </para>
/// <para>
/// A reader asking "what can this attribute actually hold?" needs exactly the discarded half back.
/// So this walks the same tables, through the same precedence, and keeps everything — including a
/// derived <see cref="Range"/>, since the OMT tabulates no bounds of its own for a simple type.
/// </para>
/// </remarks>
public sealed class DataTypeDetail
{
    /// <summary>The datatype name as written against the attribute, or as declared.</summary>
    public required string Name { get; init; }

    /// <summary>Which table the name was found in.</summary>
    public required DataTypeTable Table { get; init; }

    /// <summary>The structural family, from the resolver.</summary>
    public required DataTypeShape Shape { get; init; }

    /// <summary>The canonical encoding, identical to what the Encoding column shows.</summary>
    public required string Canonical { get; init; }

    /// <summary>
    /// True when the definition came from the built-in HLA standard MIM rather than from the FOM
    /// file, which is the normal case for the <c>HLA…</c> types a 1516 module leaves to the RTI.
    /// </summary>
    public bool IsFromStandardMim { get; init; }

    public int? Bits { get; init; }
    public string? Endian { get; init; }

    /// <summary>The name this type is declared over, for a simple or enumerated type.</summary>
    public string? Representation { get; init; }

    /// <summary>The basic type's interpretation column — the prose that says what the bits mean.</summary>
    public string? Interpretation { get; init; }

    public string? Units { get; init; }
    public string? Resolution { get; init; }
    public string? Accuracy { get; init; }
    public string? Encoding { get; init; }
    public string? Cardinality { get; init; }
    public string? Semantics { get; init; }

    /// <summary>What the type can carry, derived. Null when no single span applies.</summary>
    public ValueRange? Range { get; init; }

    /// <summary>Fields, elements, enumerators or alternatives, in declaration order.</summary>
    public IReadOnlyList<DataTypeDetailMember> Members { get; init; } = Array.Empty<DataTypeDetailMember>();

    /// <summary>
    /// Set when the walk stopped early — a datatype graph that loops, or one nested deeper than the
    /// inspector unfolds. Says so rather than silently showing a partial structure as a whole one.
    /// </summary>
    public string? Truncation { get; init; }

    public bool IsResolved => Shape != DataTypeShape.Unknown;
    public bool HasMembers => Members.Count > 0;

    /// <summary>How the table is worded for a reader.</summary>
    public string TableLabel => Table switch
    {
        DataTypeTable.Basic => "Basic data representation",
        DataTypeTable.Simple => "Simple datatype",
        DataTypeTable.Enumerated => "Enumerated datatype",
        DataTypeTable.Array => "Array datatype",
        DataTypeTable.FixedRecord => "Fixed record datatype",
        DataTypeTable.VariantRecord => "Variant record datatype",
        DataTypeTable.Primitive => "HLA 1.3 primitive",
        _ => "Not declared",
    };

    /// <summary>Where the definition came from, for the line under the title.</summary>
    public string SourceLabel => Table switch
    {
        DataTypeTable.None => "Declared in no datatype table and matching no known primitive",
        DataTypeTable.Primitive => "Not tabulated by HLA 1.3; read from the built-in C/IDL lexicon",
        _ => IsFromStandardMim
            ? "Declared by the HLA standard MIM, which the RTI merges in — not by this FOM file"
            : "Declared by this FOM",
    };

    public override string ToString() => $"{Name} ({Canonical})";
}
