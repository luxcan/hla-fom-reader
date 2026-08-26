using System;

namespace HLAFomReader.Core.Comparison;

/// <summary>How much of each element to compare, as opposed to which elements to compare.</summary>
public enum ComparisonDepth
{
    /// <summary>
    /// Names only. Reports what exists on one side and not the other, and nothing else.
    /// </summary>
    Structure = 0,

    /// <summary>
    /// Names, plus the datatype of every attribute and parameter, plus the datatype definitions
    /// themselves. This is the default: for answering "will these two FOMs interoperate?", whether
    /// an attribute exists and what it is typed as carries almost all of the signal, while sharing,
    /// ownership, update type, accuracy and prose descriptions mostly generate noise.
    /// </summary>
    DataTypes = 1,

    /// <summary>
    /// Every property the OMT defines. Honest and exhaustive, and very loud — a cross-standard
    /// comparison at this depth reports hundreds of differences nobody authored.
    /// </summary>
    Full = 2,
}

/// <summary>
/// Knobs for <see cref="FomComparer"/>. Defaults are "strict": anything one standard
/// cannot express counts as a real difference, and only the naming conventions that
/// would otherwise make a cross-standard diff meaningless are normalised.
/// </summary>
public sealed class ComparisonOptions
{
    /// <summary>
    /// How much of each matched element to compare. Defaults to
    /// <see cref="ComparisonDepth.DataTypes"/> — name and datatype — which is what actually decides
    /// whether two FOMs agree. Raise it to <see cref="ComparisonDepth.Full"/> for an exhaustive audit.
    /// </summary>
    public ComparisonDepth Depth { get; set; } = ComparisonDepth.DataTypes;

    /// <summary>Compare element names case-insensitively.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>Exclude &lt;semantics&gt; prose from the comparison.</summary>
    public bool IgnoreSemantics { get; set; }

    /// <summary>Exclude note references from the comparison.</summary>
    public bool IgnoreNotes { get; set; } = true;

    /// <summary>
    /// Treat the HLA 1.3 and 1516 root names as the same class, so a cross-standard
    /// diff lines the trees up instead of reporting every class twice:
    /// ObjectRoot↔HLAobjectRoot, InteractionRoot↔HLAinteractionRoot,
    /// privilegeToDelete↔HLAprivilegeToDeleteObject.
    /// </summary>
    public bool NormalizeRootNames { get; set; } = true;

    /// <summary>
    /// Fold spelling variants of transportation and order tokens onto one form
    /// (reliable↔HLAreliable, best_effort↔HLAbestEffort, timestamp↔TimeStamp, receive↔Receive)
    /// so a 1.3 FED and a 1516 FOM compare on meaning rather than on capitalisation.
    /// </summary>
    public bool NormalizeTransportAndOrder { get; set; } = true;

    /// <summary>Skip the MOM subtree (HLAmanager / Manager) on both sides.</summary>
    public bool IgnoreManagementObjectModel { get; set; }

    /// <summary>
    /// Drop the differences that exist only because one standard cannot express a concept, keeping
    /// the ones somebody actually authored.
    /// </summary>
    /// <remarks>
    /// Strict comparison is the default and is the honest answer to "are these two files the same?".
    /// But across standards it drowns the signal: an HLA 1.3 FED has no dataType, sharing, updateType,
    /// updateCondition, ownership, dimensions or semantics, so every attribute differs on seven
    /// properties at once. On the sample pair that is 292 format gaps against 4 authored changes.
    /// With this set, any difference the comparer tagged with a Reason is reported as equal, so what
    /// remains is what the two FOMs genuinely disagree about. The Reason text stays on the row, so
    /// nothing is hidden — it simply stops counting.
    /// </remarks>
    public bool IgnoreInexpressibleProperties { get; set; }

    /// <summary>Skip datatype, dimension and note tables entirely.</summary>
    public bool IgnoreDataTypes { get; set; }
    public bool IgnoreDimensions { get; set; }

    /// <summary>Skip the modelIdentification header block.</summary>
    public bool IgnoreIdentification { get; set; }

    /// <summary>Collapse runs of whitespace before comparing prose values.</summary>
    public bool NormalizeWhitespace { get; set; } = true;

    /// <summary>Keep unchanged nodes in the result tree so the UI can show full context.</summary>
    public bool KeepUnchanged { get; set; } = true;

    public StringComparer NameComparer =>
        IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public StringComparison NameComparison =>
        IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ComparisonOptions Clone() => (ComparisonOptions)MemberwiseClone();

    /// <summary>
    /// True when <paramref name="other"/> would produce the same comparison as this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so a screen can hold the options a result was computed under and notice when the live
    /// ones have moved off them. Comparing by value rather than tracking a dirty flag means changing
    /// a switch and changing it back leaves the result current, which matters: a re-run on a pair the
    /// size of RPR is not free, and an accidental click should not cost one.
    /// </para>
    /// <para>
    /// Every setting is checked, not only the ones a caller happens to expose. A setting left out
    /// here would let a comparison run under different rules be reported as still current — the exact
    /// failure the check exists to prevent — so <c>EverySettingIsCoveredByMatches</c> in the tests
    /// walks the properties by reflection and fails if a new one is ever added without being added
    /// here too. <see cref="NameComparer"/> and <see cref="NameComparison"/> need no line of their
    /// own; both are derived from <see cref="IgnoreCase"/>.
    /// </para>
    /// </remarks>
    public bool Matches(ComparisonOptions? other) =>
        other is not null
        && Depth == other.Depth
        && IgnoreCase == other.IgnoreCase
        && IgnoreSemantics == other.IgnoreSemantics
        && IgnoreNotes == other.IgnoreNotes
        && NormalizeRootNames == other.NormalizeRootNames
        && NormalizeTransportAndOrder == other.NormalizeTransportAndOrder
        && IgnoreManagementObjectModel == other.IgnoreManagementObjectModel
        && IgnoreInexpressibleProperties == other.IgnoreInexpressibleProperties
        && IgnoreDataTypes == other.IgnoreDataTypes
        && IgnoreDimensions == other.IgnoreDimensions
        && IgnoreIdentification == other.IgnoreIdentification
        && NormalizeWhitespace == other.NormalizeWhitespace
        && KeepUnchanged == other.KeepUnchanged;
}
