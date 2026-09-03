using System;
using System.Collections.Generic;
using HLAFomReader.Core.Comparison;

namespace HLAFomReader.Core.Reporting;

/// <summary>
/// One line of the side-by-side remap worksheet: an attribute, or one level of the structure inside
/// its datatype, with what each FOM carries there.
/// </summary>
/// <remarks>
/// A row is a <em>pairing</em> rather than a thing: both halves are filled when the two sides line
/// up, and one half is blank where only one side has it. That is what makes the sheet readable
/// across the fold — the eye runs along a row to see whether the same bytes arrive on the other
/// side, and drops down a level to see where inside a record they stopped agreeing.
/// </remarks>
public sealed class AttributePairRow
{
    /// <summary>
    /// 1 for the attribute itself, 2 for the members of its datatype, 3 for the members of those.
    /// </summary>
    /// <remarks>
    /// The number is the machine-readable truth about level, the same way
    /// <see cref="ClassHierarchyExporter"/> writes a numeric <b>Level</b> column beside its
    /// staircase. The indentation on the two name columns is for the eye and is not worth parsing.
    /// </remarks>
    public required int Depth { get; init; }

    /// <summary>
    /// What the row is inside its parent, or null at depth 1 where the row is the attribute itself.
    /// </summary>
    public DataTypeMemberRole? Role { get; init; }

    /// <summary>
    /// The role worded for a reader — "Attribute", "Field", "Element", "Discriminant",
    /// "Alternative" — read from <see cref="DataTypeMemberRoleText"/> so the sheet and the datatype
    /// inspector cannot drift apart.
    /// </summary>
    public string Kind => Role is null ? "Attribute" : DataTypeMemberRoleText.Label(Role.Value);

    /// <summary>
    /// The depth-1 attribute this row belongs to, repeated on every row of its block.
    /// </summary>
    /// <remarks>
    /// Repeated rather than written once, for the reason <see cref="ClassMemberExporter"/> repeats a
    /// class name: the sheet is going to be sorted and filtered, and a stray <c>X</c> three rows deep
    /// has to still be able to say which attribute it came out of.
    /// </remarks>
    public required string AttributeName { get; init; }

    /// <summary>
    /// How the two halves compare, or <see cref="AttributeMapStatus.Unpaired"/> where no class is
    /// chosen on the other side. Null where nothing can be established — typically an unresolved
    /// datatype name on one side, which is evidence neither way.
    /// </summary>
    public AttributeMapStatus? Match { get; init; }

    /// <summary>The A-side name: the attribute, the field, or the element's type.</summary>
    public string? NameA { get; init; }

    public string? DataTypeA { get; init; }

    /// <summary>The canonical encoding of <see cref="DataTypeA"/> — <c>uint:32</c> and the like.</summary>
    public string? EncodingA { get; init; }

    public string? NameB { get; init; }
    public string? DataTypeB { get; init; }
    public string? EncodingB { get; init; }

    /// <summary>The class that declares the attribute in FOM A. Depth 1 only.</summary>
    public string? DeclaredInA { get; init; }

    /// <summary>The class that declares the attribute in FOM B. Depth 1 only.</summary>
    public string? DeclaredInB { get; init; }

    /// <summary>
    /// Why the row reads as it does, when the reason is not visible in the columns: a positional
    /// pairing, a datatype that could not be resolved, a loop in the FOM's own datatype graph, or
    /// the sheet reaching one of its own limits.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// The whole side-by-side worksheet for one pair of classes.
/// </summary>
/// <remarks>
/// Built as data and rendered separately, the same split <see cref="ClassHierarchyExporter"/> and
/// <see cref="ClassMemberExporter"/> use, so the layout can be asserted in a test without writing a
/// file and reading it back.
/// </remarks>
public sealed class AttributePairSheet
{
    /// <summary>The qualified name of the class chosen in FOM A, or null when none is.</summary>
    public string? ClassA { get; init; }

    /// <summary>The qualified name of the class chosen in FOM B, or null when none is.</summary>
    public string? ClassB { get; init; }

    /// <summary>What FOM A is called — its model name, else its file name.</summary>
    public string LeftLabel { get; init; } = "FOM A";

    /// <summary>What FOM B is called.</summary>
    public string RightLabel { get; init; } = "FOM B";

    public required IReadOnlyList<AttributePairRow> Rows { get; init; }

    /// <summary>
    /// Fidelity notes carried over from the map, plus anything the expansion itself had to say.
    /// </summary>
    /// <remarks>
    /// Not written into the worksheet. These describe the whole comparison rather than any row, so
    /// they would have to go above the header, where they would push the staircase down and be the
    /// first thing a sort or a filter swept up. The per-row reasons are in the Note column already;
    /// these are for the dialog that reports the export, where the reader still has the screen.
    /// </remarks>
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();
}

/// <summary>How far the side-by-side sheet unfolds, and what it leaves out.</summary>
public sealed class AttributePairSheetOptions
{
    /// <summary>Name folding, so the sheet cannot disagree with the grid it was taken from.</summary>
    public ComparisonOptions? Comparison { get; set; }

    /// <summary>
    /// The deepest level written. 1 is the attribute, 2 its datatype's members, 3 theirs.
    /// </summary>
    /// <remarks>
    /// Must stay at or below <see cref="DataTypeResolver"/>'s own unfolding limit, or the walk would
    /// reach nodes that were never expanded and report a composite type as a leaf. Three is the
    /// level the user asked for and is where a remap decision is actually made: a record of records
    /// of scalars is as deep as almost anything in RPR goes.
    /// </remarks>
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// The most rows any one attribute may contribute before its expansion is cut short.
    /// </summary>
    /// <remarks>
    /// The depth cap bounds levels, not fan-out. Three levels of a 200-field record whose fields are
    /// themselves records is tens of thousands of rows for a single attribute, which is not a
    /// worksheet anybody reads. Hitting this says so in the row's Note rather than truncating in
    /// silence.
    /// </remarks>
    public int MaxRowsPerAttribute { get; set; } = 500;

    /// <summary>
    /// Whether an enumerated datatype's declared values each earn a row. Off by default.
    /// </summary>
    /// <remarks>
    /// An enumeration's values say which values are legal, not how the bytes are laid out, and one
    /// RPR enumeration can declare hundreds. Turning them into rows buries the structure the sheet
    /// exists to show under a value list the datatype inspector already presents better.
    /// </remarks>
    public bool IncludeEnumerators { get; set; }

    /// <summary>
    /// Whether a simple or enumerated type's representation earns a row. Off by default.
    /// </summary>
    /// <remarks>
    /// It would restate the level-1 row: the resolver badges a simple type under its representation's
    /// signature, so the attribute's own Encoding column already <em>is</em> the representation's
    /// canonical form. On a FOM where nearly every attribute is a simple scalar this doubles the
    /// sheet and adds nothing.
    /// </remarks>
    public bool IncludeRepresentation { get; set; }
}
