using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Serialization;

/// <summary>
/// Writes a <see cref="FomDocument"/> out as an IEEE 1516-2010 OMT DIF document.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <c>Ieee1516XmlParser</c>, and written against it: every scalar is emitted in a
/// spelling that parser reads back to the same value, and <c>Ieee1516XmlWriterTests</c> proves it by
/// round-tripping each sample through both and comparing the result with <c>FomComparer</c>. That
/// test is the specification. Where the parser accepts several spellings of a property — an XML
/// attribute or a child element, <c>&lt;text&gt;</c> or <c>&lt;semantics&gt;</c> — this writer picks
/// the one 1516-2010 itself uses, so the output is a file other HLA tooling can read rather than
/// only a file this application can read.
/// </para>
/// <para>
/// Output is always 1516-2010, whatever the source document's standard. A compiled FOM is the model
/// a 1516-2010 federation runs, and modules are a 1516-2010 concept; there is nothing to write for
/// the other standards that would not be a translation rather than a serialisation. HLA 1.3 routing
/// spaces are the one thing deliberately dropped, because 1516 has no element for them — it
/// expresses the same idea with dimensions.
/// </para>
/// <para>
/// Nothing is invented. A property the document does not carry produces no element, rather than an
/// element holding a default: an absent <c>updateType</c> and an <c>updateType</c> of <c>NA</c> mean
/// different things to a reader, and writing the second where the first was true would make this
/// file claim more than the modules it came from ever said.
/// </para>
/// </remarks>
public static class Ieee1516XmlWriter
{
    /// <summary>The 1516-2010 OMT DIF namespace, declared as the default on the root element.</summary>
    public const string Namespace = "http://standards.ieee.org/IEEE1516-2010";

    private static readonly XNamespace Ns = Namespace;

    /// <summary>Renders <paramref name="document"/> as a complete OMT DIF XML document.</summary>
    public static string ToXml(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", null), BuildRoot(document));

        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using (var writer = XmlWriter.Create(builder, settings))
            xml.Save(writer);

        return builder.ToString();
    }

    /// <summary>
    /// Writes <paramref name="document"/> to <paramref name="path"/>, creating the folder if needed.
    /// </summary>
    /// <remarks>
    /// Rendered in full before the file is touched. A failure part-way through serialising would
    /// otherwise leave a truncated FOM on disk under a name the registry is about to point at, and a
    /// FOM that stops half way through its datatype table parses to something plausible.
    /// </remarks>
    public static void Write(FomDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var xml = ToXml(document);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ---------------------------------------------------------------- document

    private static XElement BuildRoot(FomDocument document)
    {
        var root = new XElement(Ns + "objectModel");

        // OMT section order, which is the order the standard lists them in. The parser does not
        // care, but a person diffing this against a vendor FOM does.
        Add(root, Identification(document.Identification));
        Add(root, Section("objects", document.ObjectClasses.Select(ObjectClass)));
        Add(root, Section("interactions", document.InteractionClasses.Select(InteractionClass)));
        Add(root, Section("dimensions", document.Dimensions.Select(Dimension)));
        Add(root, Time(document.Time));
        Add(root, Section("tags", document.Tags.Select(Tag)));
        Add(root, Section("synchronizations", document.Synchronizations.Select(Synchronization)));
        Add(root, Section("transportations", document.Transportations.Select(Transportation)));
        Add(root, Section("switches", document.Switches.Select(Switch)));
        Add(root, Section("updateRates", document.UpdateRates.Select(UpdateRate)));
        Add(root, DataTypes(document.DataTypes));
        Add(root, Section("notes", document.Notes.Select(Note)));

        return root;
    }

    // ---------------------------------------------------------------- model identification

    private static XElement? Identification(ModelIdentification id)
    {
        var element = new XElement(Ns + "modelIdentification");

        Scalar(element, "name", id.Name);
        Scalar(element, "type", id.Type);
        Scalar(element, "version", id.Version);
        Scalar(element, "modificationDate", id.ModificationDate);
        Scalar(element, "securityClassification", id.SecurityClassification);
        Scalar(element, "releaseRestriction", id.ReleaseRestriction);
        Scalar(element, "purpose", id.Purpose);
        Scalar(element, "applicationDomain", id.ApplicationDomain);
        Scalar(element, "description", id.Description);
        Scalar(element, "useLimitation", id.UseLimitation);

        foreach (var keyword in id.Keywords.Where(NotBlank))
            element.Add(new XElement(Ns + "keyword", new XElement(Ns + "taxonomyValue", keyword)));

        foreach (var use in id.UseHistory.Where(NotBlank))
            element.Add(new XElement(Ns + "useHistory", use));

        // Both of these are read as flattened text — a point of contact as "type: name, org, phone,
        // email" and the references joined with "; " — so they go back out as the single string they
        // were read into. Re-splitting them into the structured form would mean guessing which comma
        // was a field separator and which was part of an organisation's name.
        foreach (var poc in id.PointsOfContact.Where(NotBlank))
            element.Add(new XElement(Ns + "poc", poc));

        Scalar(element, "reference", id.Reference);
        Scalar(element, "other", id.Other);
        Scalar(element, "glyph", id.Glyph);

        return element.HasElements ? element : null;
    }

    // ---------------------------------------------------------------- classes

    private static XElement ObjectClass(FomObjectClass objectClass)
    {
        var element = new XElement(Ns + "objectClass");

        Scalar(element, "name", objectClass.Name);
        Scalar(element, "sharing", objectClass.Sharing);
        Common(element, objectClass);

        foreach (var attribute in objectClass.Attributes)
            element.Add(Attribute(attribute));

        foreach (var child in objectClass.Children)
            element.Add(ObjectClass(child));

        return element;
    }

    private static XElement Attribute(FomAttribute attribute)
    {
        var element = new XElement(Ns + "attribute");

        Scalar(element, "name", attribute.Name);
        Scalar(element, "dataType", attribute.DataType);
        Scalar(element, "updateType", attribute.UpdateType);
        Scalar(element, "updateCondition", attribute.UpdateCondition);
        Scalar(element, "ownership", attribute.Ownership);
        Scalar(element, "sharing", attribute.Sharing);
        Dimensions(element, attribute.Dimensions);
        Scalar(element, "transportation", attribute.Transportation);
        Scalar(element, "order", attribute.Order);
        Common(element, attribute);

        return element;
    }

    private static XElement InteractionClass(FomInteractionClass interaction)
    {
        var element = new XElement(Ns + "interactionClass");

        Scalar(element, "name", interaction.Name);
        Scalar(element, "sharing", interaction.Sharing);
        Dimensions(element, interaction.Dimensions);
        Scalar(element, "transportation", interaction.Transportation);
        Scalar(element, "order", interaction.Order);
        Common(element, interaction);

        foreach (var parameter in interaction.Parameters)
            element.Add(Parameter(parameter));

        foreach (var child in interaction.Children)
            element.Add(InteractionClass(child));

        return element;
    }

    private static XElement Parameter(FomParameter parameter)
    {
        var element = new XElement(Ns + "parameter");

        Scalar(element, "name", parameter.Name);
        Scalar(element, "dataType", parameter.DataType);
        Common(element, parameter);

        return element;
    }

    /// <summary>
    /// The dimension names an attribute or interaction is normalised over.
    /// </summary>
    /// <remarks>
    /// Written as the nested list rather than the flat <c>dimensions="A B"</c> form the parser also
    /// accepts, because a dimension name is allowed to contain a space and the flat form cannot say
    /// where one ends.
    /// </remarks>
    private static void Dimensions(XElement owner, IReadOnlyList<string> dimensions)
    {
        var named = dimensions.Where(NotBlank).ToList();
        if (named.Count == 0) return;

        owner.Add(new XElement(Ns + "dimensions",
            named.Select(name => new XElement(Ns + "dimension", name))));
    }

    // ---------------------------------------------------------------- tables

    private static XElement Dimension(FomDimension dimension)
    {
        var element = new XElement(Ns + "dimension");

        Scalar(element, "name", dimension.Name);
        Scalar(element, "dataType", dimension.DataType);
        Scalar(element, "upperBound", dimension.UpperBound);
        Scalar(element, "normalization", dimension.Normalization);
        Scalar(element, "value", dimension.Value);
        Common(element, dimension);

        return element;
    }

    private static XElement? Time(FomTime time)
    {
        if (time.IsEmpty) return null;

        var element = new XElement(Ns + "time");

        var timeStamp = new XElement(Ns + "timeStamp");
        Scalar(timeStamp, "dataType", time.TimeStampDataType);
        Scalar(timeStamp, "semantics", time.TimeStampSemantics);
        if (timeStamp.HasElements) element.Add(timeStamp);

        var lookahead = new XElement(Ns + "lookahead");
        Scalar(lookahead, "dataType", time.LookaheadDataType);
        Scalar(lookahead, "semantics", time.LookaheadSemantics);
        if (lookahead.HasElements) element.Add(lookahead);

        return element.HasElements ? element : null;
    }

    /// <summary>
    /// One of the OMT tag slots. The slot is the element name — <c>updateReflectTag</c>,
    /// <c>sendReceiveTag</c> and so on — rather than a <c>name</c> property, which is why this is
    /// built from <see cref="FomNode.Name"/> instead of writing it.
    /// </summary>
    private static XElement Tag(FomTag tag)
    {
        var element = new XElement(Ns + Local(tag.Name, "tag"));

        Scalar(element, "dataType", tag.DataType);
        Common(element, tag);

        return element;
    }

    private static XElement Synchronization(FomSynchronization synchronization)
    {
        var element = new XElement(Ns + "synchronization");

        Scalar(element, "name", synchronization.Name);
        Scalar(element, "capability", synchronization.Capability);
        Scalar(element, "dataType", synchronization.DataType);
        Common(element, synchronization);

        return element;
    }

    private static XElement Transportation(FomTransportation transportation)
    {
        var element = new XElement(Ns + "transportation");

        Scalar(element, "name", transportation.Name);
        Scalar(element, "reliable", transportation.Reliable);
        Common(element, transportation);

        return element;
    }

    private static XElement UpdateRate(FomUpdateRate updateRate)
    {
        var element = new XElement(Ns + "updateRate");

        Scalar(element, "name", updateRate.Name);
        Scalar(element, "rate", updateRate.Rate);
        Common(element, updateRate);

        return element;
    }

    /// <summary>
    /// A switch, whose name is its element name and whose value is an XML attribute.
    /// </summary>
    /// <remarks>
    /// The attribute spelling — <c>&lt;autoProvide isEnabled="true"/&gt;</c> — is what 1516-2010
    /// itself uses for this table, and the only one of the accepted spellings that does.
    /// </remarks>
    private static XElement Switch(FomSwitch fomSwitch)
    {
        var element = new XElement(Ns + Local(fomSwitch.Name, "switch"));

        if (NotBlank(fomSwitch.IsEnabled))
            element.Add(new XAttribute("isEnabled", fomSwitch.IsEnabled!));

        if (NotBlank(fomSwitch.ResignSwitch))
            element.Add(new XAttribute("resignAction", fomSwitch.ResignSwitch!));

        Common(element, fomSwitch);

        return element;
    }

    /// <summary>
    /// One note from the notes table.
    /// </summary>
    /// <remarks>
    /// The body goes out as <c>&lt;semantics&gt;</c> when the note has one and as
    /// <c>&lt;text&gt;</c> otherwise. Writing both would be wrong rather than merely redundant: the
    /// parser prefers semantics for the note's text, so a note whose text came from
    /// <c>&lt;text&gt;</c> would come back carrying a semantics it never had.
    /// </remarks>
    private static XElement Note(FomNote note)
    {
        var element = new XElement(Ns + "note");

        Scalar(element, "label", note.Label ?? note.Name);

        if (NotBlank(note.Semantics))
            Scalar(element, "semantics", note.Semantics);
        else
            Scalar(element, "text", note.Text);

        Scalar(element, "notes", note.Notes);

        return element;
    }

    // ---------------------------------------------------------------- datatypes

    private static XElement? DataTypes(FomDataTypeTables tables)
    {
        if (tables.IsEmpty) return null;

        var element = new XElement(Ns + "dataTypes");

        Add(element, Section("basicDataRepresentations", tables.BasicDataRepresentations.Select(Basic)));
        Add(element, Section("simpleDataTypes", tables.SimpleDataTypes.Select(Simple)));
        Add(element, Section("enumeratedDataTypes", tables.EnumeratedDataTypes.Select(Enumerated)));
        Add(element, Section("arrayDataTypes", tables.ArrayDataTypes.Select(ArrayType)));
        Add(element, Section("fixedRecordDataTypes", tables.FixedRecordDataTypes.Select(FixedRecord)));
        Add(element, Section("variantRecordDataTypes", tables.VariantRecordDataTypes.Select(VariantRecord)));

        return element.HasElements ? element : null;
    }

    private static XElement Basic(BasicDataType type)
    {
        var element = new XElement(Ns + "basicData");

        Scalar(element, "name", type.Name);
        Scalar(element, "size", type.Size);
        Scalar(element, "interpretation", type.Interpretation);
        Scalar(element, "endian", type.Endian);
        Scalar(element, "encoding", type.Encoding);
        Common(element, type);

        return element;
    }

    private static XElement Simple(SimpleDataType type)
    {
        var element = new XElement(Ns + "simpleData");

        Scalar(element, "name", type.Name);
        Scalar(element, "representation", type.Representation);
        Scalar(element, "units", type.Units);
        Scalar(element, "resolution", type.Resolution);
        Scalar(element, "accuracy", type.Accuracy);
        Common(element, type);

        return element;
    }

    private static XElement Enumerated(EnumeratedDataType type)
    {
        var element = new XElement(Ns + "enumeratedData");

        Scalar(element, "name", type.Name);
        Scalar(element, "representation", type.Representation);
        Common(element, type);

        foreach (var enumerator in type.Enumerators)
        {
            var entry = new XElement(Ns + "enumerator");

            Scalar(entry, "name", enumerator.Name);

            // The reader joins several <value> elements with ", " into one string, so the string it
            // produced goes back out as one <value>. Splitting it again would invent a boundary.
            Scalar(entry, "value", enumerator.Values);
            Common(entry, enumerator);

            element.Add(entry);
        }

        return element;
    }

    private static XElement ArrayType(ArrayDataType type)
    {
        var element = new XElement(Ns + "arrayData");

        Scalar(element, "name", type.Name);
        Scalar(element, "dataType", type.DataType);
        Scalar(element, "cardinality", type.Cardinality);
        Scalar(element, "encoding", type.Encoding);
        Common(element, type);

        return element;
    }

    private static XElement FixedRecord(FixedRecordDataType type)
    {
        var element = new XElement(Ns + "fixedRecordData");

        Scalar(element, "name", type.Name);
        Scalar(element, "encoding", type.Encoding);
        Scalar(element, "include", type.Include);
        Common(element, type);

        foreach (var field in type.Fields)
        {
            var entry = new XElement(Ns + "field");

            Scalar(entry, "name", field.Name);
            Scalar(entry, "dataType", field.DataType);
            Common(entry, field);

            element.Add(entry);
        }

        return element;
    }

    private static XElement VariantRecord(VariantRecordDataType type)
    {
        var element = new XElement(Ns + "variantRecordData");

        Scalar(element, "name", type.Name);
        Scalar(element, "discriminant", type.Discriminant);
        Scalar(element, "dataType", type.DataType);
        Scalar(element, "encoding", type.Encoding);
        Common(element, type);

        foreach (var alternative in type.Alternatives)
        {
            var entry = new XElement(Ns + "alternative");

            Scalar(entry, "name", alternative.Name);
            Scalar(entry, "enumerator", alternative.Enumerator);
            Scalar(entry, "dataType", alternative.DataType);
            Common(entry, alternative);

            element.Add(entry);
        }

        return element;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The two properties every named OMT element can carry.</summary>
    private static void Common(XElement element, FomNode node)
    {
        Scalar(element, "semantics", node.Semantics);
        Scalar(element, "notes", node.Notes);
    }

    /// <summary>Adds a child element holding <paramref name="value"/>, unless it is blank.</summary>
    private static void Scalar(XElement owner, string localName, string? value)
    {
        if (NotBlank(value))
            owner.Add(new XElement(Ns + localName, value!));
    }

    /// <summary>A wrapper element holding <paramref name="entries"/>, or null when there are none.</summary>
    private static XElement? Section(string localName, IEnumerable<XElement> entries)
    {
        var children = entries.ToList();
        return children.Count == 0 ? null : new XElement(Ns + localName, children);
    }

    private static void Add(XElement owner, XElement? child)
    {
        if (child is not null) owner.Add(child);
    }

    /// <summary>
    /// An element name taken from a model element's own name, for the two tables where the name
    /// <em>is</em> the element. Falls back when a document arrives carrying a blank one, since an
    /// XML element cannot be nameless and dropping the entry would lose the value it holds.
    /// </summary>
    private static string Local(string name, string fallback)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || !XmlNameStartsWell(trimmed) ? fallback : trimmed;
    }

    private static bool XmlNameStartsWell(string name)
    {
        try
        {
            return XmlConvert.VerifyName(name) == name;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
}
