using System.Collections.Generic;
using System.Linq;

namespace HLAFomReader.Core.Model;

/// <summary>The six OMT datatype tables. Always empty for HLA 1.3, which has no datatype model.</summary>
public sealed class FomDataTypeTables
{
    public List<BasicDataType> BasicDataRepresentations { get; } = new();
    public List<SimpleDataType> SimpleDataTypes { get; } = new();
    public List<EnumeratedDataType> EnumeratedDataTypes { get; } = new();
    public List<ArrayDataType> ArrayDataTypes { get; } = new();
    public List<FixedRecordDataType> FixedRecordDataTypes { get; } = new();
    public List<VariantRecordDataType> VariantRecordDataTypes { get; } = new();

    public int TotalCount =>
        BasicDataRepresentations.Count + SimpleDataTypes.Count + EnumeratedDataTypes.Count +
        ArrayDataTypes.Count + FixedRecordDataTypes.Count + VariantRecordDataTypes.Count;

    public bool IsEmpty => TotalCount == 0;

    public IEnumerable<FomNode> AllDataTypes() =>
        BasicDataRepresentations.Cast<FomNode>()
            .Concat(SimpleDataTypes)
            .Concat(EnumeratedDataTypes)
            .Concat(ArrayDataTypes)
            .Concat(FixedRecordDataTypes)
            .Concat(VariantRecordDataTypes);
}

public sealed class BasicDataType : FomNode
{
    public string? Size { get; set; }
    public string? Interpretation { get; set; }
    public string? Endian { get; set; }
    public string? Encoding { get; set; }
}

public sealed class SimpleDataType : FomNode
{
    public string? Representation { get; set; }
    public string? Units { get; set; }
    public string? Resolution { get; set; }
    public string? Accuracy { get; set; }
}

public sealed class EnumeratedDataType : FomNode
{
    public string? Representation { get; set; }
    public List<EnumeratorValue> Enumerators { get; } = new();
}

public sealed class EnumeratorValue : FomNode
{
    /// <summary>One or more literal values, joined with ", " when the source lists several.</summary>
    public string? Values { get; set; }
}

public sealed class ArrayDataType : FomNode
{
    public string? DataType { get; set; }
    public string? Cardinality { get; set; }
    public string? Encoding { get; set; }
}

public sealed class FixedRecordDataType : FomNode
{
    public string? Encoding { get; set; }
    public string? Include { get; set; }
    public List<RecordField> Fields { get; } = new();
}

public sealed class RecordField : FomNode
{
    public string? DataType { get; set; }
}

public sealed class VariantRecordDataType : FomNode
{
    public string? Discriminant { get; set; }
    public string? DataType { get; set; }
    public string? Encoding { get; set; }
    public List<VariantAlternative> Alternatives { get; } = new();
}

public sealed class VariantAlternative : FomNode
{
    public string? Enumerator { get; set; }
    public string? DataType { get; set; }
}
