using System;
using System.Collections.Generic;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Reporting;

/// <summary>One class as the export sees it: the class, how deep it sits, and the path down to it.</summary>
/// <typeparam name="T">
/// <see cref="FomObjectClass"/> or <see cref="FomInteractionClass"/>. The two are separate types
/// with no common base beyond <see cref="FomNode"/>, which carries neither children nor members.
/// </typeparam>
/// <param name="Class">The class itself.</param>
/// <param name="Level">Depth, counting the root as 1 — the number the hierarchy sheet writes.</param>
/// <param name="Ancestors">
/// Every class from the root down to, but not including, <paramref name="Class"/>. Root first, so
/// walking it accumulates inherited members in the order the FOM declares them.
/// </param>
internal readonly record struct ClassPath<T>(T Class, int Level, IReadOnlyList<T> Ancestors);

/// <summary>
/// Depth-first pre-order walks of a document's two class trees, shared by every sheet the export
/// writes.
/// </summary>
/// <remarks>
/// <para>
/// One walk rather than one per sheet, because the sheets have to agree with each other. The
/// hierarchy sheet's merges are only rectangles because pre-order puts a class immediately before
/// its descendants and nothing else, and the member sheet's rows are only in the same order as the
/// hierarchy sheet's because it is the same order. Two copies of that traversal would be two
/// chances to drift apart, and the workbook would say two different things about one FOM.
/// </para>
/// <para>
/// The cycle and depth guards live here for the same reason. A class tree is a tree, but a
/// hand-assembled or malformed document can contain a cycle, and a guard that only some of the
/// sheets applied would leave one of them spinning on a file the others survived.
/// </para>
/// </remarks>
internal static class ClassWalk
{
    /// <summary>
    /// Recursion limit. Deep enough that no real FOM comes close — RPR reaches eight — and shallow
    /// enough that a cycle the visited-set somehow missed still terminates.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>The object class tree, depth-first, roots in declaration order.</summary>
    public static List<ClassPath<FomObjectClass>> Objects(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Walk<FomObjectClass>(document.ObjectClasses, c => c.Children);
    }

    /// <summary>The interaction class tree, depth-first, roots in declaration order.</summary>
    public static List<ClassPath<FomInteractionClass>> Interactions(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Walk<FomInteractionClass>(document.InteractionClasses, c => c.Children);
    }

    private static List<ClassPath<T>> Walk<T>(IEnumerable<T?> roots, Func<T, IEnumerable<T?>> children)
        where T : class
    {
        var paths = new List<ClassPath<T>>();
        var visited = new HashSet<T>(ReferenceEqualityComparer.Instance);
        var ancestors = new List<T>();

        foreach (var root in roots)
            Visit(root, ancestors, paths, visited, children);

        return paths;
    }

    private static void Visit<T>(
        T? node,
        List<T> ancestors,
        List<ClassPath<T>> paths,
        HashSet<T> visited,
        Func<T, IEnumerable<T?>> children)
        where T : class
    {
        if (node is null || ancestors.Count >= MaxDepth || !visited.Add(node)) return;

        paths.Add(new ClassPath<T>(node, ancestors.Count + 1, ancestors.ToArray()));

        ancestors.Add(node);
        foreach (var child in children(node))
            Visit(child, ancestors, paths, visited, children);
        ancestors.RemoveAt(ancestors.Count - 1);
    }
}
