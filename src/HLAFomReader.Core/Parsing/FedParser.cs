using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Parsing;

/// <summary>
/// Reads an HLA 1.3 Federation Execution Data (<c>.fed</c>) file into a <see cref="FomDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The grammar understood is the classic DoD 1.3 one:
/// <c>(FED (Federation …) (FEDversion …) (spaces …) (objects …) (interactions …))</c>.
/// Element keywords are matched case-insensitively; names are taken exactly as written.
/// </para>
/// <para>
/// FED cannot express datatypes, dimensions, sharing or ownership, so the corresponding parts of the
/// normalised model are deliberately left null or empty — the comparer, not the parser, decides
/// whether that counts as a difference against a 1516 document.
/// </para>
/// <para>
/// Malformed content is never thrown: every problem becomes a <see cref="ParseDiagnostic"/> carrying
/// the source line, and whatever else could be understood is still returned.
/// </para>
/// </remarks>
public sealed class FedParser : IFomParser
{
    /// <summary>Guard against pathological nesting in a malformed file; real FED trees are far shallower.</summary>
    private const int MaxClassDepth = 64;

    /// <inheritdoc />
    public FomStandard Standard => FomStandard.Hla13;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public FomDocument Parse(TextReader reader, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var doc = new FomDocument
        {
            Standard = FomStandard.Hla13,
            SourcePath = sourcePath,
            SourceNamespace = null,
        };
        doc.Identification.Type = "FED";

        var source = SExpressionReader.Parse(reader);

        // Tokenising problems (stray parens, unterminated strings) are warnings: the content that
        // could be read is still usable.
        foreach (var problem in source.Problems)
            Add(doc, DiagnosticSeverity.Warning, problem.Message, problem.Line);

        // The FED element proper. Bare atoms outside any list are ignored when locating it, so that
        // stray text before the document still lets us find the real body.
        var first = source.Expressions.FirstOrDefault(e => !e.IsAtom);
        var fed = source.Expressions.FirstOrDefault(e => !e.IsAtom && e.HasHead("FED"));

        if (fed is null || !ReferenceEquals(fed, first))
        {
            Add(doc, DiagnosticSeverity.Error,
                "The file does not begin with a '(FED …)' element; any recognised blocks are parsed anyway.",
                first?.Line ?? source.Expressions.FirstOrDefault()?.Line);
        }

        // Fall back to the top level when there is no FED wrapper, so a fragment still yields content.
        var body = fed is not null ? fed.Children : source.Expressions.Where(e => !e.IsAtom).ToList();
        var bodyLine = fed?.Line;

        var sawObjects = false;
        var sawInteractions = false;

        foreach (var element in body)
        {
            if (element.HasHead("Federation"))
            {
                ReadFederationName(doc, element);
            }
            else if (element.HasHead("FEDversion"))
            {
                ReadFedVersion(doc, element);
            }
            else if (element.HasHead("spaces"))
            {
                ReadSpaces(doc, element);
            }
            else if (element.HasHead("objects"))
            {
                sawObjects = true;
                ReadObjects(doc, element);
            }
            else if (element.HasHead("interactions"))
            {
                sawInteractions = true;
                ReadInteractions(doc, element);
            }
            else
            {
                ReportUnrecognised(doc, element, "FED");
            }
        }

        if (!sawObjects)
            Add(doc, DiagnosticSeverity.Warning, "The FED declares no '(objects …)' block.", bodyLine, "FED");

        if (!sawInteractions)
            Add(doc, DiagnosticSeverity.Warning, "The FED declares no '(interactions …)' block.", bodyLine, "FED");

        return doc;
    }

    // ---------------------------------------------------------------- header

    /// <summary>Maps <c>(Federation &lt;name&gt;)</c> onto the model identification name.</summary>
    private static void ReadFederationName(FomDocument doc, SExpression element)
    {
        var name = JoinAtoms(element);
        if (name is null)
        {
            Add(doc, DiagnosticSeverity.Warning, "'(Federation …)' declares no federation name.", element.Line, "FED/Federation");
            return;
        }

        doc.Identification.Name = name;
    }

    /// <summary>Maps <c>(FEDversion &lt;version&gt;)</c> onto the model identification version.</summary>
    private static void ReadFedVersion(FomDocument doc, SExpression element)
    {
        var version = JoinAtoms(element);
        if (version is null)
        {
            Add(doc, DiagnosticSeverity.Warning, "'(FEDversion …)' declares no version.", element.Line, "FED/FEDversion");
            return;
        }

        doc.Identification.Version = version;
        doc.Identification.Type = "FED";
    }

    // ---------------------------------------------------------------- spaces

    /// <summary>
    /// Reads the <c>(spaces …)</c> block into <see cref="FomDocument.RoutingSpaces"/>.
    /// <see cref="FomDocument.Dimensions"/> stays empty: 1.3 routing-space dimensions are plain
    /// names, not the normalisation dimensions of 1516.
    /// </summary>
    private static void ReadSpaces(FomDocument doc, SExpression spaces)
    {
        foreach (var element in spaces.Children)
        {
            if (!element.HasHead("space"))
            {
                ReportUnrecognised(doc, element, "FED/spaces");
                continue;
            }

            var name = element.Atom(0);
            if (string.IsNullOrEmpty(name))
            {
                Add(doc, DiagnosticSeverity.Warning, "'(space …)' declares no name; skipped.", element.Line, "FED/spaces");
                continue;
            }

            var space = new FomRoutingSpace { Name = name, QualifiedName = name };

            foreach (var child in element.Children)
            {
                if (!child.HasHead("dimension"))
                {
                    ReportUnrecognised(doc, child, $"FED/spaces/space[{name}]");
                    continue;
                }

                var dimension = child.Atom(0);
                if (string.IsNullOrEmpty(dimension))
                {
                    Add(doc, DiagnosticSeverity.Warning,
                        $"'(dimension …)' in space '{name}' declares no name; skipped.",
                        child.Line, $"FED/spaces/space[{name}]");
                    continue;
                }

                space.Dimensions.Add(dimension);
            }

            doc.RoutingSpaces.Add(space);
        }
    }

    // --------------------------------------------------------------- objects

    /// <summary>Reads the <c>(objects …)</c> block into the object class tree.</summary>
    private static void ReadObjects(FomDocument doc, SExpression objects)
    {
        foreach (var element in objects.Children)
        {
            if (!element.HasHead("class"))
            {
                ReportUnrecognised(doc, element, "FED/objects");
                continue;
            }

            var root = ReadObjectClass(doc, element, parent: null, parentQualifiedName: null, depth: 1);
            if (root is not null)
                doc.ObjectClasses.Add(root);
        }
    }

    /// <summary>
    /// Reads one <c>(class …)</c> element and its subtree. Returns null when the class has no name
    /// and therefore cannot be placed in the tree.
    /// </summary>
    private static FomObjectClass? ReadObjectClass(
        FomDocument doc, SExpression element, FomObjectClass? parent, string? parentQualifiedName, int depth)
    {
        var name = element.Atom(0);
        if (string.IsNullOrEmpty(name))
        {
            Add(doc, DiagnosticSeverity.Warning, "Object '(class …)' declares no name; skipped with its contents.",
                element.Line, NodePath("objects", parentQualifiedName));
            return null;
        }

        var qualifiedName = parentQualifiedName is null ? name : $"{parentQualifiedName}.{name}";

        var objectClass = new FomObjectClass
        {
            Name = name,
            QualifiedName = qualifiedName,
            Parent = parent,
            // Sharing stays null: HLA 1.3 has no publish/subscribe declaration in the FED.
        };

        if (depth >= MaxClassDepth)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Object class '{qualifiedName}' is nested deeper than {MaxClassDepth} levels; its children were not read.",
                element.Line, NodePath("objects", qualifiedName));
            return objectClass;
        }

        foreach (var child in element.Children)
        {
            if (child.HasHead("attribute"))
            {
                var attribute = ReadAttribute(doc, child, objectClass);
                if (attribute is not null)
                    objectClass.Attributes.Add(attribute);
            }
            else if (child.HasHead("class"))
            {
                var nested = ReadObjectClass(doc, child, objectClass, qualifiedName, depth + 1);
                if (nested is not null)
                    objectClass.Children.Add(nested);
            }
            else
            {
                ReportUnrecognised(doc, child, NodePath("objects", qualifiedName));
            }
        }

        return objectClass;
    }

    /// <summary>
    /// Reads <c>(attribute &lt;name&gt; &lt;transportation&gt; &lt;order&gt; [&lt;routingSpace&gt;])</c>.
    /// Some tools emit fewer tokens; the missing ones stay null and a warning is raised.
    /// </summary>
    private static FomAttribute? ReadAttribute(FomDocument doc, SExpression element, FomObjectClass owner)
    {
        var path = NodePath("objects", owner.QualifiedName);
        var name = element.Atom(0);

        if (string.IsNullOrEmpty(name))
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"'(attribute …)' in class '{owner.QualifiedName}' declares no name; skipped.",
                element.Line, path);
            return null;
        }

        var count = element.Atoms.Count;
        if (count < 3)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Attribute '{owner.QualifiedName}.{name}' declares {count} token(s); " +
                "expected name, transportation and order. The missing values are left undefined.",
                element.Line, path);
        }
        else if (count > 4)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Attribute '{owner.QualifiedName}.{name}' declares {count} tokens; " +
                "only name, transportation, order and routing space were read.",
                element.Line, path);
        }

        return new FomAttribute
        {
            Name = name,
            QualifiedName = $"{owner.QualifiedName}.{name}",
            Transportation = NullIfEmpty(element.Atom(1)),
            Order = NullIfEmpty(element.Atom(2)),
            RoutingSpace = NullIfEmpty(element.Atom(3)),
            // DataType, UpdateType, UpdateCondition, Ownership, Sharing and Dimensions stay
            // null/empty: HLA 1.3 cannot express any of them.
        };
    }

    // ---------------------------------------------------------- interactions

    /// <summary>Reads the <c>(interactions …)</c> block into the interaction class tree.</summary>
    private static void ReadInteractions(FomDocument doc, SExpression interactions)
    {
        foreach (var element in interactions.Children)
        {
            if (!element.HasHead("class"))
            {
                ReportUnrecognised(doc, element, "FED/interactions");
                continue;
            }

            var root = ReadInteractionClass(doc, element, parent: null, parentQualifiedName: null, depth: 1);
            if (root is not null)
                doc.InteractionClasses.Add(root);
        }
    }

    /// <summary>
    /// Reads one <c>(class &lt;name&gt; &lt;transportation&gt; &lt;order&gt; [&lt;routingSpace&gt;] …)</c>
    /// element and its subtree. Returns null when the class has no name.
    /// </summary>
    private static FomInteractionClass? ReadInteractionClass(
        FomDocument doc, SExpression element, FomInteractionClass? parent, string? parentQualifiedName, int depth)
    {
        var name = element.Atom(0);
        if (string.IsNullOrEmpty(name))
        {
            Add(doc, DiagnosticSeverity.Warning, "Interaction '(class …)' declares no name; skipped with its contents.",
                element.Line, NodePath("interactions", parentQualifiedName));
            return null;
        }

        var qualifiedName = parentQualifiedName is null ? name : $"{parentQualifiedName}.{name}";
        var path = NodePath("interactions", qualifiedName);

        var count = element.Atoms.Count;
        if (count < 3)
        {
            // Some tools write '(class Name)' alone and carry transportation/order elsewhere.
            Add(doc, DiagnosticSeverity.Warning,
                $"Interaction class '{qualifiedName}' declares {count} token(s); " +
                "expected name, transportation and order. The missing values are left undefined.",
                element.Line, path);
        }
        else if (count > 4)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Interaction class '{qualifiedName}' declares {count} tokens; " +
                "only name, transportation, order and routing space were read.",
                element.Line, path);
        }

        var interaction = new FomInteractionClass
        {
            Name = name,
            QualifiedName = qualifiedName,
            Parent = parent,
            Transportation = NullIfEmpty(element.Atom(1)),
            Order = NullIfEmpty(element.Atom(2)),
            RoutingSpace = NullIfEmpty(element.Atom(3)),
            // Sharing stays null and Dimensions stay empty: HLA 1.3 cannot express them.
        };

        if (depth >= MaxClassDepth)
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"Interaction class '{qualifiedName}' is nested deeper than {MaxClassDepth} levels; its children were not read.",
                element.Line, path);
            return interaction;
        }

        foreach (var child in element.Children)
        {
            if (child.HasHead("parameter"))
            {
                var parameter = ReadParameter(doc, child, interaction);
                if (parameter is not null)
                    interaction.Parameters.Add(parameter);
            }
            else if (child.HasHead("class"))
            {
                var nested = ReadInteractionClass(doc, child, interaction, qualifiedName, depth + 1);
                if (nested is not null)
                    interaction.Children.Add(nested);
            }
            else
            {
                ReportUnrecognised(doc, child, path);
            }
        }

        return interaction;
    }

    /// <summary>
    /// Reads <c>(parameter &lt;name&gt;)</c>. Any further tokens are a dialect that repeats the
    /// transportation and order per parameter; the model has nowhere to keep them, so they are ignored.
    /// </summary>
    private static FomParameter? ReadParameter(FomDocument doc, SExpression element, FomInteractionClass owner)
    {
        var name = element.Atom(0);
        if (string.IsNullOrEmpty(name))
        {
            Add(doc, DiagnosticSeverity.Warning,
                $"'(parameter …)' in interaction '{owner.QualifiedName}' declares no name; skipped.",
                element.Line, NodePath("interactions", owner.QualifiedName));
            return null;
        }

        return new FomParameter
        {
            Name = name,
            QualifiedName = $"{owner.QualifiedName}.{name}",
            DataType = null, // HLA 1.3 has no datatype table.
        };
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Reports an element the FED grammar does not define, without stopping the parse.</summary>
    private static void ReportUnrecognised(FomDocument doc, SExpression element, string? path)
    {
        var head = element.Head
                   ?? (element.IsAtom ? element.Atom(0) : null)
                   ?? "";

        Add(doc, DiagnosticSeverity.Info, $"Unrecognised FED element '{head}'", element.Line, path);
    }

    private static void Add(FomDocument doc, DiagnosticSeverity severity, string message, int? line, string? path = null) =>
        doc.Diagnostics.Add(new ParseDiagnostic(severity, message, line, path));

    /// <summary>Builds a diagnostic path such as <c>objects/class[ObjectRoot.Aircraft]</c>.</summary>
    private static string NodePath(string block, string? qualifiedName) =>
        string.IsNullOrEmpty(qualifiedName) ? block : $"{block}/class[{qualifiedName}]";

    /// <summary>
    /// Joins the atoms of a single-value element with spaces, so an unquoted multi-word
    /// federation name still survives. Returns null when the element carries no value.
    /// </summary>
    private static string? JoinAtoms(SExpression element)
    {
        if (element.Atoms.Count == 0)
            return null;
        if (element.Atoms.Count == 1)
            return NullIfEmpty(element.Atoms[0]);

        var joined = string.Join(' ', element.Atoms).Trim();
        return NullIfEmpty(joined);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
