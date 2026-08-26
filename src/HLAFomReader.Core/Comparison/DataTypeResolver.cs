using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// Resolves a datatype <em>name</em>, as written against an attribute or parameter, down to the
/// <see cref="DataTypeSignature"/> that says how it is actually encoded.
/// </summary>
/// <remarks>
/// <para>
/// The name on its own is nearly worthless when comparing two FOM generations. Migrating RPR 1.0 to
/// RPR 2.0 renames almost everything — <c>octet</c> to <c>Octet</c>, <c>unsigned long</c> to
/// <c>UnsignedInteger32</c>, <c>float</c> to <c>AngleRadianFloat32</c> — while the bytes on the wire
/// stay exactly as they were. Comparing names reports 614 changes on the user's real pair and buries
/// the handful, such as <c>GridAxisStruct</c> becoming <c>GridAxisStructLengthlessArray</c>, that
/// genuinely need a data conversion. Resolving both sides to a canonical structural form separates
/// "renamed" from "re-encoded", which is the difference between no work and real work.
/// </para>
/// <para>
/// Resolution walks the document's own six datatype tables. A name is looked for in each in turn —
/// basic, simple, enumerated, array, fixed record, variant record — and the <b>first</b> table that
/// holds it wins. A well-formed OMT declares each name once, so the order only matters for a
/// malformed file, where some answer beats an ambiguity error.
/// </para>
/// <para>
/// <b>A name in none of those tables may still be a standard one.</b> An IEEE 1516 FOM module does
/// not declare <c>HLAoctet</c>, <c>HLAinteger32BE</c>, <c>HLAASCIIstring</c> or any of their
/// siblings, because they are declared by the HLA standard MIM, which every RTI merges into the
/// federation's object model automatically and which is therefore not part of any FOM file. A tool
/// that reads the FOM file on its own sees only the half of the object model the author wrote, so it
/// has to know the other half itself. Without it nearly every 1516 datatype bottoms out on a name in
/// no table: RPR 2.0's <c>Octet</c> is a simple type over <c>HLAoctet</c>, and resolving it against
/// the file alone answers "unknown" — which would defeat the entire point of telling a rename apart
/// from a re-encoding.
/// </para>
/// <para>
/// So the standard MIM's basic, simple and array datatypes are held here as a built-in table,
/// consulted <b>after</b> all six of the document's own and <b>before</b> the primitive lexicon. The
/// document always wins: a FOM that redeclares a standard type — several real ones do, sometimes
/// with a different interpretation column — keeps its own definition, exactly as an RTI would honour
/// it. The built-in entries resolve through the same code paths as declared ones, so a FOM that
/// spells <c>HLAASCIIstring</c> out and one that relies on the MIM produce the same canonical form
/// rather than two spellings of it.
/// </para>
/// <para>
/// A name in no table at all is not a failure: an HLA 1.3 OMT types its attributes with C/IDL
/// primitives (<c>unsigned long</c>, <c>double</c>, <c>octet</c>) that the 1.3 standard never
/// tabulates. Those fall through to a fixed primitive lexicon, which is the whole reason a 1.3
/// document can be compared against a 1516 one on encoding rather than on spelling.
/// </para>
/// <para>
/// <b>Endianness is deliberately excluded from the canonical form.</b> It is captured on the
/// signature's <see cref="DataTypeSignature.Endian"/> so a caller that cares can compare it, but it
/// takes no part in <see cref="DataTypeSignature.Canonical"/>. HLA 1.3 primitives state no byte
/// order whatsoever, so folding endianness into the canonical form would mark every single attribute
/// of a 1.3-to-1516 comparison as re-encoded — precisely the noise this feature exists to remove.
/// Semantics are excluded for the same reason: a simple type's units, resolution and accuracy
/// describe what the number means, not how it is laid out, so <c>float</c> and
/// <c>AngleRadianFloat32</c> resolve alike and come out as the rename they are.
/// </para>
/// <para>
/// Nothing here throws on content. An unknown, absent, circular or malformed type yields
/// <see cref="DataTypeSignature.Unresolved"/>, never a guess and never an exception.
/// </para>
/// <para>
/// One instance per document, reused across that document's attributes: the lookups are built once
/// in the constructor and every answer is memoised, so <see cref="Resolve"/> costs a dictionary hit
/// plus, on a first sighting, the depth of the type. The memo makes an instance stateful and
/// therefore <b>not thread-safe</b>; give each thread its own resolver.
/// </para>
/// <para>
/// <b>The memo stays per-instance and the built-in table stays declarations-only.</b> The standard
/// table holds the MIM's <em>declarations</em>, never any resolved signature: <c>HLAcount</c> is a
/// simple type over <c>HLAinteger32BE</c>, and a document is free to redeclare
/// <c>HLAinteger32BE</c>, so what <c>HLAcount</c> resolves to is a property of the document and not
/// of the MIM. Caching a resolved standard signature statically would let the first document to ask
/// hand its answer to every later one. Declarations are immutable and shared; answers are memoised
/// only on the resolver that produced them.
/// </para>
/// </remarks>
public sealed class DataTypeResolver
{
    // Every table is keyed ordinal-ignore-case: FOM authors are inconsistent about capitalisation,
    // and a case mismatch resolving to "unknown" would read as a datatype change that is not one.
    private readonly Dictionary<string, BasicDataType> _basic = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimpleDataType> _simple = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnumeratedDataType> _enumerated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArrayDataType> _arrays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FixedRecordDataType> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VariantRecordDataType> _variants = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DataTypeSignature> _memo = new(StringComparer.OrdinalIgnoreCase);

    // The names currently being resolved on this path, so a record typed as itself terminates.
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);

    // Counts cycle detections. A signature computed while a cycle was cut short depends on which
    // member of the loop was entered first, so it must not be memoised as if it were the one answer.
    private int _cycleHits;

    /// <summary>Creates a resolver over one document's datatype tables.</summary>
    /// <param name="document">The document whose tables define the names being resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public DataTypeResolver(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var tables = document.DataTypes;

        // TryAdd, not the indexer: a malformed FOM can declare a name twice in one table, and the
        // first declaration is the one a reader of the file would take.
        foreach (var type in tables.BasicDataRepresentations) TryIndex(_basic, type.Name, type);
        foreach (var type in tables.SimpleDataTypes) TryIndex(_simple, type.Name, type);
        foreach (var type in tables.EnumeratedDataTypes) TryIndex(_enumerated, type.Name, type);
        foreach (var type in tables.ArrayDataTypes) TryIndex(_arrays, type.Name, type);
        foreach (var type in tables.FixedRecordDataTypes) TryIndex(_records, type.Name, type);
        foreach (var type in tables.VariantRecordDataTypes) TryIndex(_variants, type.Name, type);
    }

    /// <summary>
    /// Resolves a datatype name to the form it encodes as.
    /// </summary>
    /// <param name="dataTypeName">
    /// The name exactly as the FOM writes it. Null, blank, and the "not applicable" spellings a 1.3
    /// OMT uses for an untyped attribute all resolve to unresolved.
    /// </param>
    /// <returns>
    /// The signature, or <see cref="DataTypeSignature.Unresolved"/> when the name is in no table and
    /// is no known primitive. Never null and never throws.
    /// </returns>
    public DataTypeSignature Resolve(string? dataTypeName)
    {
        var name = dataTypeName?.Trim();
        if (string.IsNullOrEmpty(name))
            return DataTypeSignature.Unresolved(dataTypeName);

        if (_memo.TryGetValue(name, out var cached))
            return cached;

        if (!_resolving.Add(name))
        {
            // Already on the path above us: the datatype graph loops. Report it instead of recursing
            // until the stack gives out — a malformed FOM must not be able to kill the comparison.
            _cycleHits++;
            return new DataTypeSignature
            {
                Shape = DataTypeShape.Unknown,
                Canonical = $"?(cycle:{name})",
                SourceName = name,
            };
        }

        var cyclesBefore = _cycleHits;
        try
        {
            var signature = ResolveUncached(name);
            if (_cycleHits == cyclesBefore)
                _memo[name] = signature;
            return signature;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    /// <summary>
    /// Walks the document's own tables in precedence order, then the standard MIM's, then the
    /// primitive lexicon.
    /// </summary>
    /// <remarks>
    /// The order is the whole contract. The document's declarations come first so a FOM that
    /// redeclares a standard type keeps its own; the MIM comes next because a 1516 FOM leaves those
    /// names to the RTI rather than writing them out; and the 1.3 C/IDL lexicon stays last, since it
    /// is a guess about a document that tabulates nothing and must never pre-empt a real declaration.
    /// </remarks>
    private DataTypeSignature ResolveUncached(string name)
    {
        if (_basic.TryGetValue(name, out var basicType)) return ResolveBasic(name, basicType);
        if (_simple.TryGetValue(name, out var simpleType)) return ResolveSimple(name, simpleType);
        if (_enumerated.TryGetValue(name, out var enumeratedType)) return ResolveEnumerated(name, enumeratedType);
        if (_arrays.TryGetValue(name, out var arrayType)) return ResolveArray(name, arrayType);
        if (_records.TryGetValue(name, out var recordType)) return ResolveRecord(name, recordType);
        if (_variants.TryGetValue(name, out var variantType)) return ResolveVariant(name, variantType);

        // Not the document's, so it may be the RTI's. These go through the same three resolvers the
        // document's own declarations do, which is what keeps a redeclaring FOM and a relying one
        // canonically identical instead of merely equivalent.
        if (StandardMim.Basic.TryGetValue(name, out var standardBasic)) return ResolveBasic(name, standardBasic);
        if (StandardMim.Simple.TryGetValue(name, out var standardSimple)) return ResolveSimple(name, standardSimple);
        if (StandardMim.Arrays.TryGetValue(name, out var standardArray)) return ResolveArray(name, standardArray);

        return ResolvePrimitive(name);
    }

    /// <summary>
    /// True when <paramref name="name"/> resolves through the built-in HLA standard MIM table rather
    /// than through the document's own — that is, when the document never declared it and the answer
    /// came from what the RTI would have merged in.
    /// </summary>
    /// <remarks>
    /// A document that redeclares a standard name answers false: the name is standard in spelling,
    /// but the definition in force is the document's, and a caller showing the reader where a type
    /// came from would otherwise point at the wrong file.
    /// </remarks>
    /// <param name="name">The datatype name as the FOM writes it. Null or blank answers false.</param>
    public bool IsStandardDatatype(string? name)
    {
        var key = name?.Trim();
        if (string.IsNullOrEmpty(key))
            return false;

        if (_basic.ContainsKey(key) || _simple.ContainsKey(key) || _enumerated.ContainsKey(key)
            || _arrays.ContainsKey(key) || _records.ContainsKey(key) || _variants.ContainsKey(key))
            return false;

        return StandardMim.Basic.ContainsKey(key)
               || StandardMim.Simple.ContainsKey(key)
               || StandardMim.Arrays.ContainsKey(key);
    }

    // ------------------------------------------------------------------ explaining

    /// <summary>
    /// How deep <see cref="Explain"/> unfolds a nested datatype before saying that it stopped.
    /// </summary>
    /// <remarks>
    /// Real FOMs nest four or five deep — an array of records holding a variant over records — and a
    /// reader can follow that. Past it the inspector would be printing a wall nobody reads, and a
    /// pathological file could make it print forever, so the walk stops and says so.
    /// </remarks>
    private const int MaxExplainDepth = 8;

    /// <summary>
    /// Reads back everything the FOM says about a datatype, as opposed to <see cref="Resolve"/>,
    /// which reduces it to the bytes it moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both walk the same six tables in the same precedence and bottom out on the same MIM and
    /// lexicon, so an explanation can never disagree with the encoding shown beside it: the
    /// <see cref="DataTypeDetail.Canonical"/> on every node is <see cref="Resolve"/>'s own answer,
    /// not a second derivation of it.
    /// </para>
    /// <para>
    /// Where they differ is what survives. A signature drops units, resolution, accuracy, field
    /// names, enumerator labels and semantics, because comparing them would report differences that
    /// move no bytes. This keeps all of it, and adds the bounds the OMT never tabulates — see
    /// <see cref="ValueRange"/>.
    /// </para>
    /// <para>
    /// Nothing here throws on content. An unknown, absent, circular or over-deep type comes back as
    /// a node saying so, exactly as <see cref="Resolve"/> yields an unresolved signature.
    /// </para>
    /// </remarks>
    /// <param name="dataTypeName">The datatype name exactly as the FOM writes it.</param>
    /// <returns>The explanation tree. Never null and never throws.</returns>
    public DataTypeDetail Explain(string? dataTypeName) =>
        Explain(dataTypeName, depth: 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private DataTypeDetail Explain(string? dataTypeName, int depth, HashSet<string> onPath)
    {
        var name = dataTypeName?.Trim() ?? "";
        var canonical = Resolve(dataTypeName);

        if (string.IsNullOrEmpty(name))
            return Undeclared(name, canonical, "No datatype is stated for this attribute.");

        if (depth >= MaxExplainDepth)
            return Undeclared(name, canonical, $"Nested more than {MaxExplainDepth} levels deep; not unfolded further.");

        // The graph loops. Report the loop instead of recursing into it: a malformed FOM must not be
        // able to take the window down.
        if (!onPath.Add(name))
            return Undeclared(name, canonical, $"'{name}' contains itself; the definition loops here.");

        try
        {
            if (_basic.TryGetValue(name, out var basicType)) return ExplainBasic(name, basicType, canonical, fromMim: false);
            if (_simple.TryGetValue(name, out var simpleType)) return ExplainSimple(name, simpleType, canonical, depth, onPath, fromMim: false);
            if (_enumerated.TryGetValue(name, out var enumType)) return ExplainEnumerated(name, enumType, canonical, depth, onPath);
            if (_arrays.TryGetValue(name, out var arrayType)) return ExplainArray(name, arrayType, canonical, depth, onPath, fromMim: false);
            if (_records.TryGetValue(name, out var recordType)) return ExplainRecord(name, recordType, canonical, depth, onPath);
            if (_variants.TryGetValue(name, out var variantType)) return ExplainVariant(name, variantType, canonical, depth, onPath);

            // Same order Resolve uses: the document wins, then the MIM, then the 1.3 lexicon.
            if (StandardMim.Basic.TryGetValue(name, out var mimBasic)) return ExplainBasic(name, mimBasic, canonical, fromMim: true);
            if (StandardMim.Simple.TryGetValue(name, out var mimSimple)) return ExplainSimple(name, mimSimple, canonical, depth, onPath, fromMim: true);
            if (StandardMim.Arrays.TryGetValue(name, out var mimArray)) return ExplainArray(name, mimArray, canonical, depth, onPath, fromMim: true);

            return ExplainPrimitive(name, canonical);
        }
        finally
        {
            onPath.Remove(name);
        }
    }

    private static DataTypeDetail ExplainBasic(string name, BasicDataType type, DataTypeSignature canonical, bool fromMim) => new()
    {
        Name = name,
        Table = DataTypeTable.Basic,
        Shape = canonical.Shape,
        Canonical = canonical.Canonical,
        IsFromStandardMim = fromMim,
        Bits = canonical.Bits,
        Endian = canonical.Endian,
        Interpretation = Blank(type.Interpretation),
        Encoding = Blank(type.Encoding),
        Semantics = Blank(type.Semantics),

        // A basic representation is the one place a real width is stated, so it is the one place a
        // representable range can be derived without assuming anything.
        Range = ValueRange.FromCanonical(canonical.Canonical),
    };

    private DataTypeDetail ExplainSimple(
        string name, SimpleDataType type, DataTypeSignature canonical, int depth, HashSet<string> onPath, bool fromMim)
    {
        var representation = Blank(type.Representation);

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.Simple,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            IsFromStandardMim = fromMim,
            Bits = canonical.Bits,
            Endian = canonical.Endian,
            Representation = representation,
            Units = Blank(type.Units),
            Resolution = Blank(type.Resolution),
            Accuracy = Blank(type.Accuracy),
            Semantics = Blank(type.Semantics),

            // The range comes from the representation this type is carried in. Units and resolution
            // sit beside it rather than inside it: they say what the number means, not how far it goes.
            Range = ValueRange.FromCanonical(canonical.Canonical),
            Members = representation is null
                ? Array.Empty<DataTypeDetailMember>()
                : new[]
                {
                    new DataTypeDetailMember
                    {
                        Name = representation,
                        Role = DataTypeMemberRole.Representation,
                        Type = Explain(representation, depth + 1, onPath),
                    },
                },
        };
    }

    private DataTypeDetail ExplainEnumerated(
        string name, EnumeratedDataType type, DataTypeSignature canonical, int depth, HashSet<string> onPath)
    {
        var members = new List<DataTypeDetailMember>(type.Enumerators.Count + 1);
        var representation = Blank(type.Representation);

        if (representation is not null)
            members.Add(new DataTypeDetailMember
            {
                Name = representation,
                Role = DataTypeMemberRole.Representation,
                Type = Explain(representation, depth + 1, onPath),
            });

        foreach (var enumerator in type.Enumerators)
            members.Add(new DataTypeDetailMember
            {
                Name = enumerator.Name,
                Role = DataTypeMemberRole.Enumerator,
                Value = Blank(enumerator.Values),
                Semantics = Blank(enumerator.Semantics),
            });

        // The enumerator set is the real constraint and beats the representation's span: the
        // representation says a value could be any of four billion things, the enumerators say which
        // twelve are legal. Fall back to the representation when nothing is declared.
        var declared = type.Enumerators
            .Where(e => !string.IsNullOrWhiteSpace(e.Values))
            .Select(e => e.Values!)
            .ToList();

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.Enumerated,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            Bits = canonical.Bits,
            Endian = canonical.Endian,
            Representation = representation,
            Semantics = Blank(type.Semantics),
            Range = ValueRange.FromEnumerators(declared) ?? ValueRange.FromCanonical(canonical.Canonical),
            Members = members,
        };
    }

    private DataTypeDetail ExplainArray(
        string name, ArrayDataType type, DataTypeSignature canonical, int depth, HashSet<string> onPath, bool fromMim)
    {
        var element = Blank(type.DataType);

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.Array,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            IsFromStandardMim = fromMim,
            Encoding = Blank(type.Encoding),
            Cardinality = Blank(type.Cardinality),
            Semantics = Blank(type.Semantics),

            // An array bounds a count, not a value: how many elements, not how large each one is.
            // The element's own range is on its node.
            Range = ValueRange.FromCardinality(type.Cardinality),
            Members = element is null
                ? Array.Empty<DataTypeDetailMember>()
                : new[]
                {
                    new DataTypeDetailMember
                    {
                        Name = element,
                        Role = DataTypeMemberRole.Element,
                        Type = Explain(element, depth + 1, onPath),
                    },
                },
        };
    }

    private DataTypeDetail ExplainRecord(
        string name, FixedRecordDataType type, DataTypeSignature canonical, int depth, HashSet<string> onPath)
    {
        var members = new List<DataTypeDetailMember>(type.Fields.Count);

        foreach (var field in type.Fields)
            members.Add(new DataTypeDetailMember
            {
                Name = field.Name,
                Role = DataTypeMemberRole.Field,
                Value = Blank(field.DataType),
                Semantics = Blank(field.Semantics),
                Type = Explain(field.DataType, depth + 1, onPath),
            });

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.FixedRecord,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            Encoding = Blank(type.Encoding),
            Semantics = Blank(type.Semantics),

            // A record has no single span of its own — each field bounds itself.
            Members = members,
        };
    }

    private DataTypeDetail ExplainVariant(
        string name, VariantRecordDataType type, DataTypeSignature canonical, int depth, HashSet<string> onPath)
    {
        var members = new List<DataTypeDetailMember>(type.Alternatives.Count + 1);
        var discriminantType = Blank(type.DataType);

        if (discriminantType is not null)
            members.Add(new DataTypeDetailMember
            {
                // Discriminant is the field's name; DataType is what it is carried in.
                Name = Blank(type.Discriminant) ?? discriminantType,
                Role = DataTypeMemberRole.Discriminant,
                Value = discriminantType,
                Type = Explain(discriminantType, depth + 1, onPath),
            });

        foreach (var alternative in type.Alternatives)
            members.Add(new DataTypeDetailMember
            {
                Name = alternative.Name,
                Role = DataTypeMemberRole.Alternative,
                Value = Blank(alternative.Enumerator),
                Semantics = Blank(alternative.Semantics),
                Type = Explain(alternative.DataType, depth + 1, onPath),
            });

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.VariantRecord,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            Encoding = Blank(type.Encoding),
            Semantics = Blank(type.Semantics),
            Members = members,
        };
    }

    /// <summary>
    /// A name no table declares. It is still worth explaining: an HLA 1.3 OMT types every attribute
    /// this way, and the lexicon knows how wide those are even though the standard tabulates nothing.
    /// </summary>
    private static DataTypeDetail ExplainPrimitive(string name, DataTypeSignature canonical)
    {
        if (!canonical.IsResolved)
            return Undeclared(name, canonical,
                $"'{name}' is in none of this FOM's datatype tables, is not declared by the HLA standard MIM, " +
                "and matches no HLA 1.3 primitive. Its encoding cannot be established from this file.");

        return new DataTypeDetail
        {
            Name = name,
            Table = DataTypeTable.Primitive,
            Shape = canonical.Shape,
            Canonical = canonical.Canonical,
            Bits = canonical.Bits,
            Endian = canonical.Endian,
            Range = ValueRange.FromCanonical(canonical.Canonical),
        };
    }

    /// <summary>A node that explains why there is nothing to explain.</summary>
    private static DataTypeDetail Undeclared(string name, DataTypeSignature canonical, string reason) => new()
    {
        Name = name,
        Table = DataTypeTable.None,
        Shape = canonical.Shape,
        Canonical = canonical.Canonical,
        Truncation = reason,
    };

    /// <summary>Treats a whitespace-only column as the absence it is.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ------------------------------------------------------------------ the six tables

    /// <summary>A basic representation: the only place a real bit width is stated.</summary>
    private DataTypeSignature ResolveBasic(string name, BasicDataType type)
    {
        var endian = NormaliseEndian(type.Endian);
        var bits = ParseBits(type.Size);
        var kind = BasicKind(name, type);

        if (bits is null)
        {
            // The width column was missing or unreadable. A standard basic type still names its own
            // shape (HLAoctet, HLAfloat64BE), so give the lexicon a second chance before giving up.
            if (Lexicon.TryGetValue(NormaliseWhitespace(name), out var spec))
                return FromPrimitive(spec, name, endian);

            return new DataTypeSignature
            {
                Shape = DataTypeShape.Scalar,
                // The name is kept inside the canonical so two width-less scalars of the same kind
                // cannot be reported as encoding alike when nothing establishes that they do.
                Canonical = $"{kind}:?({name})",
                Endian = endian,
                SourceName = name,
            };
        }

        return new DataTypeSignature
        {
            Shape = DataTypeShape.Scalar,
            Canonical = $"{kind}:{bits.Value.ToString(CultureInfo.InvariantCulture)}",
            Bits = bits,
            Endian = endian,
            SourceName = name,
        };
    }

    /// <summary>
    /// A simple type is a representation plus semantics. Only the representation is encoding, so the
    /// signature is the representation's, re-badged with this name — which is exactly what turns
    /// <c>float</c> to <c>AngleRadianFloat32</c> into a rename rather than a conversion.
    /// </summary>
    private DataTypeSignature ResolveSimple(string name, SimpleDataType type)
    {
        if (string.IsNullOrWhiteSpace(type.Representation))
            return DataTypeSignature.Unresolved(name);

        var representation = Resolve(type.Representation);

        return new DataTypeSignature
        {
            Shape = representation.Shape,
            Canonical = representation.Canonical,
            Bits = representation.Bits,
            Endian = representation.Endian,
            SourceName = name,
            Parts = representation.Parts,
        };
    }

    /// <summary>
    /// An enumeration encodes as its representation. The enumerator list is deliberately absent from
    /// the canonical form: adding or retiring a value changes what the field may say, not how many
    /// bits it occupies, and a caller that needs to see the added values compares them directly.
    /// </summary>
    private DataTypeSignature ResolveEnumerated(string name, EnumeratedDataType type)
    {
        var representation = Resolve(type.Representation);

        // An enumeration encodes EXACTLY as its representation. The enumerator set constrains which
        // values are legal; it does not change a single bit on the wire. So the canonical form is the
        // representation's, with no wrapper — the same rule already applied to a simple type, where
        // units and resolution are dropped for being semantics rather than encoding.
        //
        // This is what stops a whole class of false work. Migrating RPR 1.0 to 2.0 retypes 510
        // attributes from the 1.3 primitive `boolean` to `RPRboolean`, an enumeration over an 8-bit
        // type. Both are one octet carrying 0 or 1; nothing needs converting. Wrapping the canonical
        // in "enum:" reported every one of them as a re-encoding.
        //
        // HLA 1.3 states no representation at all — the width hides in the type's own name. Naming
        // the enumeration in that case keeps two width-less enumerations from colliding while still
        // letting the same enumeration on both sides match.
        var canonical = representation.IsResolved
            ? representation.Canonical
            : $"enum:?({name})";

        return new DataTypeSignature
        {
            Shape = DataTypeShape.Enumerated,
            Canonical = canonical,
            Bits = representation.Bits,
            Endian = representation.Endian,
            SourceName = name,
        };
    }

    /// <summary>
    /// An array is its element plus its length rule. Cardinality stays in the canonical because a
    /// fixed-length array and a variable-length one of the same element are different on the wire.
    /// </summary>
    private DataTypeSignature ResolveArray(string name, ArrayDataType type)
    {
        var element = Resolve(type.DataType);
        var cardinality = NormaliseCardinality(type.Cardinality);

        return new DataTypeSignature
        {
            Shape = DataTypeShape.Array,
            Canonical = $"array({element.Canonical},{cardinality})",
            SourceName = name,
            Parts = new[] { element },
        };
    }

    /// <summary>
    /// A fixed record is its fields, in order. Field <em>names</em> are excluded — renaming a field
    /// moves no bytes — while order and type are included, because they place every byte.
    /// </summary>
    private DataTypeSignature ResolveRecord(string name, FixedRecordDataType type)
    {
        var parts = new List<DataTypeSignature>(type.Fields.Count);
        var canonical = new StringBuilder("record(");

        for (var i = 0; i < type.Fields.Count; i++)
        {
            var field = Resolve(type.Fields[i].DataType);
            parts.Add(field);
            if (i > 0) canonical.Append(',');
            canonical.Append(field.Canonical);
        }

        canonical.Append(')');

        return new DataTypeSignature
        {
            Shape = DataTypeShape.Record,
            Canonical = canonical.ToString(),
            SourceName = name,
            Parts = parts,
        };
    }

    /// <summary>
    /// A variant record is its discriminant's type followed by its alternatives, in order. The
    /// enumerator labels are excluded for the same reason field names are: they select a branch,
    /// they do not lay out bytes.
    /// </summary>
    private DataTypeSignature ResolveVariant(string name, VariantRecordDataType type)
    {
        // DataType is the discriminant's type; Discriminant is only the field's name.
        var discriminant = Resolve(type.DataType);

        var parts = new List<DataTypeSignature>(type.Alternatives.Count + 1) { discriminant };
        var canonical = new StringBuilder("variant(").Append(discriminant.Canonical).Append(';');

        for (var i = 0; i < type.Alternatives.Count; i++)
        {
            var alternative = Resolve(type.Alternatives[i].DataType);
            parts.Add(alternative);
            if (i > 0) canonical.Append(',');
            canonical.Append(alternative.Canonical);
        }

        canonical.Append(')');

        return new DataTypeSignature
        {
            Shape = DataTypeShape.Variant,
            Canonical = canonical.ToString(),
            SourceName = name,
            Parts = parts,
        };
    }

    // ------------------------------------------------------------------- the lexicon

    /// <summary>
    /// Last resort for a name no table declares — which is the normal case for an HLA 1.3 OMT, whose
    /// attributes are typed with C/IDL primitives that the standard never tabulates anywhere.
    /// </summary>
    private static DataTypeSignature ResolvePrimitive(string name)
    {
        var key = NormaliseWhitespace(name);

        // "NA" is how a 1.3 OMT says an attribute has no datatype. It is an absence, not a type, and
        // must never be reported as encoding the same as another absence.
        if (NotApplicable.Contains(key))
            return DataTypeSignature.Unresolved(name);

        return Lexicon.TryGetValue(key, out var spec)
            ? FromPrimitive(spec, name, endian: null)
            : DataTypeSignature.Unresolved(name);
    }

    private static DataTypeSignature FromPrimitive(PrimitiveSpec spec, string name, string? endian) => new()
    {
        Shape = spec.Shape,
        Canonical = spec.Canonical,
        Bits = spec.Bits,
        Endian = endian,
        SourceName = name,
    };

    /// <summary>The spellings a FOM uses to say "this attribute carries no datatype".</summary>
    private static readonly HashSet<string> NotApplicable = new(StringComparer.OrdinalIgnoreCase)
    {
        "NA", "N/A", "none",
    };

    /// <summary>
    /// C/IDL primitive names to their encoding, keyed on the whitespace-normalised spelling so
    /// <c>unsigned  long</c> and <c>unsigned long</c> are one entry.
    /// </summary>
    /// <remarks>
    /// Widths are the ones HLA 1.3 federations were built against — <c>long</c> is 32 bits and
    /// <c>long long</c> 64, as on every platform an RTI shipped for. <c>string</c> and
    /// <c>wstring</c> share one canonical form because a 1.3 OMT says nothing about which encoding
    /// or which length prefix it meant, and inventing a distinction it never stated would report
    /// differences nobody can act on.
    /// </remarks>
    private static readonly Dictionary<string, PrimitiveSpec> Lexicon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["octet"] = new(DataTypeShape.Scalar, "uint:8", 8),
        ["unsigned char"] = new(DataTypeShape.Scalar, "uint:8", 8),
        ["char"] = new(DataTypeShape.Scalar, "char:8", 8),
        // An HLA 1.3 / IDL boolean is one octet carrying 0 or 1, so it canonicalises as the octet it
        // is rather than as a distinct "bool" family. That is what lets it match an 8-bit enumeration
        // such as RPR 2.0's RPRboolean, which is the same byte with the same two values — the single
        // largest source of false re-encodings in a 1.3-to-1516 migration.
        //
        // Note this is deliberately NOT a claim that every boolean is 8 bits: the 1516 MIM's
        // HLAboolean is an enumeration over HLAinteger32BE and resolves, correctly, to int:32. Only
        // the 1.3 primitive is fixed at one octet.
        ["boolean"] = new(DataTypeShape.Scalar, "uint:8", 8),
        ["bool"] = new(DataTypeShape.Scalar, "uint:8", 8),
        ["short"] = new(DataTypeShape.Scalar, "int:16", 16),
        ["unsigned short"] = new(DataTypeShape.Scalar, "uint:16", 16),
        ["long"] = new(DataTypeShape.Scalar, "int:32", 32),
        ["int"] = new(DataTypeShape.Scalar, "int:32", 32),
        ["unsigned long"] = new(DataTypeShape.Scalar, "uint:32", 32),
        ["unsigned int"] = new(DataTypeShape.Scalar, "uint:32", 32),
        ["long long"] = new(DataTypeShape.Scalar, "int:64", 64),
        ["unsigned long long"] = new(DataTypeShape.Scalar, "uint:64", 64),
        ["float"] = new(DataTypeShape.Scalar, "float:32", 32),
        ["double"] = new(DataTypeShape.Scalar, "float:64", 64),
        ["long double"] = new(DataTypeShape.Scalar, "float:64", 64),
        ["wchar"] = new(DataTypeShape.Scalar, "char:16", 16),
        // A string is a run of characters of unstated length, so it is an array shape with no width.
        ["string"] = new(DataTypeShape.Array, "string:n", null),
        ["wstring"] = new(DataTypeShape.Array, "string:n", null),
    };

    /// <summary>One primitive's encoding: what it is, how it is written, how wide it is.</summary>
    private readonly record struct PrimitiveSpec(DataTypeShape Shape, string Canonical, int? Bits);

    // ------------------------------------------------------- the HLA standard MIM

    /// <summary>
    /// The datatypes the HLA standard MIM declares and no FOM module does, transcribed from
    /// <c>HLAstandardMIM.xml</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once, on first touch of this nested type, and shared by every resolver ever created:
    /// the MIM does not change between documents, and rebuilding thirty-six declarations per
    /// document would be waste on a comparison that already constructs one resolver per side. The
    /// tables are read-only after construction and nothing here is ever mutated, which is what makes
    /// sharing them across threads safe even though a resolver instance is not.
    /// </para>
    /// <para>
    /// <b>Declarations only.</b> No resolved signature is cached here; see the class remarks for why
    /// a statically cached answer would leak one document's redeclaration into the next.
    /// </para>
    /// <para>
    /// Only the basic, simple and array datatypes are carried, because those are what real FOM
    /// modules bottom out on. The enumerated and fixed-record types the MIM also declares are left
    /// out deliberately rather than guessed at: an entry recalled imperfectly would assert an
    /// encoding nobody checked, which is worse than answering "unresolved" and saying so.
    /// </para>
    /// <para>
    /// The interpretation column below is <em>not</em> the MIM's own prose. Interpretation is read
    /// for keywords, and the MIM's wording for <c>HLAoctet</c> — "Uninterpreted 8-bit value" —
    /// contains the letters of "int" inside "Uninterpreted" and would be read as a signed integer.
    /// What is stored is therefore the kind itself, stated so it cannot be misread: the
    /// <c>HLAintegerNN*</c> are signed, the <c>HLAfloatNN*</c> are floats, and <c>HLAoctet</c> and
    /// <c>HLAoctetPair*</c> are uninterpreted runs of bytes carried as unsigned values of 8 and 16
    /// bits.
    /// </para>
    /// </remarks>
    private static class StandardMim
    {
        private const string Integer = "Integer";
        private const string Floating = "Floating point number";
        private const string Unsigned = "Unsigned value";

        private const string Big = "Big";
        private const string Little = "Little";

        /// <summary>The MIM's basic representations: the only entries here that state a bit width.</summary>
        internal static readonly FrozenDictionary<string, BasicDataType> Basic = Build(
            new (string Name, int Bits, string Endian, string Kind)[]
            {
                ("HLAinteger16BE", 16, Big, Integer),
                ("HLAinteger16LE", 16, Little, Integer),
                ("HLAinteger32BE", 32, Big, Integer),
                ("HLAinteger32LE", 32, Little, Integer),
                ("HLAinteger64BE", 64, Big, Integer),
                ("HLAinteger64LE", 64, Little, Integer),
                ("HLAfloat32BE", 32, Big, Floating),
                ("HLAfloat32LE", 32, Little, Floating),
                ("HLAfloat64BE", 64, Big, Floating),
                ("HLAfloat64LE", 64, Little, Floating),
                ("HLAoctetPairBE", 16, Big, Unsigned),
                ("HLAoctetPairLE", 16, Little, Unsigned),
                ("HLAoctet", 8, Big, Unsigned),
            },
            entry => new BasicDataType
            {
                Name = entry.Name,
                QualifiedName = entry.Name,
                Size = entry.Bits.ToString(CultureInfo.InvariantCulture),
                Interpretation = entry.Kind,
                Endian = entry.Endian,
            },
            entry => entry.Name);

        /// <summary>
        /// The MIM's simple types. Each is a representation plus semantics, and resolves to its
        /// representation exactly as a document-declared simple type does.
        /// </summary>
        internal static readonly FrozenDictionary<string, SimpleDataType> Simple = Build(
            new (string Name, string Representation)[]
            {
                ("HLAASCIIchar", "HLAoctet"),
                ("HLAunicodeChar", "HLAoctetPairBE"),
                ("HLAbyte", "HLAoctet"),
                ("HLAcount", "HLAinteger32BE"),
                ("HLAseconds", "HLAinteger32BE"),
                ("HLAmsec", "HLAinteger32BE"),
                ("HLAnormalizedFederateHandle", "HLAinteger32BE"),
                ("HLAindex", "HLAinteger32BE"),
                ("HLAinteger64Time", "HLAinteger64BE"),
                ("HLAfloat64Time", "HLAfloat64BE"),
            },
            entry => new SimpleDataType
            {
                Name = entry.Name,
                QualifiedName = entry.Name,
                Representation = entry.Representation,
            },
            entry => entry.Name);

        /// <summary>
        /// The MIM's arrays. <c>HLAASCIIstring</c> is an array over <c>HLAASCIIchar</c> over
        /// <c>HLAoctet</c> and is resolved through all three steps rather than short-circuited to a
        /// canned answer, so that a FOM which writes these out itself lands on the same canonical
        /// form as one that leaves them to the RTI.
        /// </summary>
        internal static readonly FrozenDictionary<string, ArrayDataType> Arrays = Build(
            new (string Name, string Element, string Cardinality)[]
            {
                ("HLAASCIIstring", "HLAASCIIchar", "Dynamic"),
                ("HLAunicodeString", "HLAunicodeChar", "Dynamic"),
                ("HLAopaqueData", "HLAbyte", "Dynamic"),
                ("HLAtoken", "HLAbyte", "0"),
                ("HLAhandle", "HLAbyte", "Dynamic"),
                ("HLAtransportationName", "HLAunicodeChar", "Dynamic"),
                ("HLAupdateRateName", "HLAunicodeChar", "Dynamic"),
                ("HLAlogicalTime", "HLAbyte", "Dynamic"),
                ("HLAtimeInterval", "HLAbyte", "Dynamic"),
                ("HLAhandleList", "HLAhandle", "Dynamic"),
                ("HLAargumentList", "HLAunicodeString", "Dynamic"),
                ("HLAsynchPointList", "HLAunicodeString", "Dynamic"),
                ("HLAmoduleDesignatorList", "HLAunicodeString", "Dynamic"),
            },
            entry => new ArrayDataType
            {
                Name = entry.Name,
                QualifiedName = entry.Name,
                DataType = entry.Element,
                Cardinality = entry.Cardinality,
            },
            entry => entry.Name);

        // Keyed ordinal-ignore-case for the same reason the document's tables are: a FOM that writes
        // "HLAoctet" as "HLAOctet" means the standard type, not an unknown one.
        private static FrozenDictionary<string, TValue> Build<TEntry, TValue>(
            IEnumerable<TEntry> entries,
            Func<TEntry, TValue> toValue,
            Func<TEntry, string> toName) =>
            entries
                .Select(entry => new KeyValuePair<string, TValue>(toName(entry), toValue(entry)))
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------------- text helpers

    private static void TryIndex<T>(Dictionary<string, T> index, string? name, T value)
    {
        if (!string.IsNullOrWhiteSpace(name))
            index.TryAdd(name.Trim(), value);
    }

    /// <summary>
    /// Reads the leading integer of a size column, so both <c>32</c> and <c>32 bits</c> answer 32.
    /// Sizes in an OMT are bit counts; anything unreadable answers null rather than a guess.
    /// </summary>
    private static int? ParseBits(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var text = size.AsSpan().Trim();
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;

        if (digits == 0)
            return null;

        if (!int.TryParse(text[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            return null;

        // A width outside this range is a parsing accident, not a datatype anyone declared.
        return bits is > 0 and <= 65536 ? bits : null;
    }

    /// <summary>Normalises a stated byte order to "BE"/"LE"; anything else counts as unstated.</summary>
    private static string? NormaliseEndian(string? endian)
    {
        if (string.IsNullOrWhiteSpace(endian))
            return null;

        var text = endian.Trim();

        if (text.Contains("big", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("BE", StringComparison.OrdinalIgnoreCase))
            return "BE";

        if (text.Contains("little", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("LE", StringComparison.OrdinalIgnoreCase))
            return "LE";

        // "Portable" and friends state no order that could be compared against another FOM's.
        return null;
    }

    /// <summary>
    /// Collapses the several ways a FOM writes "variable length" onto one token, and leaves a fixed
    /// count exactly as written so that 3 and 31 stay different.
    /// </summary>
    private static string NormaliseCardinality(string? cardinality)
    {
        if (string.IsNullOrWhiteSpace(cardinality))
            return "n";

        var text = NormaliseWhitespace(cardinality);

        return text.Equals("Dynamic", StringComparison.OrdinalIgnoreCase)
               || text.Equals("n", StringComparison.OrdinalIgnoreCase)
               || text == "-"
            ? "n"
            : text;
    }

    /// <summary>
    /// Trims and squeezes internal whitespace runs to one space, so a lexicon lookup is not defeated
    /// by <c>unsigned&#160;&#160;long</c> or by a tab that survived an export.
    /// </summary>
    private static string NormaliseWhitespace(string text)
    {
        var trimmed = text.Trim();

        var needsWork = false;
        for (var i = 1; i < trimmed.Length; i++)
        {
            if (char.IsWhiteSpace(trimmed[i]) && (char.IsWhiteSpace(trimmed[i - 1]) || trimmed[i] != ' '))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;

        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                    builder.Append(' ');
                previousWasSpace = true;
            }
            else
            {
                builder.Append(character);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The kind of scalar a basic representation carries: read from its interpretation column, from
    /// its own name when that column says nothing usable, and promoted to unsigned when either the
    /// name or the encoding column says so outright.
    /// </summary>
    /// <remarks>
    /// The promotion exists because an OMT routinely states signedness nowhere the interpretation
    /// column can be read for it. RPR 2.0 declares <c>RPRunsignedInteger32BE</c> with the
    /// interpretation "Integer in the range [0, 2^32-1]" — plainly unsigned to a human, plainly just
    /// "integer" to a keyword match — while both its name and its encoding column say "unsigned"
    /// outright. Left as <c>int:32</c> it would not match the 1.3 side's <c>unsigned long</c>
    /// (<c>uint:32</c>), and one of the most common renames in the whole migration would be reported
    /// as a re-encoding. Only <c>int</c> is promoted, and only on the literal word, so a float, a
    /// char or an already-unsigned kind cannot be moved by it.
    /// </remarks>
    private static string BasicKind(string name, BasicDataType type)
    {
        var kind = KindOf(type.Interpretation) ?? KindOf(name) ?? "scalar";

        if (kind == "int" && (Mentions(name, "unsigned") || Mentions(type.Encoding, "unsigned")))
            return "uint";

        return kind;

        static bool Mentions(string? text, string word) =>
            text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the kind of scalar out of an interpretation column, or out of a type's own name when
    /// the column says nothing usable — the standard basic types spell their kind into their names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters, and every test above the integer one is there because the letters "int" hide
    /// inside a word that means something else. "Floating point number" carries it in "point".
    /// "Uninterpreted 8-bit byte" — how the HLA standard MIM words <c>HLAoctet</c>, and how every FOM
    /// that spells the MIM out repeats it — carries it in "uninterpreted", and being read as a signed
    /// integer there is not cosmetic: it makes <c>HLAoctet</c> resolve to <c>int:8</c>, so RPR 1.0's
    /// <c>octet</c> and RPR 2.0's <c>Octet</c> stop matching and a migration reports a re-encoding on
    /// every uninterpreted byte it carries. So "uninterpreted" is tested first, on the same principle
    /// floating point already was — and only that word, so the long-standing precedence between the
    /// integer and octet tests below is left exactly as it was.
    /// </para>
    /// <para>
    /// Substring matching is kept rather than replaced with whole-word matching because the column is
    /// free prose that real FOMs write a dozen ways — "Integer", "32-bit integer", "signed integer
    /// value" — and requiring an exact token would resolve fewer real files, not more.
    /// </para>
    /// </remarks>
    private static string? KindOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (Has(text, "unsigned")) return "uint";
        if (Has(text, "float") || Has(text, "real")) return "float";

        // Only "uninterpreted" jumps ahead of the integer test, because it is the one word that
        // hides "int" while meaning the opposite of one. Octet and byte stay below it, where they
        // have always been, so an interpretation that names both a byte and an integer keeps
        // reading as the integer it says it is.
        if (Has(text, "uninterpreted")) return "uint";

        if (Has(text, "integer") || Has(text, "int")) return "int";
        if (Has(text, "octet") || Has(text, "byte")) return "uint";
        if (Has(text, "bool") || Has(text, "logical")) return "bool";
        if (Has(text, "char")) return "char";

        return null;

        static bool Has(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
