using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Parsing;

/// <summary>
/// Reads an IEEE 1516 OMT DIF XML document — 1516-2000, 1516-2010 ("HLA Evolved") and
/// 1516-2025 — into the normalised <see cref="FomDocument"/>.
/// </summary>
/// <remarks>
/// The reader is deliberately forgiving, because the DIF is serialised very differently by the
/// tools in the field:
/// <list type="bullet">
/// <item><description>
/// Elements are matched on their <em>local name</em> only, so a document works whether it declares
/// <c>http://standards.ieee.org/IEEE1516-2010</c>, one of the other OMT namespaces, a prefix, or no
/// namespace at all. No namespace URI is ever hard-coded into a lookup.
/// </description></item>
/// <item><description>
/// Every scalar is read through <see cref="Value(XElement,string)"/>, which accepts the property
/// either as an XML attribute (<c>&lt;objectClass name="Aircraft"&gt;</c>) or as a child element
/// (<c>&lt;objectClass&gt;&lt;name&gt;Aircraft&lt;/name&gt;</c>). Both spellings occur in the wild.
/// </description></item>
/// <item><description>
/// Name matching is case-insensitive, which costs nothing here (the OMT vocabulary has no pairs that
/// differ only by case) and tolerates generators that alter capitalisation.
/// </description></item>
/// </list>
/// Content problems become <see cref="ParseDiagnostic"/> entries; only a null <c>reader</c> throws.
/// </remarks>
public sealed class Ieee1516XmlParser : IFomParser
{
    /// <summary>Comparison used for every element, attribute and namespace name test.</summary>
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>Placeholder used when a named element carries no usable name.</summary>
    private const string UnnamedName = "(unnamed)";

    private readonly FomStandard _standard;

    /// <summary>
    /// Creates the parser.
    /// </summary>
    /// <param name="standard">
    /// The standard to report when the document's root namespace does not identify one. The
    /// namespace is always preferred; this value is only the fallback and the value returned by
    /// <see cref="Standard"/>.
    /// </param>
    public Ieee1516XmlParser(FomStandard standard = FomStandard.Ieee1516_2010)
    {
        _standard = standard;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the configured fallback. The <see cref="FomDocument.Standard"/> of a parsed document
    /// may differ from it, because the root namespace is detected per file.
    /// </remarks>
    public FomStandard Standard => _standard;

    /// <inheritdoc />
    public FomDocument Parse(TextReader reader, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        XDocument xml;
        try
        {
            xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return Failed(sourcePath, $"The file is not well-formed XML: {ex.Message}",
                ex.LineNumber > 0 ? ex.LineNumber : null);
        }
        catch (Exception ex)
        {
            // A broken stream, a decoding failure or a prohibited DTD must not escape Parse.
            return Failed(sourcePath, $"The file could not be read: {ex.Message}", null);
        }

        var doc = new FomDocument { SourcePath = sourcePath, Standard = _standard };

        var root = xml.Root;
        if (root is null)
        {
            doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error,
                "The document has no root element.", null, sourcePath));
            return doc;
        }

        var namespaceUri = root.Name.NamespaceName;
        doc.SourceNamespace = Norm(namespaceUri);
        doc.Standard = DetectStandard(namespaceUri, root, doc);

        // The OMT root is <objectModel>. Anything else is an error, but the sections are still
        // worth hunting for: some tools wrap the model in an envelope element.
        var contentRoot = root;
        var deepSearch = false;
        if (!IsNamed(root, "objectModel"))
        {
            doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error,
                $"Expected a root element named 'objectModel' but found '{root.Name.LocalName}'; " +
                "the OMT sections will be searched for anywhere in the document.",
                Line(root), root.Name.LocalName));

            var nested = root.Descendants().FirstOrDefault(e => IsNamed(e, "objectModel"));
            if (nested is not null)
                contentRoot = nested;
            else
                deepSearch = true;
        }

        var rootPath = contentRoot.Name.LocalName;

        XElement? Section(string localName) => deepSearch
            ? contentRoot.Descendants().FirstOrDefault(e => IsNamed(e, localName))
            : Element(contentRoot, localName);

        ReadModelIdentification(Section("modelIdentification"), doc, rootPath);
        ReadObjects(Section("objects"), doc, rootPath);
        ReadInteractions(Section("interactions"), doc, rootPath);
        ReadDimensionTable(Section("dimensions"), doc, rootPath);
        ReadTime(Section("time"), doc, rootPath);
        ReadTags(Section("tags"), doc, rootPath);
        ReadSynchronizations(Section("synchronizations"), doc, rootPath);
        ReadTransportations(Section("transportations"), doc, rootPath);
        ReadUpdateRates(Section("updateRates"), doc, rootPath);
        ReadSwitches(Section("switches"), doc, rootPath);
        ReadNotes(Section("notes"), doc, rootPath);
        ReadDataTypes(Section("dataTypes"), doc, rootPath);

        return doc;
    }

    // ---------------------------------------------------------------- standard detection

    /// <summary>
    /// Maps the root namespace URI onto a <see cref="FomStandard"/>, falling back to the configured
    /// standard (with an <see cref="DiagnosticSeverity.Info"/> diagnostic) when it is unfamiliar.
    /// </summary>
    private FomStandard DetectStandard(string namespaceUri, XElement root, FomDocument doc)
    {
        if (namespaceUri.Contains("IEEE1516-2010", Cmp)) return FomStandard.Ieee1516_2010;
        if (namespaceUri.Contains("IEEE1516-2025", Cmp)) return FomStandard.Ieee1516_2025;
        if (namespaceUri.Contains("IEEE1516-2000", Cmp)) return FomStandard.Ieee1516_2000;

        doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Info,
            string.IsNullOrWhiteSpace(namespaceUri)
                ? $"The root element declares no XML namespace; assuming {Describe(_standard)}."
                : $"Unrecognised root namespace '{namespaceUri}'; assuming {Describe(_standard)}.",
            Line(root), root.Name.LocalName));

        return _standard;
    }

    /// <summary>Best guess at the standard for a file that could not be opened at all.</summary>
    private FomStandard GuessStandardFromPath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return _standard;

        var fileName = Path.GetFileName(sourcePath);
        if (fileName.Contains("2025", Cmp)) return FomStandard.Ieee1516_2025;
        if (fileName.Contains("2000", Cmp)) return FomStandard.Ieee1516_2000;
        if (fileName.Contains("2010", Cmp) || fileName.Contains("evolved", Cmp)) return FomStandard.Ieee1516_2010;

        // Nothing in the name to go on: an XML-ish extension means "the standard I was configured
        // for", anything else means the caller handed this parser a file it does not own.
        var extension = Path.GetExtension(sourcePath);
        return extension.Equals(".xml", Cmp) || extension.Equals(".fom", Cmp) ||
               extension.Equals(".fdd", Cmp) || extension.Equals(".omt", Cmp)
            ? _standard
            : FomStandard.Unknown;
    }

    /// <summary>Human label for a standard, matching <see cref="FomDocument.StandardDisplayName"/>.</summary>
    private static string Describe(FomStandard standard) => standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "IEEE 1516-2000",
        FomStandard.Ieee1516_2010 => "IEEE 1516-2010 (Evolved)",
        FomStandard.Ieee1516_2025 => "IEEE 1516-2025",
        _ => "Unknown",
    };

    /// <summary>Builds the empty document returned when the file cannot be loaded.</summary>
    private FomDocument Failed(string? sourcePath, string message, int? line)
    {
        var doc = new FomDocument
        {
            SourcePath = sourcePath,
            Standard = GuessStandardFromPath(sourcePath),
        };
        doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Error, message, line, sourcePath));
        return doc;
    }

    // ---------------------------------------------------------------- model identification

    /// <summary>Reads <c>&lt;modelIdentification&gt;</c> into <see cref="FomDocument.Identification"/>.</summary>
    private static void ReadModelIdentification(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/modelIdentification";
        if (WarnIfEmpty(section, doc, path)) return;

        var id = new ModelIdentification
        {
            Name = Value(section, "name"),
            Type = Value(section, "type"),
            Version = Value(section, "version"),
            ModificationDate = Value(section, "modificationDate"),
            SecurityClassification = Value(section, "securityClassification"),
            ReleaseRestriction = Value(section, "releaseRestriction"),
            Purpose = Value(section, "purpose"),
            ApplicationDomain = Value(section, "applicationDomain"),
            Description = Value(section, "description"),
            UseLimitation = Value(section, "useLimitation"),
            Reference = ReadReferences(section),
            Other = Value(section, "other"),
            Glyph = Value(section, "glyph"),
        };

        foreach (var keyword in Elements(section, "keyword"))
        {
            // 1516-2010 writes <keyword><taxonomy/><taxonomyValue/></keyword>; older tools just
            // put the word in the element.
            var text = Value(keyword, "taxonomyValue") ?? OwnText(keyword) ?? Norm(keyword.Value);
            if (text is not null)
                id.Keywords.Add(text);
            else
                WarnIfEmpty(keyword, doc, $"{path}/keyword");
        }

        foreach (var poc in Elements(section, "poc"))
        {
            var text = FlattenPointOfContact(poc);
            if (text is not null)
                id.PointsOfContact.Add(text);
            else
                WarnIfEmpty(poc, doc, $"{path}/poc");
        }

        foreach (var use in Elements(section, "useHistory"))
        {
            var text = OwnText(use) ?? Norm(use.Value);
            if (text is not null)
                id.UseHistory.Add(text);
            else
                WarnIfEmpty(use, doc, $"{path}/useHistory");
        }

        doc.Identification = id;
    }

    /// <summary>
    /// Flattens the <c>&lt;reference&gt;</c> entries. 1516-2000 nests them as
    /// <c>&lt;reference&gt;&lt;type/&gt;&lt;identification/&gt;&lt;/reference&gt;</c>, which is
    /// rendered as <c>"type: identification"</c>; several references are joined with "; ".
    /// </summary>
    private static string? ReadReferences(XElement section)
    {
        var parts = new List<string>();

        foreach (var reference in Elements(section, "reference"))
        {
            var type = Value(reference, "type");
            var identification = Value(reference, "identification");
            var flattened = Join(": ", type, identification) ?? OwnText(reference) ?? Norm(reference.Value);
            if (flattened is not null)
                parts.Add(flattened);
        }

        if (parts.Count == 0)
        {
            var attribute = Norm(Attr(section, "reference")?.Value);
            if (attribute is not null) parts.Add(attribute);
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Renders one <c>&lt;poc&gt;</c> as "type: name, org, telephone, email".</summary>
    private static string? FlattenPointOfContact(XElement poc)
    {
        var type = ValueAny(poc, "pocType", "type");
        var name = ValueAny(poc, "pocName", "name");
        var org = ValueAny(poc, "pocOrg", "pocOrgName", "organization");
        var telephone = ValueAny(poc, "pocTelephone", "telephone", "pocPhone");
        var email = ValueAny(poc, "pocEmail", "email");

        var details = string.Join(", ", new[] { name, org, telephone, email }
            .Where(part => !string.IsNullOrEmpty(part)));

        var body = Norm(details) ?? OwnText(poc);
        return Join(": ", type, body);
    }

    // ---------------------------------------------------------------- objects

    /// <summary>Reads the <c>&lt;objects&gt;</c> class tree.</summary>
    private static void ReadObjects(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/objects";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "objectClass"))
        {
            var objectClass = ReadObjectClass(element, null, doc, path);
            if (objectClass is not null)
                doc.ObjectClasses.Add(objectClass);
        }
    }

    /// <summary>Reads one <c>&lt;objectClass&gt;</c> and, recursively, its nested classes.</summary>
    private static FomObjectClass? ReadObjectClass(XElement element, FomObjectClass? parent, FomDocument doc, string parentPath)
    {
        var name = Value(element, "name");
        var path = $"{parentPath}/objectClass[{name ?? UnnamedName}]";
        if (WarnIfEmpty(element, doc, path)) return null;
        name ??= WarnUnnamed(element, doc, path);

        var objectClass = new FomObjectClass
        {
            Name = name,
            QualifiedName = parent is null ? name : $"{parent.QualifiedName}.{name}",
            Sharing = Value(element, "sharing"),
            Parent = parent,
        };
        ReadCommon(objectClass, element);

        foreach (var attributeElement in Elements(element, "attribute"))
        {
            var attribute = ReadAttribute(attributeElement, objectClass, doc, path);
            if (attribute is not null)
                objectClass.Attributes.Add(attribute);
        }

        foreach (var childElement in Elements(element, "objectClass"))
        {
            var child = ReadObjectClass(childElement, objectClass, doc, path);
            if (child is not null)
                objectClass.Children.Add(child);
        }

        return objectClass;
    }

    /// <summary>Reads one <c>&lt;attribute&gt;</c> of an object class.</summary>
    private static FomAttribute? ReadAttribute(XElement element, FomObjectClass owner, FomDocument doc, string parentPath)
    {
        var name = Value(element, "name");
        var path = $"{parentPath}/attribute[{name ?? UnnamedName}]";
        if (WarnIfEmpty(element, doc, path)) return null;
        name ??= WarnUnnamed(element, doc, path);

        var attribute = new FomAttribute
        {
            Name = name,
            QualifiedName = $"{owner.QualifiedName}.{name}",
            DataType = Value(element, "dataType"),
            UpdateType = Value(element, "updateType"),
            UpdateCondition = Value(element, "updateCondition"),
            Ownership = Value(element, "ownership"),
            Sharing = Value(element, "sharing"),
            Transportation = Value(element, "transportation"),
            Order = Value(element, "order"),
            // RoutingSpace is an HLA 1.3 concept; 1516 expresses the same idea with dimensions.
        };
        ReadCommon(attribute, element);
        ReadDimensionRefs(element, attribute.Dimensions);

        return attribute;
    }

    // ---------------------------------------------------------------- interactions

    /// <summary>Reads the <c>&lt;interactions&gt;</c> class tree.</summary>
    private static void ReadInteractions(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/interactions";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "interactionClass"))
        {
            var interaction = ReadInteractionClass(element, null, doc, path);
            if (interaction is not null)
                doc.InteractionClasses.Add(interaction);
        }
    }

    /// <summary>Reads one <c>&lt;interactionClass&gt;</c> and, recursively, its nested classes.</summary>
    private static FomInteractionClass? ReadInteractionClass(XElement element, FomInteractionClass? parent, FomDocument doc, string parentPath)
    {
        var name = Value(element, "name");
        var path = $"{parentPath}/interactionClass[{name ?? UnnamedName}]";
        if (WarnIfEmpty(element, doc, path)) return null;
        name ??= WarnUnnamed(element, doc, path);

        var interaction = new FomInteractionClass
        {
            Name = name,
            QualifiedName = parent is null ? name : $"{parent.QualifiedName}.{name}",
            Sharing = Value(element, "sharing"),
            Transportation = Value(element, "transportation"),
            Order = Value(element, "order"),
            Parent = parent,
        };
        ReadCommon(interaction, element);
        ReadDimensionRefs(element, interaction.Dimensions);

        foreach (var parameterElement in Elements(element, "parameter"))
        {
            var parameter = ReadParameter(parameterElement, interaction, doc, path);
            if (parameter is not null)
                interaction.Parameters.Add(parameter);
        }

        foreach (var childElement in Elements(element, "interactionClass"))
        {
            var child = ReadInteractionClass(childElement, interaction, doc, path);
            if (child is not null)
                interaction.Children.Add(child);
        }

        return interaction;
    }

    /// <summary>Reads one <c>&lt;parameter&gt;</c> of an interaction class.</summary>
    private static FomParameter? ReadParameter(XElement element, FomInteractionClass owner, FomDocument doc, string parentPath)
    {
        var name = Value(element, "name");
        var path = $"{parentPath}/parameter[{name ?? UnnamedName}]";
        if (WarnIfEmpty(element, doc, path)) return null;
        name ??= WarnUnnamed(element, doc, path);

        var parameter = new FomParameter
        {
            Name = name,
            QualifiedName = $"{owner.QualifiedName}.{name}",
            DataType = Value(element, "dataType"),
        };
        ReadCommon(parameter, element);

        return parameter;
    }

    /// <summary>
    /// Collects the dimension names referenced by an attribute or interaction class. Both shapes are
    /// accepted: the nested <c>&lt;dimensions&gt;&lt;dimension&gt;</c> list and a flat
    /// <c>dimensions="A B"</c> / <c>dimensions="A,B"</c> XML attribute.
    /// </summary>
    private static void ReadDimensionRefs(XElement owner, List<string> target)
    {
        AddSplit(Attr(owner, "dimensions")?.Value, target);

        foreach (var list in Elements(owner, "dimensions"))
        {
            var entries = Elements(list, "dimension").ToList();
            if (entries.Count == 0)
            {
                // <dimensions>A B</dimensions> — the whole list written as text.
                AddSplit(OwnText(list), target);
                continue;
            }

            foreach (var entry in entries)
            {
                var name = OwnText(entry) ?? Value(entry, "name");
                AddUnique(name, target);
            }
        }
    }

    // ---------------------------------------------------------------- dimensions

    /// <summary>Reads the <c>&lt;dimensions&gt;</c> table.</summary>
    private static void ReadDimensionTable(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/dimensions";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "dimension"))
        {
            var name = Value(element, "name");
            var entryPath = $"{path}/dimension[{name ?? UnnamedName}]";
            if (WarnIfEmpty(element, doc, entryPath)) continue;
            name ??= WarnUnnamed(element, doc, entryPath);

            var dimension = new FomDimension
            {
                Name = name,
                QualifiedName = name,
                DataType = Value(element, "dataType"),
                UpperBound = Value(element, "upperBound"),
                Normalization = Value(element, "normalization"),
                Value = Value(element, "value"),
            };
            ReadCommon(dimension, element);

            doc.Dimensions.Add(dimension);
        }
    }

    // ---------------------------------------------------------------- time

    /// <summary>Reads the <c>&lt;time&gt;</c> representation table.</summary>
    private static void ReadTime(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/time";
        if (WarnIfEmpty(section, doc, path)) return;

        var time = new FomTime();

        var timeStamp = Element(section, "timeStamp");
        if (timeStamp is not null)
        {
            time.TimeStampDataType = Value(timeStamp, "dataType") ?? BareText(timeStamp);
            time.TimeStampSemantics = Value(timeStamp, "semantics");
        }

        var lookahead = Element(section, "lookahead");
        if (lookahead is not null)
        {
            time.LookaheadDataType = Value(lookahead, "dataType") ?? BareText(lookahead);
            time.LookaheadSemantics = Value(lookahead, "semantics");
        }

        doc.Time = time;
    }

    // ---------------------------------------------------------------- tags

    /// <summary>
    /// Reads the <c>&lt;tags&gt;</c> table. Every child element is a tag slot
    /// (<c>updateReflectTag</c>, <c>sendReceiveTag</c>, …); the slot name is the element's own name,
    /// so slots added by later revisions are picked up without a code change.
    /// </summary>
    private static void ReadTags(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/tags";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in section.Elements())
        {
            var name = element.Name.LocalName;
            var entryPath = $"{path}/{name}";
            if (WarnIfEmpty(element, doc, entryPath)) continue;

            var tag = new FomTag
            {
                Name = name,
                QualifiedName = name,
                DataType = Value(element, "dataType") ?? BareText(element),
            };
            ReadCommon(tag, element);

            doc.Tags.Add(tag);
        }
    }

    // ---------------------------------------------------------------- synchronizations

    /// <summary>Reads the <c>&lt;synchronizations&gt;</c> table.</summary>
    private static void ReadSynchronizations(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/synchronizations";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "synchronization"))
        {
            var name = ValueAny(element, "name", "label");
            var entryPath = $"{path}/synchronization[{name ?? UnnamedName}]";
            if (WarnIfEmpty(element, doc, entryPath)) continue;
            name ??= WarnUnnamed(element, doc, entryPath);

            var synchronization = new FomSynchronization
            {
                Name = name,
                QualifiedName = name,
                Capability = Value(element, "capability"),
                DataType = Value(element, "dataType"),
            };
            ReadCommon(synchronization, element);

            doc.Synchronizations.Add(synchronization);
        }
    }

    // ---------------------------------------------------------------- transportations

    /// <summary>Reads the <c>&lt;transportations&gt;</c> table.</summary>
    private static void ReadTransportations(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/transportations";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "transportation"))
        {
            var name = Value(element, "name");
            var entryPath = $"{path}/transportation[{name ?? UnnamedName}]";
            if (WarnIfEmpty(element, doc, entryPath)) continue;
            name ??= WarnUnnamed(element, doc, entryPath);

            var transportation = new FomTransportation
            {
                Name = name,
                QualifiedName = name,
                Reliable = Value(element, "reliable"),
            };
            ReadCommon(transportation, element);

            doc.Transportations.Add(transportation);
        }
    }

    // ---------------------------------------------------------------- update rates

    /// <summary>Reads the <c>&lt;updateRates&gt;</c> table (1516-2010 and later).</summary>
    private static void ReadUpdateRates(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/updateRates";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in Elements(section, "updateRate"))
        {
            var name = Value(element, "name");
            var entryPath = $"{path}/updateRate[{name ?? UnnamedName}]";
            if (WarnIfEmpty(element, doc, entryPath)) continue;
            name ??= WarnUnnamed(element, doc, entryPath);

            var updateRate = new FomUpdateRate
            {
                Name = name,
                QualifiedName = name,
                Rate = Value(element, "rate"),
            };
            ReadCommon(updateRate, element);

            doc.UpdateRates.Add(updateRate);
        }
    }

    // ---------------------------------------------------------------- switches

    /// <summary>
    /// Reads the <c>&lt;switches&gt;</c> table. One switch per child element, named after the
    /// element, so unknown switches survive. Both spellings of the value are handled: the 1516-2010
    /// <c>isEnabled="true"</c> attribute and the 1516-2000 element text ("Enabled"/"Disabled").
    /// </summary>
    private static void ReadSwitches(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/switches";
        if (WarnIfEmpty(section, doc, path)) return;

        foreach (var element in section.Elements())
        {
            var name = element.Name.LocalName;
            var entryPath = $"{path}/{name}";
            if (WarnIfEmpty(element, doc, entryPath)) continue;

            var isEnabled = Value(element, "isEnabled");
            var resign = ValueAny(element, "resignAction", "value");
            var text = BareText(element);

            if (text is not null)
            {
                // A bare value belongs to the resign action for the resign switch, and to the
                // enabled flag for everything else.
                if (name.Contains("resign", Cmp))
                    resign ??= text;
                else
                    isEnabled ??= text;
            }

            var fomSwitch = new FomSwitch
            {
                Name = name,
                QualifiedName = name,
                IsEnabled = isEnabled,
                ResignSwitch = resign,
            };
            ReadCommon(fomSwitch, element);

            doc.Switches.Add(fomSwitch);
        }
    }

    // ---------------------------------------------------------------- notes

    /// <summary>Reads the <c>&lt;notes&gt;</c> table.</summary>
    private static void ReadNotes(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/notes";
        if (WarnIfEmpty(section, doc, path)) return;

        var index = 0;
        foreach (var element in Elements(section, "note"))
        {
            index++;
            var label = ValueAny(element, "label", "name");
            var entryPath = $"{path}/note[{label ?? index.ToString()}]";
            if (WarnIfEmpty(element, doc, entryPath)) continue;

            // An unlabelled note is still referenceable by position, so number it instead of
            // dropping it.
            label ??= index.ToString();

            var note = new FomNote
            {
                Name = label,
                QualifiedName = label,
                Label = label,
                Text = ValueAny(element, "semantics", "text") ?? BareText(element),
            };
            ReadCommon(note, element);

            doc.Notes.Add(note);
        }
    }

    // ---------------------------------------------------------------- datatypes

    /// <summary>Reads the six <c>&lt;dataTypes&gt;</c> tables.</summary>
    private static void ReadDataTypes(XElement? section, FomDocument doc, string parentPath)
    {
        if (section is null) return;

        var path = $"{parentPath}/dataTypes";
        if (WarnIfEmpty(section, doc, path)) return;

        var tables = doc.DataTypes;

        foreach (var (element, entryPath) in TableEntries(section, "basicDataRepresentations", "basicData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var basic = new BasicDataType
            {
                Name = name,
                QualifiedName = name,
                Size = Value(element, "size"),
                Interpretation = Value(element, "interpretation"),
                Endian = Value(element, "endian"),
                Encoding = Value(element, "encoding"),
            };
            ReadCommon(basic, element);
            tables.BasicDataRepresentations.Add(basic);
        }

        foreach (var (element, entryPath) in TableEntries(section, "simpleDataTypes", "simpleData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var simple = new SimpleDataType
            {
                Name = name,
                QualifiedName = name,
                Representation = Value(element, "representation"),
                Units = Value(element, "units"),
                Resolution = Value(element, "resolution"),
                Accuracy = Value(element, "accuracy"),
            };
            ReadCommon(simple, element);
            tables.SimpleDataTypes.Add(simple);
        }

        foreach (var (element, entryPath) in TableEntries(section, "enumeratedDataTypes", "enumeratedData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var enumerated = new EnumeratedDataType
            {
                Name = name,
                QualifiedName = name,
                Representation = Value(element, "representation"),
            };
            ReadCommon(enumerated, element);

            foreach (var enumeratorElement in Elements(element, "enumerator"))
            {
                var enumeratorPath = $"{entryPath}/enumerator";
                if (WarnIfEmpty(enumeratorElement, doc, enumeratorPath)) continue;

                var enumeratorName = RequireName(enumeratorElement, doc, enumeratorPath);
                var enumerator = new EnumeratorValue
                {
                    Name = enumeratorName,
                    QualifiedName = $"{name}.{enumeratorName}",
                    Values = EnumeratorValues(enumeratorElement),
                };
                ReadCommon(enumerator, enumeratorElement);
                enumerated.Enumerators.Add(enumerator);
            }

            tables.EnumeratedDataTypes.Add(enumerated);
        }

        foreach (var (element, entryPath) in TableEntries(section, "arrayDataTypes", "arrayData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var array = new ArrayDataType
            {
                Name = name,
                QualifiedName = name,
                DataType = Value(element, "dataType"),
                Cardinality = Value(element, "cardinality"),
                Encoding = Value(element, "encoding"),
            };
            ReadCommon(array, element);
            tables.ArrayDataTypes.Add(array);
        }

        foreach (var (element, entryPath) in TableEntries(section, "fixedRecordDataTypes", "fixedRecordData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var record = new FixedRecordDataType
            {
                Name = name,
                QualifiedName = name,
                Encoding = Value(element, "encoding"),
                // A record may inherit from several others; keep them all in one readable string.
                Include = Joined(element, "include"),
            };
            ReadCommon(record, element);

            foreach (var fieldElement in Elements(element, "field"))
            {
                var fieldPath = $"{entryPath}/field";
                if (WarnIfEmpty(fieldElement, doc, fieldPath)) continue;

                var fieldName = RequireName(fieldElement, doc, fieldPath);
                var field = new RecordField
                {
                    Name = fieldName,
                    QualifiedName = $"{name}.{fieldName}",
                    DataType = Value(fieldElement, "dataType"),
                };
                ReadCommon(field, fieldElement);
                record.Fields.Add(field);
            }

            tables.FixedRecordDataTypes.Add(record);
        }

        foreach (var (element, entryPath) in TableEntries(section, "variantRecordDataTypes", "variantRecordData", doc, path))
        {
            var name = RequireName(element, doc, entryPath);
            var variant = new VariantRecordDataType
            {
                Name = name,
                QualifiedName = name,
                Discriminant = Value(element, "discriminant"),
                DataType = Value(element, "dataType"),
                Encoding = Value(element, "encoding"),
            };
            ReadCommon(variant, element);

            foreach (var alternativeElement in Elements(element, "alternative"))
            {
                var alternativePath = $"{entryPath}/alternative";
                if (WarnIfEmpty(alternativeElement, doc, alternativePath)) continue;

                var alternativeName = RequireName(alternativeElement, doc, alternativePath);
                var alternative = new VariantAlternative
                {
                    Name = alternativeName,
                    QualifiedName = $"{name}.{alternativeName}",
                    Enumerator = Value(alternativeElement, "enumerator"),
                    DataType = Value(alternativeElement, "dataType"),
                };
                ReadCommon(alternative, alternativeElement);
                variant.Alternatives.Add(alternative);
            }

            tables.VariantRecordDataTypes.Add(variant);
        }
    }

    /// <summary>
    /// Yields the entries of one datatype table together with their diagnostic path. Both layouts
    /// are accepted: the entries wrapped in their plural element, and a bare run of entries directly
    /// under <c>&lt;dataTypes&gt;</c>.
    /// </summary>
    private static IEnumerable<(XElement Element, string Path)> TableEntries(
        XElement dataTypes, string plural, string singular, FomDocument doc, string parentPath)
    {
        var results = new List<(XElement, string)>();

        var wrapper = Element(dataTypes, plural);
        if (wrapper is not null && !WarnIfEmpty(wrapper, doc, $"{parentPath}/{plural}"))
        {
            foreach (var entry in Elements(wrapper, singular))
                results.Add((entry, $"{parentPath}/{plural}/{singular}[{Value(entry, "name") ?? UnnamedName}]"));
        }

        foreach (var entry in Elements(dataTypes, singular))
            results.Add((entry, $"{parentPath}/{singular}[{Value(entry, "name") ?? UnnamedName}]"));

        // Empty entries are reported once here so every table body can assume usable content.
        return results.Where(candidate => !WarnIfEmpty(candidate.Item1, doc, candidate.Item2)).ToList();
    }

    /// <summary>
    /// Reads the literal values of one <c>&lt;enumerator&gt;</c>. The value may be a single
    /// <c>&lt;value&gt;</c> element, several of them, or a <c>value</c> XML attribute; several
    /// values are joined with ", ".
    /// </summary>
    private static string? EnumeratorValues(XElement enumerator)
    {
        var parts = new List<string>();

        foreach (var value in Elements(enumerator, "value"))
        {
            var text = (value.HasElements ? OwnText(value) : Norm(value.Value)) ?? Norm(Attr(value, "value")?.Value);
            if (text is not null)
                parts.Add(text);
        }

        if (parts.Count == 0)
        {
            var attribute = Norm(Attr(enumerator, "value")?.Value);
            if (attribute is not null)
                parts.Add(attribute);
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // ---------------------------------------------------------------- XML helpers

    /// <summary>True when the element's local name matches, whatever namespace it lives in.</summary>
    private static bool IsNamed(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, Cmp);

    /// <summary>Direct child elements with the given local name, in document order.</summary>
    private static IEnumerable<XElement> Elements(XElement owner, string localName) =>
        owner.Elements().Where(child => child.Name.LocalName.Equals(localName, Cmp));

    /// <summary>First direct child element with the given local name, or null.</summary>
    private static XElement? Element(XElement owner, string localName) =>
        Elements(owner, localName).FirstOrDefault();

    /// <summary>XML attribute with the given local name, or null.</summary>
    private static XAttribute? Attr(XElement owner, string localName) =>
        owner.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(localName, Cmp));

    /// <summary>
    /// Reads one scalar property, wherever the serialiser chose to put it: the XML attribute first,
    /// then the child element of the same local name. Returns null for a missing or blank value.
    /// A child that has element children of its own contributes only its own text, so a structured
    /// element never collapses into a run of concatenated descendants.
    /// </summary>
    private static string? Value(XElement owner, string localName)
    {
        var attribute = Norm(Attr(owner, localName)?.Value);
        if (attribute is not null) return attribute;

        var child = Element(owner, localName);
        if (child is null) return null;

        return child.HasElements ? OwnText(child) : Norm(child.Value);
    }

    /// <summary>
    /// <see cref="Value(XElement,string)"/> over several spellings of the same property; the first
    /// one that yields a value wins.
    /// </summary>
    private static string? ValueAny(XElement owner, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            var value = Value(owner, localName);
            if (value is not null) return value;
        }

        return null;
    }

    /// <summary>All values of a repeatable scalar property, joined with ", ".</summary>
    private static string? Joined(XElement owner, string localName)
    {
        var parts = new List<string>();

        var attribute = Norm(Attr(owner, localName)?.Value);
        if (attribute is not null) parts.Add(attribute);

        foreach (var child in Elements(owner, localName))
        {
            var text = child.HasElements ? OwnText(child) : Norm(child.Value);
            if (text is not null) parts.Add(text);
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The element's own text nodes, ignoring the text of any child elements.</summary>
    private static string? OwnText(XElement element) =>
        Norm(string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)));

    /// <summary>The text of an element that carries no child elements, otherwise null.</summary>
    private static string? BareText(XElement element) =>
        element.HasElements ? null : Norm(element.Value);

    /// <summary>Trims a raw XML value and maps blank to null.</summary>
    private static string? Norm(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Joins two optional parts with a separator, dropping the separator when one is blank.</summary>
    private static string? Join(string separator, string? first, string? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first + separator + second;
    }

    /// <summary>Adds a value to a list unless it is blank or already present.</summary>
    private static void AddUnique(string? value, List<string> target)
    {
        var text = Norm(value);
        if (text is null) return;
        if (!target.Contains(text, StringComparer.Ordinal))
            target.Add(text);
    }

    /// <summary>Splits a comma- or whitespace-separated list into a target list, without duplicates.</summary>
    private static void AddSplit(string? value, List<string> target)
    {
        if (value is null) return;

        foreach (var part in value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            AddUnique(part, target);
        }
    }

    /// <summary>Copies the properties every OMT element may carry onto the node.</summary>
    private static void ReadCommon(FomNode node, XElement element)
    {
        node.Semantics = Value(element, "semantics");
        node.Notes = Value(element, "notes");
    }

    /// <summary>Line number of an element, when the document was loaded with line information.</summary>
    private static int? Line(XObject element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    /// <summary>
    /// Reports an element that exists but carries nothing at all — no attributes, no children and no
    /// text — and tells the caller to skip it.
    /// </summary>
    private static bool WarnIfEmpty(XElement element, FomDocument doc, string path)
    {
        if (element.HasAttributes || element.HasElements || !string.IsNullOrWhiteSpace(element.Value))
            return false;

        doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning,
            $"<{element.Name.LocalName}> is present but empty; it was ignored.", Line(element), path));
        return true;
    }

    /// <summary>Reports a named element that has no name and returns the placeholder used instead.</summary>
    private static string WarnUnnamed(XElement element, FomDocument doc, string path)
    {
        doc.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning,
            $"<{element.Name.LocalName}> has no name; it was read as \"{UnnamedName}\".",
            Line(element), path));
        return UnnamedName;
    }

    /// <summary>The element's name, or the placeholder plus a warning when it has none.</summary>
    private static string RequireName(XElement element, FomDocument doc, string path) =>
        Value(element, "name") ?? WarnUnnamed(element, doc, path);
}
