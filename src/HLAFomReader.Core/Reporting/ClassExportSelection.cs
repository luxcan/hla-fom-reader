using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Reporting;

/// <summary>
/// The classes a user ticked before exporting: the object classes whose attributes they want
/// written out, and the interaction classes whose parameters they want written out.
/// </summary>
/// <remarks>
/// <para>
/// Classes are named rather than referenced. The selection is made against one document and used
/// against the same one, so a reference would work — but it would also make the selection
/// unspeakable outside the process that built it, and the obvious next asks (remember the last
/// selection; apply the same one to the other FOM in a comparison) both want a name. A qualified
/// name identifies a class across two FOMs; an object reference identifies it in exactly one.
/// </para>
/// <para>
/// Matching accepts either the qualified name or the local one, because the two sources of a
/// selection disagree about which they hold. The dialog walks the same document the exporter will
/// and can offer the qualified name; a caller assembling a selection by hand — a test, a saved
/// preset — usually has only the short one. Ordinal comparison throughout: HLA names are
/// case-sensitive identifiers, so <c>Aircraft</c> and <c>aircraft</c> may legally be two classes.
/// </para>
/// <para>
/// An empty selection is the normal case rather than a degenerate one. It means "just the
/// hierarchies", which is exactly what this export produced before there was anything to tick.
/// </para>
/// </remarks>
public sealed class ClassExportSelection
{
    private readonly HashSet<string> _objectClasses;
    private readonly HashSet<string> _interactionClasses;

    /// <summary>Nothing ticked: the workbook is the two hierarchy sheets and nothing else.</summary>
    public static ClassExportSelection None { get; } = new(null, null);

    /// <summary>Builds a selection from the names the user ticked.</summary>
    /// <param name="objectClasses">
    /// Object classes to write attributes for, by qualified or local name. Null is an empty set.
    /// </param>
    /// <param name="interactionClasses">
    /// Interaction classes to write parameters for, by qualified or local name. Null is an empty set.
    /// </param>
    public ClassExportSelection(
        IEnumerable<string>? objectClasses,
        IEnumerable<string>? interactionClasses)
    {
        _objectClasses = Freeze(objectClasses);
        _interactionClasses = Freeze(interactionClasses);
    }

    /// <summary>Names of the object classes to write attributes for.</summary>
    public IReadOnlyCollection<string> ObjectClasses => _objectClasses;

    /// <summary>Names of the interaction classes to write parameters for.</summary>
    public IReadOnlyCollection<string> InteractionClasses => _interactionClasses;

    /// <summary>True when nothing at all was ticked.</summary>
    public bool IsEmpty => _objectClasses.Count == 0 && _interactionClasses.Count == 0;

    /// <summary>How many classes were ticked, of both kinds together.</summary>
    public int Count => _objectClasses.Count + _interactionClasses.Count;

    /// <summary>True when this selection names <paramref name="objectClass"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="objectClass"/> is null.</exception>
    public bool Includes(FomObjectClass objectClass) => Matches(_objectClasses, objectClass);

    /// <summary>True when this selection names <paramref name="interactionClass"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="interactionClass"/> is null.</exception>
    public bool Includes(FomInteractionClass interactionClass) => Matches(_interactionClasses, interactionClass);

    private static bool Matches(HashSet<string> names, FomNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return names.Count != 0
            && (names.Contains(node.QualifiedName) || names.Contains(node.Name));
    }

    /// <summary>
    /// Copies the names into a set, so a selection cannot change under the exporter and a document
    /// with a thousand classes does not cost a thousand list scans.
    /// </summary>
    private static HashSet<string> Freeze(IEnumerable<string>? names) =>
        names is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.Ordinal);
}
