using System.Collections.Generic;

namespace HLAFomReader.Core.Model;

/// <summary>An OMT object class. Children form the publication/subscription class tree.</summary>
public sealed class FomObjectClass : FomNode
{
    /// <summary>Publish / Subscribe / PublishSubscribe / Neither. Null when the source cannot express it (HLA 1.3).</summary>
    public string? Sharing { get; set; }

    public List<FomAttribute> Attributes { get; } = new();
    public List<FomObjectClass> Children { get; } = new();

    /// <summary>Set during parsing; not serialised.</summary>
    public FomObjectClass? Parent { get; set; }

    public IEnumerable<FomObjectClass> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }
}

/// <summary>An attribute of an object class.</summary>
public sealed class FomAttribute : FomNode
{
    /// <summary>
    /// Datatype name. Null when read from a 1.3 <c>.fed</c> file, which has no datatype table;
    /// populated when read from a 1.3 OMT document, which carries the attribute table's datatypes.
    /// </summary>
    public string? DataType { get; set; }

    /// <summary>Static / Periodic / Conditional / NA.</summary>
    public string? UpdateType { get; set; }

    public string? UpdateCondition { get; set; }

    /// <summary>Divest / Acquire / DivestAcquire / NoTransfer.</summary>
    public string? Ownership { get; set; }

    public string? Sharing { get; set; }

    /// <summary>Dimension names associated with the attribute (1516). Empty for HLA 1.3.</summary>
    public List<string> Dimensions { get; } = new();

    /// <summary>Raw transportation token as written, e.g. <c>HLAreliable</c> or <c>reliable</c>.</summary>
    public string? Transportation { get; set; }

    /// <summary>Raw order token as written, e.g. <c>TimeStamp</c> or <c>timestamp</c>.</summary>
    public string? Order { get; set; }

    /// <summary>HLA 1.3 routing space bound to this attribute, when the FED declares one.</summary>
    public string? RoutingSpace { get; set; }

    /// <summary>
    /// Number of elements, from the HLA 1.3 OMT attribute table. Null for 1516 documents, which
    /// express cardinality inside the datatype rather than on the attribute.
    /// </summary>
    public string? Cardinality { get; set; }

    /// <summary>Units of measure (HLA 1.3 OMT only; 1516 carries this on the simple datatype).</summary>
    public string? Units { get; set; }

    /// <summary>Smallest distinguishable change (HLA 1.3 OMT only).</summary>
    public string? Resolution { get; set; }

    /// <summary>Accuracy of the value (HLA 1.3 OMT only).</summary>
    public string? Accuracy { get; set; }

    /// <summary>Condition under which <see cref="Accuracy"/> holds (HLA 1.3 OMT only).</summary>
    public string? AccuracyCondition { get; set; }
}

/// <summary>An OMT interaction class.</summary>
public sealed class FomInteractionClass : FomNode
{
    public string? Sharing { get; set; }
    public List<string> Dimensions { get; } = new();
    public string? Transportation { get; set; }
    public string? Order { get; set; }

    /// <summary>HLA 1.3 routing space bound to this interaction, when the FED declares one.</summary>
    public string? RoutingSpace { get; set; }

    public List<FomParameter> Parameters { get; } = new();
    public List<FomInteractionClass> Children { get; } = new();

    public FomInteractionClass? Parent { get; set; }

    public IEnumerable<FomInteractionClass> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }
}

/// <summary>A parameter of an interaction class.</summary>
public sealed class FomParameter : FomNode
{
    /// <summary>
    /// Datatype name. Null when read from a 1.3 <c>.fed</c>, which has no types; populated when read
    /// from a 1.3 OMT document, which does.
    /// </summary>
    public string? DataType { get; set; }

    /// <summary>
    /// Number of elements, from the HLA 1.3 OMT attribute table. Null for 1516 documents, which
    /// express cardinality inside the datatype rather than on the attribute.
    /// </summary>
    public string? Cardinality { get; set; }

    /// <summary>Units of measure (HLA 1.3 OMT only; 1516 carries this on the simple datatype).</summary>
    public string? Units { get; set; }

    /// <summary>Smallest distinguishable change (HLA 1.3 OMT only).</summary>
    public string? Resolution { get; set; }

    /// <summary>Accuracy of the value (HLA 1.3 OMT only).</summary>
    public string? Accuracy { get; set; }

    /// <summary>Condition under which <see cref="Accuracy"/> holds (HLA 1.3 OMT only).</summary>
    public string? AccuracyCondition { get; set; }
}

/// <summary>A normalisation/routing dimension (1516 only).</summary>
public sealed class FomDimension : FomNode
{
    public string? DataType { get; set; }
    public string? UpperBound { get; set; }
    public string? Normalization { get; set; }
    public string? Value { get; set; }
}

/// <summary>An HLA 1.3 routing space, declared in the FED <c>(spaces …)</c> block.</summary>
public sealed class FomRoutingSpace : FomNode
{
    public List<string> Dimensions { get; } = new();
}

public sealed class FomTransportation : FomNode
{
    public string? Reliable { get; set; }
}

public sealed class FomSynchronization : FomNode
{
    public string? Capability { get; set; }
    public string? DataType { get; set; }
}

public sealed class FomUpdateRate : FomNode
{
    public string? Rate { get; set; }
}

public sealed class FomSwitch : FomNode
{
    public string? IsEnabled { get; set; }
    public string? ResignSwitch { get; set; }
}

/// <summary>One of the OMT tag slots (updateReflectTag, sendReceiveTag, …).</summary>
public sealed class FomTag : FomNode
{
    public string? DataType { get; set; }
}

public sealed class FomNote : FomNode
{
    public string? Label { get; set; }
    public string? Text { get; set; }
}

/// <summary>The OMT time representation table.</summary>
public sealed class FomTime
{
    public string? TimeStampDataType { get; set; }
    public string? TimeStampSemantics { get; set; }
    public string? LookaheadDataType { get; set; }
    public string? LookaheadSemantics { get; set; }

    public bool IsEmpty =>
        TimeStampDataType is null && LookaheadDataType is null &&
        TimeStampSemantics is null && LookaheadSemantics is null;
}
