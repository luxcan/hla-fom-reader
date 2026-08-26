using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Model;

/// <summary>
/// A single registered FOM/FED file, normalised into one shape regardless of which
/// HLA standard it came from. Concepts a standard cannot express stay null/empty —
/// the comparer decides whether that counts as a difference.
/// </summary>
public sealed class FomDocument
{
    public FomStandard Standard { get; set; } = FomStandard.Unknown;

    /// <summary>Absolute path of the file this document was read from.</summary>
    public string? SourcePath { get; set; }

    /// <summary>XML namespace URI of the document root, for 1516 files.</summary>
    public string? SourceNamespace { get; set; }

    public ModelIdentification Identification { get; set; } = new();

    /// <summary>Root object classes (normally the single <c>HLAobjectRoot</c> / <c>ObjectRoot</c>).</summary>
    public List<FomObjectClass> ObjectClasses { get; } = new();

    /// <summary>Root interaction classes (normally the single <c>HLAinteractionRoot</c> / <c>InteractionRoot</c>).</summary>
    public List<FomInteractionClass> InteractionClasses { get; } = new();

    public FomDataTypeTables DataTypes { get; } = new();
    public List<FomDimension> Dimensions { get; } = new();
    public List<FomTransportation> Transportations { get; } = new();
    public List<FomSynchronization> Synchronizations { get; } = new();
    public List<FomUpdateRate> UpdateRates { get; } = new();
    public List<FomSwitch> Switches { get; } = new();
    public List<FomTag> Tags { get; } = new();
    public List<FomNote> Notes { get; } = new();
    public FomTime Time { get; set; } = new();

    /// <summary>HLA 1.3 routing spaces. Empty for 1516 documents, which use dimensions instead.</summary>
    public List<FomRoutingSpace> RoutingSpaces { get; } = new();

    public List<ParseDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<FomObjectClass> AllObjectClasses() =>
        ObjectClasses.SelectMany(c => c.DescendantsAndSelf());

    public IEnumerable<FomInteractionClass> AllInteractionClasses() =>
        InteractionClasses.SelectMany(c => c.DescendantsAndSelf());

    public int ObjectClassCount => AllObjectClasses().Count();
    public int AttributeCount => AllObjectClasses().Sum(c => c.Attributes.Count);
    public int InteractionClassCount => AllInteractionClasses().Count();
    public int ParameterCount => AllInteractionClasses().Sum(c => c.Parameters.Count);
    public int DataTypeCount => DataTypes.TotalCount;
    public int DimensionCount => Dimensions.Count;

    /// <summary>Human label for the standard, used in the UI and in reports.</summary>
    public string StandardDisplayName => Standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "IEEE 1516-2000",
        FomStandard.Ieee1516_2010 => "IEEE 1516-2010 (Evolved)",
        FomStandard.Ieee1516_2025 => "IEEE 1516-2025",
        _ => "Unknown",
    };

    /// <summary>True when this document can carry a datatype table.</summary>
    /// <remarks>
    /// Keyed off content rather than off <see cref="Standard"/>, because "HLA 1.3" is not one answer:
    /// a <c>.fed</c> has no datatypes at all, while a 1.3 OMT document carries the full attribute
    /// table. Asking the document what it holds is the only reading that is true for both.
    /// </remarks>
    public bool SupportsDataTypes =>
        !DataTypes.IsEmpty || (Standard != FomStandard.Hla13 && Standard != FomStandard.Unknown);

    /// <summary>True when this document can carry normalisation dimensions.</summary>
    public bool SupportsDimensions =>
        Dimensions.Count > 0 || (Standard != FomStandard.Hla13 && Standard != FomStandard.Unknown);
}
