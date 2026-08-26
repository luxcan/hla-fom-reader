using System;
using System.Collections.Generic;
using System.Text;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// Pure spelling helpers that fold the OMT dialects onto one canonical form before matching.
/// </summary>
/// <remarks>
/// Every helper is driven by <see cref="ComparisonOptions"/>: with the corresponding option
/// switched off the input is handed back unchanged, so a "strict" comparison sees exactly what
/// the two files say. Nothing here looks at the model — the comparer decides where a folded
/// value is allowed to hide a difference and where it is not.
/// </remarks>
public static class OmtNormalizer
{
    /// <summary>Canonical spelling of the object class root (<c>ObjectRoot</c> in HLA 1.3).</summary>
    public const string ObjectRootName = "HLAobjectRoot";

    /// <summary>Canonical spelling of the interaction class root (<c>InteractionRoot</c> in HLA 1.3).</summary>
    public const string InteractionRootName = "HLAinteractionRoot";

    /// <summary>Canonical spelling of the delete-privilege attribute (<c>privilegeToDelete</c> in HLA 1.3).</summary>
    public const string PrivilegeToDeleteName = "HLAprivilegeToDeleteObject";

    /// <summary>Canonical spelling of the MOM sub-root (<c>Manager</c> in HLA 1.3).</summary>
    public const string ManagerName = "HLAmanager";

    /// <summary>Canonical reliable transportation token.</summary>
    public const string ReliableTransportation = "HLAreliable";

    /// <summary>Canonical best-effort transportation token.</summary>
    public const string BestEffortTransportation = "HLAbestEffort";

    /// <summary>Canonical time-stamp order token.</summary>
    public const string TimeStampOrder = "TimeStamp";

    /// <summary>Canonical receive order token.</summary>
    public const string ReceiveOrder = "Receive";

    /// <summary>Names that may appear as the first segment of a qualified class name.</summary>
    private static readonly Dictionary<string, string> RootAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ObjectRoot"] = ObjectRootName,
        ["HLAobjectRoot"] = ObjectRootName,
        ["InteractionRoot"] = InteractionRootName,
        ["HLAinteractionRoot"] = InteractionRootName,
    };

    /// <summary>The MOM sub-root, which sits directly under the class root.</summary>
    private static readonly Dictionary<string, string> ManagerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Manager"] = ManagerName,
        ["HLAmanager"] = ManagerName,
    };

    /// <summary>Aliases that apply to a plain (leaf) name such as an attribute.</summary>
    private static readonly Dictionary<string, string> LeafAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["privilegeToDelete"] = PrivilegeToDeleteName,
        ["HLAprivilegeToDeleteObject"] = PrivilegeToDeleteName,
    };

    /// <summary>Segments that count as the management object model, whatever the dialect.</summary>
    private static readonly string[] ManagementSegments = { ManagerName, "Manager" };

    /// <summary>
    /// Folds a single, undotted name onto its canonical 1516 spelling. Dotted input is handed to
    /// <see cref="NormalizeQualifiedName"/> so callers never have to pick the right helper.
    /// </summary>
    /// <returns>
    /// The canonical spelling, the trimmed input when no alias applies, or the input untouched
    /// when <see cref="ComparisonOptions.NormalizeRootNames"/> is off.
    /// </returns>
    public static string? NormalizeName(string? name, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.NormalizeRootNames) return name;
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (name.IndexOf('.') >= 0) return NormalizeQualifiedName(name, options);

        var trimmed = name.Trim();
        if (RootAliases.TryGetValue(trimmed, out var root)) return root;
        if (LeafAliases.TryGetValue(trimmed, out var leaf)) return leaf;
        if (ManagerAliases.TryGetValue(trimmed, out var manager)) return manager;
        return trimmed;
    }

    /// <summary>
    /// Folds a dotted qualified name segment by segment so that, for example,
    /// <c>ObjectRoot.Manager.Federate</c> and <c>HLAobjectRoot.HLAmanager.Federate</c> line up.
    /// </summary>
    /// <remarks>
    /// The root aliases apply to the first segment only, the MOM alias to the segment that
    /// directly follows the root, and the leaf alias to the final segment — so a class that
    /// happens to be called <c>ObjectRoot</c> half way down a tree is left alone.
    /// </remarks>
    public static string? NormalizeQualifiedName(string? qualifiedName, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.NormalizeRootNames) return qualifiedName;
        if (string.IsNullOrWhiteSpace(qualifiedName)) return qualifiedName;

        var segments = qualifiedName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();

            if (i == 0 && RootAliases.TryGetValue(segment, out var root))
                segment = root;
            else if (i == 1 && ManagerAliases.TryGetValue(segment, out var manager))
                segment = manager;

            if (i == segments.Length - 1 && LeafAliases.TryGetValue(segment, out var leaf))
                segment = leaf;

            // A bare "Manager" handed in on its own is still the MOM sub-root.
            if (segments.Length == 1 && ManagerAliases.TryGetValue(segment, out var loneManager))
                segment = loneManager;

            segments[i] = segment;
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// Folds a transportation token onto <c>HLAreliable</c> / <c>HLAbestEffort</c>, ignoring case
    /// and any underscores, so a 1.3 FED and a 1516 FOM compare on meaning rather than spelling.
    /// </summary>
    public static string? NormalizeTransportation(string? transportation, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.NormalizeTransportAndOrder) return transportation;
        var token = Fold(transportation);
        if (token is null) return null;

        return token switch
        {
            "reliable" or "hlareliable" => ReliableTransportation,
            "besteffort" or "hlabesteffort" => BestEffortTransportation,
            _ => transportation!.Trim(),
        };
    }

    /// <summary>
    /// Folds an ordering token onto <c>TimeStamp</c> / <c>Receive</c>, ignoring case and underscores.
    /// </summary>
    public static string? NormalizeOrder(string? order, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.NormalizeTransportAndOrder) return order;
        var token = Fold(order);
        if (token is null) return null;

        return token switch
        {
            "timestamp" or "hlatimestamp" => TimeStampOrder,
            "receive" or "hlareceive" => ReceiveOrder,
            _ => order!.Trim(),
        };
    }

    /// <summary>
    /// Trims prose and, when <see cref="ComparisonOptions.NormalizeWhitespace"/> is on, collapses
    /// runs of whitespace to a single space so re-indented XML does not read as a change.
    /// </summary>
    /// <returns>Null when the input is null or contains nothing but whitespace.</returns>
    public static string? NormalizeText(string? text, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        if (!options.NormalizeWhitespace) return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var pendingSpace = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when any segment of the dotted name is the management object model root
    /// (<c>HLAmanager</c> or its HLA 1.3 spelling <c>Manager</c>), which also catches the
    /// whole MOM subtree below it.
    /// </summary>
    public static bool IsManagementClass(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName)) return false;

        foreach (var segment in qualifiedName.Split('.'))
        {
            var trimmed = segment.Trim();
            foreach (var management in ManagementSegments)
            {
                if (string.Equals(trimmed, management, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Lower-cases a token and drops separators, for tolerant keyword matching.</summary>
    private static string? Fold(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var builder = new StringBuilder(token.Length);
        foreach (var c in token)
        {
            if (c is '_' or '-' || char.IsWhiteSpace(c)) continue;
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
