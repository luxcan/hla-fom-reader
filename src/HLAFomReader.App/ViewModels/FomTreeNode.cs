using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Model;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// A node in the "what did we actually parse" tree on the Registry screen. Built once from a
/// <see cref="FomDocument"/> after it is loaded back out of SQLite.
/// </summary>
public sealed class FomTreeNode : ObservableObject
{
    private bool _isExpanded;

    public FomTreeNode(string name, string kind, string? detail = null)
    {
        Name = name;
        Kind = kind;
        Detail = detail;
    }

    public string Name { get; }

    /// <summary>Short category label shown as a muted chip, e.g. "class", "attribute", "datatype".</summary>
    public string Kind { get; }

    /// <summary>Trailing summary, e.g. the datatype and transportation of an attribute.</summary>
    public string? Detail { get; }


    public ObservableCollection<FomTreeNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool HasChildren => Children.Count > 0;

    private FomTreeNode Add(FomTreeNode child)
    {
        Children.Add(child);
        return child;
    }

    /// <summary>Builds the whole structure tree for a parsed document.</summary>
    public static IReadOnlyList<FomTreeNode> Build(FomDocument document)
    {
        var roots = new List<FomTreeNode>();

        if (document.ObjectClasses.Count > 0)
        {
            var section = new FomTreeNode("Object classes", "section",
                $"{document.ObjectClassCount} classes · {document.AttributeCount} attributes") { IsExpanded = true };
            foreach (var objectClass in document.ObjectClasses)
                section.Add(BuildObjectClass(objectClass));
            roots.Add(section);
        }

        if (document.InteractionClasses.Count > 0)
        {
            var section = new FomTreeNode("Interaction classes", "section",
                $"{document.InteractionClassCount} classes · {document.ParameterCount} parameters") { IsExpanded = true };
            foreach (var interaction in document.InteractionClasses)
                section.Add(BuildInteractionClass(interaction));
            roots.Add(section);
        }

        if (!document.DataTypes.IsEmpty)
        {
            var section = new FomTreeNode("Datatypes", "section", $"{document.DataTypeCount} total");
            AddDataTypeGroup(section, "Basic data representations",
                document.DataTypes.BasicDataRepresentations.Select(d => (d.Name, Describe(d.Size, d.Interpretation))));
            AddDataTypeGroup(section, "Simple datatypes",
                document.DataTypes.SimpleDataTypes.Select(d => (d.Name, Describe(d.Representation, d.Units))));
            AddDataTypeGroup(section, "Enumerated datatypes",
                document.DataTypes.EnumeratedDataTypes.Select(d => (d.Name, Describe(d.Representation, $"{d.Enumerators.Count} enumerators"))));
            AddDataTypeGroup(section, "Array datatypes",
                document.DataTypes.ArrayDataTypes.Select(d => (d.Name, Describe(d.DataType, d.Cardinality))));
            AddDataTypeGroup(section, "Fixed record datatypes",
                document.DataTypes.FixedRecordDataTypes.Select(d => (d.Name, Describe($"{d.Fields.Count} fields", d.Encoding))));
            AddDataTypeGroup(section, "Variant record datatypes",
                document.DataTypes.VariantRecordDataTypes.Select(d => (d.Name, Describe(d.Discriminant, $"{d.Alternatives.Count} alternatives"))));
            roots.Add(section);
        }

        AddFlatSection(roots, "Dimensions", "dimension",
            document.Dimensions.Select(d => (d.Name, Describe(d.DataType, d.UpperBound))));
        AddFlatSection(roots, "Routing spaces", "space",
            document.RoutingSpaces.Select(s => (s.Name, (string?)string.Join(", ", s.Dimensions))));
        AddFlatSection(roots, "Transportations", "transportation",
            document.Transportations.Select(t => (t.Name, Describe(t.Reliable, null))));
        AddFlatSection(roots, "Synchronizations", "synchronization",
            document.Synchronizations.Select(s => (s.Name, Describe(s.Capability, s.DataType))));
        AddFlatSection(roots, "Update rates", "updateRate",
            document.UpdateRates.Select(u => (u.Name, Describe(u.Rate, null))));
        AddFlatSection(roots, "Switches", "switch",
            document.Switches.Select(s => (s.Name, Describe(s.IsEnabled, s.ResignSwitch))));
        AddFlatSection(roots, "Tags", "tag",
            document.Tags.Select(t => (t.Name, Describe(t.DataType, null))));
        AddFlatSection(roots, "Notes", "note",
            document.Notes.Select(n => (n.Name, Describe(n.Text, null))));

        if (!document.Time.IsEmpty)
        {
            var section = new FomTreeNode("Time representation", "section");
            section.Add(new FomTreeNode("timeStamp", "time", document.Time.TimeStampDataType));
            section.Add(new FomTreeNode("lookahead", "time", document.Time.LookaheadDataType));
            roots.Add(section);
        }

        return roots;
    }

    private static FomTreeNode BuildObjectClass(FomObjectClass objectClass)
    {
        var detail = objectClass.Sharing is { Length: > 0 } ? objectClass.Sharing : null;
        var node = new FomTreeNode(objectClass.Name, "class", detail);

        foreach (var attribute in objectClass.Attributes)
        {
            var parts = new[] { attribute.DataType, attribute.Transportation, attribute.Order }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            node.Add(new FomTreeNode(attribute.Name, "attribute", string.Join(" · ", parts)));
        }

        foreach (var child in objectClass.Children)
            node.Add(BuildObjectClass(child));

        return node;
    }

    private static FomTreeNode BuildInteractionClass(FomInteractionClass interaction)
    {
        var parts = new[] { interaction.Sharing, interaction.Transportation, interaction.Order }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var node = new FomTreeNode(interaction.Name, "interaction", string.Join(" · ", parts));

        foreach (var parameter in interaction.Parameters)
            node.Add(new FomTreeNode(parameter.Name, "parameter", parameter.DataType));

        foreach (var child in interaction.Children)
            node.Add(BuildInteractionClass(child));

        return node;
    }

    private static void AddDataTypeGroup(FomTreeNode parent, string title,
        IEnumerable<(string Name, string? Detail)> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var group = parent.Add(new FomTreeNode(title, "group", $"{list.Count}"));
        foreach (var (name, detail) in list)
            group.Add(new FomTreeNode(name, "datatype", detail));
    }

    private static void AddFlatSection(List<FomTreeNode> roots, string title, string kind,
        IEnumerable<(string Name, string? Detail)> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var section = new FomTreeNode(title, "section", $"{list.Count}");
        foreach (var (name, detail) in list)
            section.Add(new FomTreeNode(name, kind, detail));

        roots.Add(section);
    }

    private static string? Describe(string? first, string? second)
    {
        var parts = new[] { first, second }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }
}
