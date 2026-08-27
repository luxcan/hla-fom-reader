using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Reporting;

/// <summary>
/// Renders the members of the classes a user picked: one sheet of attributes for the object
/// classes they ticked, one sheet of parameters for the interaction classes.
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy sheets answer "what is the shape of this model"; these answer "and what is
/// actually in the parts of it I care about". Both questions come up in the same sitting when
/// planning a remap, and the second is the one that cannot be answered from the screen, because it
/// spans classes the tree only shows one at a time.
/// </para>
/// <para>
/// Rows carry the <em>effective</em> members — everything inherited, then everything declared here
/// — because that is what a federate sees and what the detail screen shows. RPR's <c>Aircraft</c>
/// declares none of its 45 attributes; a sheet of declared attributes would have it blank. The
/// <b>Inherited</b> column keeps the distinction available to a filter rather than throwing it
/// away.
/// </para>
/// <para>
/// Nothing is merged, and a class's name repeats on every one of its rows. That is the opposite of
/// what <see cref="ClassHierarchyExporter"/> does, on purpose. A hierarchy sheet is a picture, and
/// merging is what makes the families legible; a member sheet is a table somebody is going to sort
/// by datatype and filter by ownership, and Excel refuses to sort a range that takes in merged
/// cells of differing heights. A repeated name costs a column of duplication and buys back every
/// operation the sheet exists for.
/// </para>
/// <para>
/// A ticked class with no effective members still gets a row, with the member columns empty. That
/// is the ordinary case for an interaction — <c>HLAinteractionRoot</c> declares no parameters, and
/// neither do many of its children — and a class that silently vanished from a sheet the user
/// ticked it into reads as a bug rather than as an answer.
/// </para>
/// </remarks>
public static class ClassMemberExporter
{
    /// <summary>Caption of the object class attribute tab.</summary>
    public const string AttributeSheetName = "Object Class Attributes";

    /// <summary>Caption of the interaction class parameter tab.</summary>
    public const string ParameterSheetName = "Interaction Class Parameters";

    /// <summary>Builds the member sheets for <paramref name="selection"/>, in tab order.</summary>
    /// <param name="document">The FOM the selection was made against.</param>
    /// <param name="selection">The classes the user ticked.</param>
    /// <returns>
    /// Nought, one or two sheets. A kind that was not ticked, or whose ticked classes are not in
    /// this document, contributes no sheet at all rather than an empty one — a tab that only ever
    /// says "nothing here" is a tab the reader has to open to find that out.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<XlsxSheet> BuildSheets(FomDocument document, ClassExportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);

        var sheets = new List<XlsxSheet>(2);

        if (selection.ObjectClasses.Count > 0)
        {
            var picked = ClassWalk.Objects(document).Where(p => selection.Includes(p.Class)).ToList();
            if (picked.Count > 0) sheets.Add(AttributeSheet(picked));
        }

        if (selection.InteractionClasses.Count > 0)
        {
            var picked = ClassWalk.Interactions(document).Where(p => selection.Includes(p.Class)).ToList();
            if (picked.Count > 0) sheets.Add(ParameterSheet(picked));
        }

        return sheets;
    }

    // ------------------------------------------------------------------- object class attributes

    /// <summary>Columns of the attribute sheet, in order. The first five identify the row.</summary>
    private static readonly (string Header, double Width)[] AttributeColumns =
    {
        ("Class", 22), ("Qualified name", 40), ("Attribute", 26), ("Declared in", 22), ("Inherited", 10),
        ("DataType", 24), ("Cardinality", 12), ("Units", 14), ("Resolution", 12), ("Accuracy", 12),
        ("Accuracy condition", 18), ("UpdateType", 13), ("Update condition", 18), ("Ownership", 14),
        ("Sharing", 16), ("Transportation", 16), ("Order", 12), ("Dimensions", 18),
        ("Routing space", 16), ("Semantics", 60),
    };

    private static XlsxSheet AttributeSheet(IReadOnlyList<ClassPath<FomObjectClass>> picked)
    {
        var sheet = NewSheet(AttributeSheetName, AttributeColumns);

        foreach (var path in picked)
        {
            var effective = FomInheritance.Effective(path.Ancestors, path.Class, c => c.Attributes, a => a.Name);

            if (effective.Count == 0)
            {
                sheet.Rows.Add(Identity(path.Class, AttributeColumns.Length));
                continue;
            }

            foreach (var (owner, attribute) in effective)
            {
                sheet.Rows.Add(new[]
                {
                    XlsxCell.Str(path.Class.Name),
                    XlsxCell.Str(QualifiedName(path.Class)),
                    XlsxCell.Str(attribute.Name),
                    XlsxCell.Str(owner.Name),
                    XlsxCell.Str(YesNo(!ReferenceEquals(owner, path.Class))),
                    XlsxCell.Str(attribute.DataType),
                    XlsxCell.Str(attribute.Cardinality),
                    XlsxCell.Str(attribute.Units),
                    XlsxCell.Str(attribute.Resolution),
                    XlsxCell.Str(attribute.Accuracy),
                    XlsxCell.Str(attribute.AccuracyCondition),
                    XlsxCell.Str(attribute.UpdateType),
                    XlsxCell.Str(attribute.UpdateCondition),
                    XlsxCell.Str(attribute.Ownership),
                    XlsxCell.Str(attribute.Sharing),
                    XlsxCell.Str(attribute.Transportation),
                    XlsxCell.Str(attribute.Order),
                    XlsxCell.Str(Join(attribute.Dimensions)),
                    XlsxCell.Str(attribute.RoutingSpace),
                    XlsxCell.Str(attribute.Semantics),
                });
            }
        }

        return sheet;
    }

    // ------------------------------------------------------------------- interaction parameters

    /// <summary>
    /// Columns of the parameter sheet. Shorter than the attribute sheet's because a parameter is:
    /// the OMT hangs transportation, order, dimensions and routing space off the interaction class
    /// itself — where the hierarchy sheet already reports on it — rather than off each parameter.
    /// </summary>
    private static readonly (string Header, double Width)[] ParameterColumns =
    {
        ("Class", 24), ("Qualified name", 44), ("Parameter", 26), ("Declared in", 24), ("Inherited", 10),
        ("DataType", 24), ("Cardinality", 12), ("Units", 14), ("Resolution", 12), ("Accuracy", 12),
        ("Accuracy condition", 18), ("Semantics", 60),
    };

    private static XlsxSheet ParameterSheet(IReadOnlyList<ClassPath<FomInteractionClass>> picked)
    {
        var sheet = NewSheet(ParameterSheetName, ParameterColumns);

        foreach (var path in picked)
        {
            var effective = FomInheritance.Effective(path.Ancestors, path.Class, c => c.Parameters, p => p.Name);

            if (effective.Count == 0)
            {
                sheet.Rows.Add(Identity(path.Class, ParameterColumns.Length));
                continue;
            }

            foreach (var (owner, parameter) in effective)
            {
                sheet.Rows.Add(new[]
                {
                    XlsxCell.Str(path.Class.Name),
                    XlsxCell.Str(QualifiedName(path.Class)),
                    XlsxCell.Str(parameter.Name),
                    XlsxCell.Str(owner.Name),
                    XlsxCell.Str(YesNo(!ReferenceEquals(owner, path.Class))),
                    XlsxCell.Str(parameter.DataType),
                    XlsxCell.Str(parameter.Cardinality),
                    XlsxCell.Str(parameter.Units),
                    XlsxCell.Str(parameter.Resolution),
                    XlsxCell.Str(parameter.Accuracy),
                    XlsxCell.Str(parameter.AccuracyCondition),
                    XlsxCell.Str(parameter.Semantics),
                });
            }
        }

        return sheet;
    }

    // ------------------------------------------------------------------- shared shape

    private static XlsxSheet NewSheet(string name, (string Header, double Width)[] columns)
    {
        var sheet = new XlsxSheet(name);

        sheet.Rows.Add(columns.Select(c => XlsxCell.Head(c.Header)).ToArray());
        sheet.ColumnWidths.AddRange(columns.Select(c => c.Width));

        return sheet;
    }

    /// <summary>The row a class with no effective members gets: who it is, and nothing else.</summary>
    private static IReadOnlyList<XlsxCell> Identity(FomNode node, int width)
    {
        var cells = new List<XlsxCell>(width)
        {
            XlsxCell.Str(node.Name),
            XlsxCell.Str(QualifiedName(node)),
        };

        // Trailing empties cost nothing in the file, but writing them keeps the row the same shape
        // as every other row on the sheet, which is what a reader of this code will expect.
        while (cells.Count < width) cells.Add(XlsxCell.Empty);

        return cells;
    }

    /// <summary>
    /// Written as words rather than TRUE/FALSE, because this column exists to be filtered on and
    /// Excel's filter list reads better as Yes/No than as a boolean it will also offer to total.
    /// </summary>
    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string? Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : string.Join(", ", values);

    /// <summary>The dotted path, falling back to the local name when the parser did not record one.</summary>
    private static string QualifiedName(FomNode node) =>
        string.IsNullOrWhiteSpace(node.QualifiedName) ? node.Name : node.QualifiedName;
}
