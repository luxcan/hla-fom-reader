using System;
using System.Collections.Generic;

namespace HLAFomReader.Core.Model;

/// <summary>
/// What a class actually has, once inheritance is applied: the members it declares itself, and the
/// ones it gets from its ancestors.
/// </summary>
/// <remarks>
/// <para>
/// This is the single statement of a rule several places have to agree on — the FOM detail screen's
/// member table, the count columns on the exported hierarchy sheets, the rows on the exported member
/// sheets, and the class counts in the export dialog. A sheet whose totals disagree with the screen
/// it was exported from is worse than no sheet, and a dialog that promises 45 attributes and
/// delivers 12 is worse again.
/// </para>
/// <para>
/// Inheritance in the OMT is by name. Ancestors are walked root-first and a name already seen is
/// skipped, so a member redeclared on a subclass is counted once, against the ancestor that
/// introduced it, and keeps that ancestor's position in the list: a subclass that redeclares
/// <c>privilegeToDelete</c> has not gained an attribute. Ordinal comparison, because HLA names are
/// case-sensitive identifiers.
/// </para>
/// <para>
/// Effective rather than declared is the useful answer, and not by a small margin. RPR's
/// <c>Aircraft</c> declares none of its 45 attributes; a caller shown only what a class declares
/// would be told, truthfully and uselessly, that one of the most-used classes in the model is empty.
/// </para>
/// </remarks>
public static class FomInheritance
{
    /// <summary>Every member <paramref name="self"/> has, inherited ones first, each paired with the class that introduced it.</summary>
    /// <typeparam name="TOwner">The class type — object class or interaction class.</typeparam>
    /// <typeparam name="TMember">The member type — attribute or parameter.</typeparam>
    /// <param name="ancestors">Root-first path down to <paramref name="self"/>, excluding it.</param>
    /// <param name="self">The class whose effective members are wanted.</param>
    /// <param name="members">Reads the members a class declares itself.</param>
    /// <param name="name">Reads a member's name, which is what inheritance is keyed on.</param>
    /// <returns>
    /// Each member paired with its owner. A pair whose owner is <paramref name="self"/> is declared
    /// here; anything else is inherited.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static List<(TOwner Owner, TMember Member)> Effective<TOwner, TMember>(
        IReadOnlyList<TOwner> ancestors,
        TOwner self,
        Func<TOwner, IEnumerable<TMember>> members,
        Func<TMember, string> name)
        where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(name);

        var rows = new List<(TOwner, TMember)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ancestor in ancestors)
            foreach (var member in members(ancestor))
                if (seen.Add(name(member))) rows.Add((ancestor, member));

        foreach (var member in members(self))
            if (seen.Add(name(member))) rows.Add((self, member));

        return rows;
    }

    /// <summary>
    /// Every attribute <paramref name="objectClass"/> has, inherited ones first, each paired with
    /// the class that declared it.
    /// </summary>
    /// <remarks>
    /// Finds the ancestors by climbing <c>Parent</c>, so a caller holding one class needs nothing
    /// else. Callers already walking the tree from its roots should use <see cref="Effective"/> and
    /// hand over the path they have.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="objectClass"/> is null.</exception>
    public static List<(FomObjectClass Owner, FomAttribute Attribute)> EffectiveAttributes(FomObjectClass objectClass)
    {
        ArgumentNullException.ThrowIfNull(objectClass);

        return Effective(Ancestors(objectClass, c => c.Parent), objectClass, c => c.Attributes, a => a.Name);
    }

    /// <summary>The interaction equivalent of <see cref="EffectiveAttributes"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="interaction"/> is null.</exception>
    public static List<(FomInteractionClass Owner, FomParameter Parameter)> EffectiveParameters(FomInteractionClass interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        return Effective(Ancestors(interaction, c => c.Parent), interaction, c => c.Parameters, p => p.Name);
    }

    /// <summary>How many attributes <paramref name="objectClass"/> has, inherited ones included.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="objectClass"/> is null.</exception>
    public static int EffectiveAttributeCount(FomObjectClass objectClass) =>
        EffectiveAttributes(objectClass).Count;

    /// <summary>How many parameters <paramref name="interaction"/> has, inherited ones included.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="interaction"/> is null.</exception>
    public static int EffectiveParameterCount(FomInteractionClass interaction) =>
        EffectiveParameters(interaction).Count;

    /// <summary>
    /// The chain from the root down to, but not including, <paramref name="node"/>.
    /// </summary>
    /// <remarks>
    /// Climbs <c>Parent</c> and reverses, rather than searching down from the roots. The guard is a
    /// visited set rather than a depth limit because a malformed document that makes a class its own
    /// ancestor would otherwise walk for ever, and stopping at an arbitrary depth would silently
    /// report a wrong count instead of the best answer available.
    /// </remarks>
    private static List<T> Ancestors<T>(T node, Func<T, T?> parent) where T : class
    {
        var chain = new List<T>();
        var visited = new HashSet<T>(ReferenceEqualityComparer.Instance);

        for (var current = parent(node); current is not null && visited.Add(current); current = parent(current))
            chain.Add(current);

        chain.Reverse();
        return chain;
    }
}
