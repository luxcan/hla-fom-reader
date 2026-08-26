using System.Collections.Generic;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Merging;

/// <summary>
/// The outcome of combining an HLA 1.3 FED with its OMT document.
/// </summary>
/// <remarks>
/// Neither 1.3 file is complete on its own. The <c>.fed</c> is what the RTI loads and is authoritative
/// for structure, transportation, order and routing spaces; it has no types at all. The <c>.omt</c>
/// is the documentation artefact the RTI never reads, and it is the only place datatypes, sharing,
/// ownership, units and descriptions exist. Registering them together is the only way to get a
/// complete 1.3 model.
/// </remarks>
public sealed class FomMergeResult
{
    public required FomDocument Document { get; init; }

    /// <summary>Classes, attributes, interactions and parameters the OMT enriched.</summary>
    public int EnrichedClassCount { get; set; }
    public int EnrichedAttributeCount { get; set; }
    public int EnrichedInteractionCount { get; set; }
    public int EnrichedParameterCount { get; set; }

    /// <summary>Elements in the FED that the OMT said nothing about.</summary>
    public List<string> UnmatchedInFed { get; } = new();

    /// <summary>
    /// Elements the OMT describes that do not exist in the FED. These are the interesting ones: the
    /// two files are supposed to describe the same federation, so a mismatch usually means they have
    /// drifted apart and the pair should not be trusted blindly.
    /// </summary>
    public List<string> UnmatchedInOmt { get; } = new();

    public bool HasMismatches => UnmatchedInFed.Count > 0 || UnmatchedInOmt.Count > 0;

    /// <summary>One-line summary for the registration confirmation.</summary>
    public string Summary =>
        $"{EnrichedAttributeCount} attributes typed from the OMT, " +
        $"{Document.DataTypeCount} datatypes added" +
        (HasMismatches
            ? $"; {UnmatchedInFed.Count + UnmatchedInOmt.Count} elements did not line up"
            : "");
}
