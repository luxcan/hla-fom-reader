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

    /// <summary>
    /// No comparison has been made, because only one side has a class chosen. The attribute is
    /// reported as that side carries it, with the other side's columns blank.
    /// </summary>
    /// <remarks>
    /// This is the absence of a verdict rather than a verdict, and keeping it out of
    /// <see cref="OnlyInLeft"/> is what stops the screen lying to a user halfway through choosing.
    /// Picking Aircraft on A alone would otherwise report its 45 inherited attributes as 45 things
    /// FOM B has lost, count them in <see cref="AttributeDataMap.ActionableCount"/>, and light up
    /// the "Only in A" chip — an assertion about FOM B that nothing has yet been compared against.
    /// So it draws no status pill, counts towards nothing, and is not work.
    /// </remarks>
    Unpaired = 6,
}

/// <summary>One attribute of one object class, as it exists on each side.</summary>
public sealed class AttributeMapRow
{
    /// <summary>
    /// The class the row belongs to: FOM A's, or FOM B's on a row only the B side has.
    /// </summary>
    /// <remarks>
    /// On a whole-FOM map this is the one class both sides matched on. On a class-pair map the two
    /// classes may be named differently, and the pair is then a property of
    /// <see cref="AttributeDataMap.LeftClassName"/> and <see cref="AttributeDataMap.RightClassName"/>
    /// rather than of the row — repeating a single name here would say the two sides agreed on it.
    /// </remarks>
    public required string ClassName { get; init; }

    /// <summary>The attribute name as FOM A spells it, or FOM B's when only B carries it.</summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// FOM B's own spelling, when the two sides matched on a normalised name they spell differently
    /// — <c>privilegeToDelete</c> against <c>HLAprivilegeToDeleteObject</c>. Null when they agree.
    /// </summary>
    public string? RightAttributeName { get; init; }

    /// <summary>The class that declares it in FOM A — may be an ancestor, since attributes are inherited.</summary>
    public string? LeftDeclaredIn { get; init; }

    /// <summary>
    /// The dotted name of the declaring class in FOM A.
    /// </summary>
    /// <remarks>
    /// The local name alone is enough on a whole-FOM map, where both sides walk one matched tree.
    /// Once the two classes are chosen independently they may sit in unrelated hierarchies that both
    /// carry a <c>Platform</c>, and only the qualified name says which one declared the attribute.
    /// </remarks>
    public string? LeftDeclaredInQualified { get; init; }

    public string? LeftDataType { get; init; }

    public string? RightDeclaredIn { get; init; }

    /// <summary>The dotted name of the declaring class in FOM B; see <see cref="LeftDeclaredInQualified"/>.</summary>
    public string? RightDeclaredInQualified { get; init; }

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

    /// <summary>
    /// True when the two sides were compared and did not agree. An
    /// <see cref="AttributeMapStatus.Unpaired"/> row answers false: nothing was compared, so nothing
    /// can be said to differ.
    /// </summary>
    public bool IsDifferent =>
        Status != AttributeMapStatus.Same && Status != AttributeMapStatus.Unpaired;

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

    /// <summary>
    /// The qualified name of the class chosen on the A side, or null — for a whole-FOM
    /// <see cref="AttributeMapper.Build"/>, or for a class-pair map with nothing chosen on A.
    /// </summary>
    /// <remarks>
    /// Held on the map rather than on every row because with two independently chosen classes this
    /// is one fact per side, not one repeated against each of forty-five attributes where it could
    /// no longer say which side it belonged to.
    /// </remarks>
    public string? LeftClassName { get; init; }

    /// <summary>The qualified name of the class chosen on the B side; see <see cref="LeftClassName"/>.</summary>
    public string? RightClassName { get; init; }

    /// <summary>True when a class was chosen on both sides, so the rows are a real comparison.</summary>
    public bool ComparesBothSides => LeftClassName is not null && RightClassName is not null;

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

    /// <summary>Rows listed from one side because the other has no class chosen yet.</summary>
    public int UnpairedCount => Rows.Count(r => r.Status == AttributeMapStatus.Unpaired);

    /// <summary>
    /// Rows that need a decision when remapping: a real re-encoding, or an attribute one side has
    /// and the other does not.
    /// </summary>
    /// <remarks>
    /// Renames are excluded on purpose, as are moves. Neither costs the reader anything, and
    /// counting them here would restore exactly the noise the encoding resolution exists to remove.
    /// Unpaired rows are excluded because they are not a finding at all: a class chosen on one side
    /// only has been compared against nothing.
    /// </remarks>
    public int ActionableCount => DataTypeChangedCount + OnlyInLeftCount + OnlyInRightCount;

    public static AttributeDataMap Empty() => new() { Rows = new List<AttributeMapRow>() };
}

/// <summary>One object class in a FOM, as a class picker lists it.</summary>
/// <remarks>
/// The count is the <b>effective</b> attribute set — declared plus inherited — and is worked out by
/// the mapper's own resolution rather than read off the class, so the figure beside a name in the
/// picker is exactly the number of rows choosing that class produces. A count taken from anywhere
/// else would disagree the moment name folding applied, since the mapper matches
/// <c>privilegeToDelete</c> onto <c>HLAprivilegeToDeleteObject</c> and a raw walk does not.
/// </remarks>
/// <param name="QualifiedName">The dotted name, e.g. <c>ObjectRoot.BaseEntity.Platform.Aircraft</c>.</param>
/// <param name="AttributeCount">Size of the class's effective attribute set.</param>
public sealed record ObjectClassSummary(string QualifiedName, int AttributeCount);

/// <summary>
/// The wording used for a row's status wherever a person sees it.
/// </summary>
/// <remarks>
/// In Core because the exported worksheet writes these words too, and a sheet that disagrees with
/// the screen it was taken from is worse than no sheet. The App's badge, its chips and the CSV all
/// read from here.
/// </remarks>
public static class AttributeMapStatusText
{
    /// <summary>The label for one status, or <c>""</c> where the screen deliberately shows none.</summary>
    public static string Label(AttributeMapStatus status) => status switch
    {
        AttributeMapStatus.Same => "Same",
        AttributeMapStatus.DataTypeChanged => "Changed",
        // A rename needs no conversion, so it reads as Same everywhere the user sees it —
        // badge, chip and CSV alike. The datatype columns still show the two names.
        AttributeMapStatus.Renamed => "Same",
        AttributeMapStatus.Moved => "Moved",
        AttributeMapStatus.OnlyInLeft => "Only in A",
        AttributeMapStatus.OnlyInRight => "Only in B",
        // Blank, not "Unpaired": the cell is empty because no comparison was made, and a word here
        // would read as a verdict on a row that carries none.
        AttributeMapStatus.Unpaired => "",
        _ => "",
    };
}
