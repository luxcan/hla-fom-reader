using System;
using System.Collections.Generic;

namespace HLAFomReader.Core.Registry;

/// <summary>
/// The catalogue of browsable registry tables, in presentation order.
/// </summary>
/// <remarks>
/// Every SELECT here is hand-authored rather than generated: the stored schema is relational, so a
/// naive <c>SELECT *</c> would show surrogate ids where a reader expects names. Each statement joins
/// those ids away, projects a leading <c>Key</c> column that identifies the row within its FOM, and
/// orders deterministically so two FOMs read back in a comparable sequence.
/// </remarks>
public static class RegistryTables
{
    /// <summary>Every browsable table, in the order the table list should show them.</summary>
    public static IReadOnlyList<RegistryTable> All { get; } = BuildAll();

    /// <summary>Lookup by <see cref="RegistryTable.Name"/>, ordinal-ignore-case.</summary>
    private static readonly Dictionary<string, RegistryTable> ByName = BuildIndex(All);

    /// <summary>Finds a table by its underlying SQLite name, ignoring case; null when unknown.</summary>
    /// <param name="name">Table name, e.g. <c>ObjectAttributes</c>.</param>
    public static RegistryTable? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return ByName.TryGetValue(name, out var table) ? table : null;
    }

    /// <summary>True when <paramref name="name"/> names a browsable table.</summary>
    public static bool IsKnown(string name) => Find(name) is not null;

    private static Dictionary<string, RegistryTable> BuildIndex(IReadOnlyList<RegistryTable> tables)
    {
        var index = new Dictionary<string, RegistryTable>(tables.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
            index[table.Name] = table;

        return index;
    }

    private static List<RegistryTable> BuildAll() => new()
    {
        new RegistryTable(
            "ObjectClasses",
            "Object classes",
            ObjectClassesSql,
            "The object class hierarchy, one row per class, with its parent shown by qualified name."),

        new RegistryTable(
            "ObjectAttributes",
            "Attributes",
            ObjectAttributesSql,
            "Every attribute of every object class, with its owning class and full OMT column set."),

        new RegistryTable(
            "AttributeDimensions",
            "Attribute dimensions",
            AttributeDimensionsSql,
            "The dimensions each attribute is associated with, in declaration order."),

        new RegistryTable(
            "InteractionClasses",
            "Interaction classes",
            InteractionClassesSql,
            "The interaction class hierarchy, one row per class, with its parent shown by qualified name."),

        new RegistryTable(
            "InteractionParameters",
            "Parameters",
            InteractionParametersSql,
            "Every parameter of every interaction class, with its owning class."),

        new RegistryTable(
            "InteractionDimensions",
            "Interaction dimensions",
            InteractionDimensionsSql,
            "The dimensions each interaction class is associated with, in declaration order."),

        new RegistryTable(
            "DataTypes",
            "Datatypes",
            DataTypesSql,
            "Basic, simple, enumerated, array and record datatypes in one list, discriminated by kind."),

        new RegistryTable(
            "DataTypeMembers",
            "Datatype members",
            DataTypeMembersSql,
            "Enumerators, record fields and variant alternatives, each under its owning datatype."),

        new RegistryTable(
            "Dimensions",
            "Dimensions",
            DimensionsSql,
            "Routing dimensions with their datatype, upper bound and normalisation function."),

        new RegistryTable(
            "RoutingSpaces",
            "Routing spaces",
            RoutingSpacesSql,
            "HLA 1.3 routing spaces declared by the FOM."),

        new RegistryTable(
            "RoutingSpaceDimensions",
            "Routing space dimensions",
            RoutingSpaceDimensionsSql,
            "The dimensions belonging to each routing space, in declaration order."),

        new RegistryTable(
            "Transportations",
            "Transportations",
            TransportationsSql,
            "Transportation types and whether each one is reliable."),

        new RegistryTable(
            "Synchronizations",
            "Synchronizations",
            SynchronizationsSql,
            "Synchronisation points with their federate capability and tag datatype."),

        new RegistryTable(
            "UpdateRates",
            "Update rates",
            UpdateRatesSql,
            "Named update rates and the maximum rate each one permits."),

        new RegistryTable(
            "Switches",
            "Switches",
            SwitchesSql,
            "Federation switches and their enabled / resign settings."),

        new RegistryTable(
            "Tags",
            "Tags",
            TagsSql,
            "User-supplied tag datatypes, one row per tag kind."),

        new RegistryTable(
            "FomNotes",
            "Notes",
            FomNotesSql,
            "The notes table; other tables reference these entries by name."),

        new RegistryTable(
            "TimeRepresentation",
            "Time representation",
            TimeRepresentationSql,
            "The single time-representation row: timestamp and lookahead datatypes."),

        new RegistryTable(
            "FomIdentificationValues",
            "Identification lists",
            IdentificationValuesSql,
            "Keywords, points of contact and use history from the model identification block."),

        new RegistryTable(
            "Diagnostics",
            "Parse diagnostics",
            DiagnosticsSql,
            "Messages raised while reading the source file, in the order the parser produced them."),

        new RegistryTable(
            "Foms",
            "FOM header",
            FomHeaderSql,
            "The single header row: file details, model identification and element counts."),
    };

    // -------------------------------------------------------------------------------------------
    // Object model
    // -------------------------------------------------------------------------------------------

    private const string ObjectClassesSql = """
        SELECT COALESCE(c.QualifiedName, '') AS "Key",
               c.Name                        AS Name,
               c.QualifiedName               AS QualifiedName,
               p.QualifiedName               AS Parent,
               c.Sharing                     AS Sharing,
               c.Semantics                   AS Semantics,
               c.NoteRefs                    AS NoteRefs,
               c.Ordinal                     AS Ordinal
        FROM ObjectClasses c
        LEFT JOIN ObjectClasses p ON p.Id = c.ParentId
        WHERE c.FomId = @fomId
        ORDER BY c.QualifiedName, c.Ordinal, c.Id;
        """;

    // "Order" is a SQL keyword: it must stay quoted as the schema declares it, and the alias must be
    // an unquoted identifier, hence OrderToken — the same spelling LoadObjectAttributes already uses.
    private const string ObjectAttributesSql = """
        SELECT COALESCE(a.QualifiedName, '') AS "Key",
               o.QualifiedName               AS ObjectClass,
               a.Name                        AS Name,
               a.DataType                    AS DataType,
               a.Cardinality                 AS Cardinality,
               a.Units                       AS Units,
               a.Resolution                  AS Resolution,
               a.Accuracy                    AS Accuracy,
               a.AccuracyCondition           AS AccuracyCondition,
               a.UpdateType                  AS UpdateType,
               a.UpdateCondition             AS UpdateCondition,
               a.Ownership                   AS Ownership,
               a.Sharing                     AS Sharing,
               a.Transportation              AS Transportation,
               a."Order"                     AS OrderToken,
               a.RoutingSpace                AS RoutingSpace,
               a.Semantics                   AS Semantics,
               a.NoteRefs                    AS NoteRefs,
               a.Ordinal                     AS Ordinal
        FROM ObjectAttributes a
        JOIN ObjectClasses o ON o.Id = a.ObjectClassId
        WHERE a.FomId = @fomId
        ORDER BY a.QualifiedName, o.QualifiedName, a.Ordinal, a.Id;
        """;

    // AttributeDimensions carries no FomId of its own, so the owning attribute supplies the filter.
    private const string AttributeDimensionsSql = """
        SELECT COALESCE(a.QualifiedName, '') || ' / ' || COALESCE(d.DimensionName, '') AS "Key",
               a.QualifiedName AS Attribute,
               d.DimensionName AS Dimension,
               d.Ordinal       AS Ordinal
        FROM AttributeDimensions d
        JOIN ObjectAttributes a ON a.Id = d.AttributeId
        WHERE a.FomId = @fomId
        ORDER BY a.QualifiedName, d.Ordinal, d.Id;
        """;

    // -------------------------------------------------------------------------------------------
    // Interactions
    // -------------------------------------------------------------------------------------------

    private const string InteractionClassesSql = """
        SELECT COALESCE(c.QualifiedName, '') AS "Key",
               c.Name                        AS Name,
               c.QualifiedName               AS QualifiedName,
               p.QualifiedName               AS Parent,
               c.Sharing                     AS Sharing,
               c.Transportation              AS Transportation,
               c."Order"                     AS OrderToken,
               c.RoutingSpace                AS RoutingSpace,
               c.Semantics                   AS Semantics,
               c.NoteRefs                    AS NoteRefs,
               c.Ordinal                     AS Ordinal
        FROM InteractionClasses c
        LEFT JOIN InteractionClasses p ON p.Id = c.ParentId
        WHERE c.FomId = @fomId
        ORDER BY c.QualifiedName, c.Ordinal, c.Id;
        """;

    private const string InteractionParametersSql = """
        SELECT COALESCE(p.QualifiedName, '') AS "Key",
               c.QualifiedName               AS InteractionClass,
               p.Name                        AS Name,
               p.DataType                    AS DataType,
               p.Cardinality                 AS Cardinality,
               p.Units                       AS Units,
               p.Resolution                  AS Resolution,
               p.Accuracy                    AS Accuracy,
               p.AccuracyCondition           AS AccuracyCondition,
               p.Semantics                   AS Semantics,
               p.NoteRefs                    AS NoteRefs,
               p.Ordinal                     AS Ordinal
        FROM InteractionParameters p
        JOIN InteractionClasses c ON c.Id = p.InteractionClassId
        WHERE p.FomId = @fomId
        ORDER BY p.QualifiedName, c.QualifiedName, p.Ordinal, p.Id;
        """;

    // Like AttributeDimensions, this table is filtered through its owning class.
    private const string InteractionDimensionsSql = """
        SELECT COALESCE(c.QualifiedName, '') || ' / ' || COALESCE(d.DimensionName, '') AS "Key",
               c.QualifiedName AS InteractionClass,
               d.DimensionName AS Dimension,
               d.Ordinal       AS Ordinal
        FROM InteractionDimensions d
        JOIN InteractionClasses c ON c.Id = d.InteractionClassId
        WHERE c.FomId = @fomId
        ORDER BY c.QualifiedName, d.Ordinal, d.Id;
        """;

    // -------------------------------------------------------------------------------------------
    // Datatypes
    // -------------------------------------------------------------------------------------------

    // Kind is part of the key: the same name may legitimately appear under two different kinds.
    private const string DataTypesSql = """
        SELECT t.Kind || '/' || COALESCE(t.Name, '') AS "Key",
               t.Kind                 AS Kind,
               t.Name                 AS Name,
               t.Size                 AS Size,
               t.Interpretation       AS Interpretation,
               t.Endian               AS Endian,
               t.Encoding             AS Encoding,
               t.Representation       AS Representation,
               t.Units                AS Units,
               t.Resolution           AS Resolution,
               t.Accuracy             AS Accuracy,
               t.ElementDataType      AS ElementDataType,
               t.Cardinality          AS Cardinality,
               t.Discriminant         AS Discriminant,
               t.DiscriminantDataType AS DiscriminantDataType,
               t.IncludeRef           AS IncludeRef,
               t.Semantics            AS Semantics,
               t.NoteRefs             AS NoteRefs,
               t.Ordinal              AS Ordinal
        FROM DataTypes t
        WHERE t.FomId = @fomId
        ORDER BY t.Kind, t.Name, t.Ordinal, t.Id;
        """;

    // The owning datatype's name is shown as DataType; the member's own datatype reference has to be
    // aliased MemberDataType so the two do not collide in one row.
    private const string DataTypeMembersSql = """
        SELECT t.Kind || '/' || COALESCE(t.Name, '') || '/' || COALESCE(m.Name, '') AS "Key",
               t.Name         AS DataType,
               m.Kind         AS Kind,
               m.Name         AS Name,
               m.MemberValues AS MemberValues,
               m.DataType     AS MemberDataType,
               m.Enumerator   AS Enumerator,
               m.Semantics    AS Semantics,
               m.Ordinal      AS Ordinal
        FROM DataTypeMembers m
        JOIN DataTypes t ON t.Id = m.DataTypeId
        WHERE t.FomId = @fomId
        ORDER BY t.Kind, t.Name, m.Ordinal, m.Id;
        """;

    // -------------------------------------------------------------------------------------------
    // Routing
    // -------------------------------------------------------------------------------------------

    private const string DimensionsSql = """
        SELECT COALESCE(d.Name, '') AS "Key",
               d.Name          AS Name,
               d.DataType      AS DataType,
               d.UpperBound    AS UpperBound,
               d.Normalization AS Normalization,
               d.Value         AS Value,
               d.Semantics     AS Semantics,
               d.NoteRefs      AS NoteRefs,
               d.Ordinal       AS Ordinal
        FROM Dimensions d
        WHERE d.FomId = @fomId
        ORDER BY d.Name, d.Ordinal, d.Id;
        """;

    private const string RoutingSpacesSql = """
        SELECT COALESCE(s.Name, '') AS "Key",
               s.Name      AS Name,
               s.Semantics AS Semantics,
               s.NoteRefs  AS NoteRefs,
               s.Ordinal   AS Ordinal
        FROM RoutingSpaces s
        WHERE s.FomId = @fomId
        ORDER BY s.Name, s.Ordinal, s.Id;
        """;

    private const string RoutingSpaceDimensionsSql = """
        SELECT COALESCE(s.Name, '') || ' / ' || COALESCE(d.Name, '') AS "Key",
               s.Name    AS RoutingSpace,
               d.Name    AS Dimension,
               d.Ordinal AS Ordinal
        FROM RoutingSpaceDimensions d
        JOIN RoutingSpaces s ON s.Id = d.RoutingSpaceId
        WHERE s.FomId = @fomId
        ORDER BY s.Name, d.Ordinal, d.Id;
        """;

    // -------------------------------------------------------------------------------------------
    // Federation-wide tables
    // -------------------------------------------------------------------------------------------

    private const string TransportationsSql = """
        SELECT COALESCE(t.Name, '') AS "Key",
               t.Name      AS Name,
               t.Reliable  AS Reliable,
               t.Semantics AS Semantics,
               t.Ordinal   AS Ordinal
        FROM Transportations t
        WHERE t.FomId = @fomId
        ORDER BY t.Name, t.Ordinal, t.Id;
        """;

    private const string SynchronizationsSql = """
        SELECT COALESCE(s.Name, '') AS "Key",
               s.Name       AS Name,
               s.Capability AS Capability,
               s.DataType   AS DataType,
               s.Semantics  AS Semantics,
               s.NoteRefs   AS NoteRefs,
               s.Ordinal    AS Ordinal
        FROM Synchronizations s
        WHERE s.FomId = @fomId
        ORDER BY s.Name, s.Ordinal, s.Id;
        """;

    private const string UpdateRatesSql = """
        SELECT COALESCE(u.Name, '') AS "Key",
               u.Name      AS Name,
               u.Rate      AS Rate,
               u.Semantics AS Semantics,
               u.NoteRefs  AS NoteRefs,
               u.Ordinal   AS Ordinal
        FROM UpdateRates u
        WHERE u.FomId = @fomId
        ORDER BY u.Name, u.Ordinal, u.Id;
        """;

    private const string SwitchesSql = """
        SELECT COALESCE(s.Name, '') AS "Key",
               s.Name         AS Name,
               s.IsEnabled    AS IsEnabled,
               s.ResignSwitch AS ResignSwitch,
               s.Ordinal      AS Ordinal
        FROM Switches s
        WHERE s.FomId = @fomId
        ORDER BY s.Name, s.Ordinal, s.Id;
        """;

    private const string TagsSql = """
        SELECT COALESCE(t.Name, '') AS "Key",
               t.Name      AS Name,
               t.DataType  AS DataType,
               t.Semantics AS Semantics,
               t.NoteRefs  AS NoteRefs,
               t.Ordinal   AS Ordinal
        FROM Tags t
        WHERE t.FomId = @fomId
        ORDER BY t.Name, t.Ordinal, t.Id;
        """;

    private const string FomNotesSql = """
        SELECT COALESCE(n.Name, '') AS "Key",
               n.Name      AS Name,
               n.Label     AS Label,
               n.Text      AS Text,
               n.Semantics AS Semantics,
               n.Ordinal   AS Ordinal
        FROM FomNotes n
        WHERE n.FomId = @fomId
        ORDER BY n.Name, n.Ordinal, n.Id;
        """;

    // TimeRepresentation holds at most one row per FOM, keyed by FomId as its primary key, so a
    // constant key is enough to line the two sides up.
    private const string TimeRepresentationSql = """
        SELECT 'time' AS "Key",
               t.TimeStampDataType  AS TimeStampDataType,
               t.TimeStampSemantics AS TimeStampSemantics,
               t.LookaheadDataType  AS LookaheadDataType,
               t.LookaheadSemantics AS LookaheadSemantics
        FROM TimeRepresentation t
        WHERE t.FomId = @fomId
        ORDER BY t.FomId;
        """;

    private const string IdentificationValuesSql = """
        SELECT v.Kind || '/' || CAST(v.Ordinal AS TEXT) AS "Key",
               v.Kind    AS Kind,
               v.Ordinal AS Ordinal,
               v.Value   AS Value
        FROM FomIdentificationValues v
        WHERE v.FomId = @fomId
        ORDER BY v.Kind, v.Ordinal, v.Id;
        """;

    // Severity is stored as the DiagnosticSeverity ordinal; spell it out so the grid reads as prose.
    private const string DiagnosticsSql = """
        SELECT CAST(d.Ordinal AS TEXT) AS "Key",
               CASE d.Severity
                   WHEN 0 THEN 'Info'
                   WHEN 1 THEN 'Warning'
                   WHEN 2 THEN 'Error'
                   ELSE CAST(d.Severity AS TEXT)
               END        AS Severity,
               d.Message  AS Message,
               d.Line     AS Line,
               d.Path     AS Path,
               d.Ordinal  AS Ordinal
        FROM Diagnostics d
        WHERE d.FomId = @fomId
        ORDER BY d.Ordinal, d.Id;
        """;

    // Foms is the parent table, so the FOM is selected by Id rather than FomId; Standard is stored as
    // the FomStandard ordinal and is spelled out for the same reason as Diagnostics.Severity.
    private const string FomHeaderSql = """
        SELECT 'header' AS "Key",
               f.DisplayName AS DisplayName,
               f.FileName    AS FileName,
               CASE f.Standard
                   WHEN 1 THEN 'HLA 1.3'
                   WHEN 2 THEN 'IEEE 1516-2000'
                   WHEN 3 THEN 'IEEE 1516-2010'
                   WHEN 4 THEN 'IEEE 1516-2025'
                   ELSE 'Unknown'
               END AS Standard,
               f.SourceNamespace             AS SourceNamespace,
               f.IdentName                   AS IdentName,
               f.IdentType                   AS IdentType,
               f.IdentVersion                AS IdentVersion,
               f.IdentModificationDate       AS IdentModificationDate,
               f.IdentSecurityClassification AS IdentSecurityClassification,
               f.IdentPurpose                AS IdentPurpose,
               f.IdentApplicationDomain      AS IdentApplicationDomain,
               f.IdentDescription            AS IdentDescription,
               f.ObjectClassCount            AS ObjectClassCount,
               f.AttributeCount              AS AttributeCount,
               f.InteractionClassCount       AS InteractionClassCount,
               f.ParameterCount              AS ParameterCount,
               f.DataTypeCount               AS DataTypeCount,
               f.DimensionCount              AS DimensionCount,
               f.ErrorCount                  AS ErrorCount,
               f.WarningCount                AS WarningCount
        FROM Foms f
        WHERE f.Id = @fomId
        ORDER BY f.Id;
        """;
}
