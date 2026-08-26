using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// One line of the datatype inspector's structure tree: a field, element, enumerator or alternative,
/// flattened into something a TreeView can bind to directly.
/// </summary>
/// <remarks>
/// The Core model nests a <see cref="DataTypeDetailMember"/> around a <see cref="DataTypeDetail"/>,
/// which is the right shape for reading it but a poor one for a tree: every level would need two
/// templates and the reader would see an alternating ladder of member and type. This collapses the
/// pair into one row — the member supplies the label, the type supplies everything else — so the
/// tree reads as the record layout it is describing.
/// </remarks>
public sealed class DataTypeMemberNode : ObservableObject
{
    private bool _isExpanded;

    private DataTypeMemberNode(string label, string role, DataTypeDetail? type, string? value, string? semantics)
    {
        Label = label;
        Role = role;
        Type = type;
        Value = value;
        Semantics = semantics;
    }

    /// <summary>The field, enumerator or alternative name — what the reader is looking for.</summary>
    public string Label { get; }

    /// <summary>"Field", "Element", "Enumerator" and so on, shown as a quiet badge.</summary>
    public string Role { get; }

    /// <summary>The member's own datatype. Null for an enumerator, which carries no type.</summary>
    public DataTypeDetail? Type { get; }

    /// <summary>The enumerator's literal value, or the datatype name for a typed member.</summary>
    public string? Value { get; }

    public string? Semantics { get; }
    public bool HasSemantics => !string.IsNullOrWhiteSpace(Semantics);

    public ObservableCollection<DataTypeMemberNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>The canonical encoding of this member, or an em dash when it has no type.</summary>
    public string Encoding => Type?.Canonical ?? "—";

    /// <summary>The member's range, worded for one line. Blank when nothing bounds it.</summary>
    public string RangeText => Type?.Range is { } range ? $"{range.Minimum} … {range.Maximum}" : "";

    public bool HasRange => RangeText.Length > 0;

    /// <summary>Units, resolution and accuracy, run together — the semantics beside the bounds.</summary>
    public string MeasureText
    {
        get
        {
            if (Type is null) return "";

            var parts = new List<string>(3);
            if (NotNa(Type.Units)) parts.Add(Type.Units!);
            if (NotNa(Type.Resolution)) parts.Add($"res {Type.Resolution}");
            if (NotNa(Type.Accuracy)) parts.Add($"acc {Type.Accuracy}");
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasMeasure => MeasureText.Length > 0;

    /// <summary>Set when the walk stopped here — a loop, or nesting past what the window unfolds.</summary>
    public string? Truncation => Type?.Truncation;
    public bool IsTruncated => !string.IsNullOrWhiteSpace(Truncation);

    /// <summary>
    /// "NA" is how an OMT says a column does not apply. It is an absence, and printing it as a value
    /// would fill the panel with three columns of noise on every simple type in the file.
    /// </summary>
    internal static bool NotNa(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !text.Trim().Equals("NA", StringComparison.OrdinalIgnoreCase)
        && !text.Trim().Equals("N/A", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the tree under one datatype, expanding the first couple of levels.</summary>
    public static IReadOnlyList<DataTypeMemberNode> Build(DataTypeDetail detail, int depth = 0)
    {
        if (detail.Members.Count == 0) return Array.Empty<DataTypeMemberNode>();

        var nodes = new List<DataTypeMemberNode>(detail.Members.Count);

        foreach (var member in detail.Members)
        {
            // A simple type's representation is the same bytes under another name, and showing it as
            // a child of every scalar doubles the tree for no information — the encoding column
            // already says what it resolved to. It is kept only where it is the ONLY thing to show,
            // so a bare simple type does not open onto an empty panel.
            if (member.Role == DataTypeMemberRole.Representation && detail.Members.Count > 1)
                continue;

            var node = new DataTypeMemberNode(
                label: string.IsNullOrWhiteSpace(member.Name) ? member.Value ?? "(unnamed)" : member.Name,
                role: Describe(member.Role),
                type: member.Type,
                value: member.Value,
                semantics: member.Semantics ?? member.Type?.Semantics);

            if (member.Type is not null)
                foreach (var child in Build(member.Type, depth + 1))
                    node.Children.Add(child);

            // Deep enough to show the shape at a glance, shallow enough not to unroll a whole FOM.
            node.IsExpanded = depth < 1;

            nodes.Add(node);
        }

        return nodes;
    }

    private static string Describe(DataTypeMemberRole role) => role switch
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
/// The datatype inspector: everything one FOM says about one datatype, for the reader who clicked an
/// encoding cell in the attribute map and wants to know what values it can actually carry.
/// </summary>
/// <remarks>
/// <para>
/// The encoding column answers "are these two the same bytes?". This answers the question that comes
/// straight after it — "so what can this field hold?" — which the canonical form deliberately cannot,
/// because everything that would answer it is exactly what the canonical form drops.
/// </para>
/// <para>
/// The bounds are derived rather than read: the OMT tabulates none. See <see cref="ValueRange"/> for
/// what each one rests on, which is shown beside it so a derived bound is never mistaken for an
/// authored one.
/// </para>
/// </remarks>
public sealed class DataTypeDetailViewModel
{
    public DataTypeDetailViewModel(DataTypeDetail detail, string sideLabel, string fomLabel, string attributeName)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        SideLabel = sideLabel;
        FomLabel = fomLabel;
        AttributeName = attributeName;

        Members = new ObservableCollection<DataTypeMemberNode>(DataTypeMemberNode.Build(detail));
    }

    public DataTypeDetail Detail { get; }

    /// <summary>"FOM A" or "FOM B" — which side of the map this came from.</summary>
    public string SideLabel { get; }

    /// <summary>The registered name of the FOM the definition was read from.</summary>
    public string FomLabel { get; }

    /// <summary>The attribute whose cell was clicked, for the line under the title.</summary>
    public string AttributeName { get; }

    public ObservableCollection<DataTypeMemberNode> Members { get; }

    public string Title => Detail.Name;
    public string WindowTitle => $"{Detail.Name} — {SideLabel}";

    public string Subtitle => $"{AttributeName}  ·  {SideLabel}  ·  {FomLabel}";

    public string TableLabel => Detail.TableLabel;
    public string SourceLabel => Detail.SourceLabel;
    public string Canonical => Detail.Canonical;

    public string WidthText => Detail.Bits is { } bits
        ? $"{bits} bit{(bits == 1 ? "" : "s")}"
        : "—";

    public string EndianText => Detail.Endian switch
    {
        "BE" => "Big endian",
        "LE" => "Little endian",
        _ => "Not stated",
    };

    // ---- the value range ------------------------------------------------------------------

    public bool HasRange => Detail.Range is not null;

    public string RangeMinimum => Detail.Range?.Minimum ?? "—";
    public string RangeMaximum => Detail.Range?.Maximum ?? "—";

    /// <summary>What the bounds rest on. Always shown: a derived bound must never read as an authored one.</summary>
    public string RangeBasis => Detail.Range?.Basis ?? "";

    public string RangeNote => Detail.Range?.Note ?? "";
    public bool HasRangeNote => RangeNote.Length > 0;

    /// <summary>
    /// Why there is no range, when there is none. A record has no single span; each field has its own,
    /// and they are in the tree below.
    /// </summary>
    public string NoRangeReason => Detail.Shape switch
    {
        DataTypeShape.Record => "A record carries no single value. Each field bounds itself — see the structure below.",
        DataTypeShape.Variant => "A variant carries whichever alternative its discriminant selects. Each has its own bounds below.",
        DataTypeShape.Unknown => "The encoding could not be established from this FOM, so nothing bounds this value.",
        _ => "This FOM states no width for the type, so no range can be derived from it.",
    };

    // ---- the semantics column -------------------------------------------------------------

    public bool HasUnits => DataTypeMemberNode.NotNa(Detail.Units);
    public bool HasResolution => DataTypeMemberNode.NotNa(Detail.Resolution);
    public bool HasAccuracy => DataTypeMemberNode.NotNa(Detail.Accuracy);
    public bool HasInterpretation => DataTypeMemberNode.NotNa(Detail.Interpretation);
    public bool HasEncodingNote => DataTypeMemberNode.NotNa(Detail.Encoding);
    public bool HasCardinality => DataTypeMemberNode.NotNa(Detail.Cardinality);
    public bool HasRepresentation => DataTypeMemberNode.NotNa(Detail.Representation);
    public bool HasSemantics => DataTypeMemberNode.NotNa(Detail.Semantics);

    public string Units => Detail.Units ?? "";
    public string Resolution => Detail.Resolution ?? "";
    public string Accuracy => Detail.Accuracy ?? "";
    public string Interpretation => Detail.Interpretation ?? "";
    public string EncodingNote => Detail.Encoding ?? "";
    public string Cardinality => Detail.Cardinality ?? "";
    public string Representation => Detail.Representation ?? "";
    public string Semantics => Detail.Semantics ?? "";

    /// <summary>True when at least one of the authored columns has something in it.</summary>
    public bool HasAnyProperty =>
        HasUnits || HasResolution || HasAccuracy || HasInterpretation
        || HasEncodingNote || HasCardinality || HasRepresentation;

    public bool HasMembers => Members.Count > 0;

    public string MembersHeader => Detail.Shape switch
    {
        DataTypeShape.Record => "Fields",
        DataTypeShape.Variant => "Discriminant and alternatives",
        DataTypeShape.Enumerated => "Declared values",
        DataTypeShape.Array => "Element",
        _ => "Structure",
    };

    /// <summary>Set when the walk stopped short. Shown as a caveat rather than left implicit.</summary>
    public string Truncation => Detail.Truncation ?? "";
    public bool IsTruncated => Truncation.Length > 0;
}
