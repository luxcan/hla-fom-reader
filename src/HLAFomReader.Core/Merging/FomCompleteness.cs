using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Merging;

/// <summary>One datatype a document uses but does not define, and the elements that reach for it.</summary>
/// <param name="DataType">The datatype name as written on the attribute or parameter.</param>
/// <param name="UsedBy">Qualified names of the elements referencing it, capped for readability.</param>
/// <param name="UseCount">How many elements reference it in total.</param>
public sealed record MissingDataType(string DataType, IReadOnlyList<string> UsedBy, int UseCount);

/// <summary>What a completeness check found.</summary>
public sealed class FomCompletenessReport
{
    /// <summary>Datatypes referenced but never defined, most-used first.</summary>
    public IReadOnlyList<MissingDataType> MissingDataTypes { get; init; } = Array.Empty<MissingDataType>();

    /// <summary>Distinct datatype names the document references.</summary>
    public int ReferencedCount { get; init; }

    /// <summary>Datatype definitions the document carries.</summary>
    public int DefinedCount { get; init; }

    /// <summary>True when every datatype an element reaches for is defined somewhere in the document.</summary>
    public bool IsComplete => MissingDataTypes.Count == 0;

    /// <summary>One line for the registration dialogue and the registry row.</summary>
    public string Summary
    {
        get
        {
            if (IsComplete) return $"Complete — all {ReferencedCount} datatypes referenced are defined.";

            var missing = MissingDataTypes.Count;
            var elements = MissingDataTypes.Sum(m => m.UseCount);

            return $"{missing} datatype{(missing == 1 ? "" : "s")} used but not defined, "
                 + $"reached by {elements} element{(elements == 1 ? "" : "s")}. A module is missing.";
        }
    }
}

/// <summary>
/// Answers whether a FOM stands on its own, or is a module still missing the ones it was written
/// against.
/// </summary>
/// <remarks>
/// <para>
/// The obvious check — read each module's <c>&lt;reference&gt;</c> dependency entries and confirm
/// they are all present — does not work. The identification strings are free text and match nothing:
/// NETN-Physical asks for <c>RPR-Physical_v2.0</c>, which is no file MAK ships, while the merged
/// RPR 2.0 that does satisfy it is filed under a completely different name.
/// </para>
/// <para>
/// Datatypes answer it properly. Every attribute and parameter names the type it carries, and a name
/// with no definition behind it is a fact about the document rather than an inference about the
/// author's intent — it needs no matching, and it names what to go and find. Compiled alone,
/// NETN-Physical reaches for seven types it cannot resolve; adding NETN-BASE resolves every one.
/// </para>
/// <para>
/// The check is deliberately one-sided. A type defined and never used is not a problem — a base
/// module carries plenty that only its extensions reach for — so nothing is reported for it.
/// </para>
/// </remarks>
public static class FomCompleteness
{
    /// <summary>Most referencing elements listed per missing datatype.</summary>
    private const int MaxUsesListed = 5;

    /// <summary>
    /// Values that mean "no datatype here" rather than naming one.
    /// </summary>
    /// <remarks>
    /// Real FOMs write a placeholder into <c>dataType</c> where the field does not apply — the
    /// Restaurant samples use <c>NA</c>. Treating one as a type name reports every FOM that does it
    /// as missing a module called NA.
    /// </remarks>
    private static readonly HashSet<string> NotADataType =
        new(new[] { "NA", "N/A", "-", "--", "none", "n.a." }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks that every datatype the document reaches for is defined in it.
    /// </summary>
    /// <param name="document">The document to check, normally the result of a module merge.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static FomCompletenessReport Check(FomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Judged on the standard, not on whether a datatype table happens to be present. A 1.3 pair
        // registered with its OMT does carry one — SupportsDataTypes is true for it — but the types
        // its attributes name are prose the OMT wrote ("float", "unsigned integer"), not references
        // to the definitions beside them. Every one would report as missing, condemning a pair that
        // is in fact as complete as HLA 1.3 gets. Whether a 1.3 entry has its types is answered
        // elsewhere, by whether the OMT companion was registered at all.
        if (document.Standard is FomStandard.Hla13 or FomStandard.Unknown)
            return new FomCompletenessReport();

        var defined = new HashSet<string>(
            document.DataTypes.AllDataTypes().Select(t => t.Name), StringComparer.Ordinal);

        var uses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (dataType, owner) in References(document))
        {
            if (NotADataType.Contains(dataType)) continue;

            referenced.Add(dataType);
            if (defined.Contains(dataType) || IsProvidedByTheMim(dataType)) continue;

            counts[dataType] = counts.GetValueOrDefault(dataType) + 1;

            var list = uses.TryGetValue(dataType, out var existing) ? existing : uses[dataType] = new List<string>();
            if (list.Count < MaxUsesListed) list.Add(owner);
        }

        var missing = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MissingDataType(pair.Key, uses[pair.Key], pair.Value))
            .ToList();

        return new FomCompletenessReport
        {
            MissingDataTypes = missing,
            ReferencedCount = referenced.Count,
            DefinedCount = defined.Count,
        };
    }

    /// <summary>
    /// True for a datatype the RTI supplies rather than the FOM.
    /// </summary>
    /// <remarks>
    /// Every federation loads <c>HLAstandardMIM</c> whether or not anything asks for it, so the
    /// types it defines are always resolvable and a FOM referencing one is not incomplete. All 53 of
    /// them are <c>HLA</c>-prefixed, which is not a coincidence worth being nervous about: IEEE 1516
    /// reserves that prefix for elements the standard itself defines, so matching on it is matching
    /// on the rule rather than on a list that would go stale.
    ///
    /// Without this the check condemns almost every real FOM. An author who uses HLAunicodeString —
    /// which is the normal way to carry a string — would be told a module is missing, and the module
    /// named would be one the RTI was always going to provide.
    /// </remarks>
    private static bool IsProvidedByTheMim(string dataType) =>
        dataType.StartsWith("HLA", StringComparison.Ordinal);

    /// <summary>Every datatype reference in the document, with the element that makes it.</summary>
    private static IEnumerable<(string DataType, string Owner)> References(FomDocument document)
    {
        foreach (var objectClass in document.AllObjectClasses())
        {
            foreach (var attribute in objectClass.Attributes)
            {
                if (!string.IsNullOrWhiteSpace(attribute.DataType))
                    yield return (attribute.DataType!.Trim(), $"{objectClass.QualifiedName}.{attribute.Name}");
            }
        }

        foreach (var interaction in document.AllInteractionClasses())
        {
            foreach (var parameter in interaction.Parameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.DataType))
                    yield return (parameter.DataType!.Trim(), $"{interaction.QualifiedName}.{parameter.Name}");
            }
        }

        // A record's fields and an array's element type are references in their own right: a module
        // can define a record whose fields are typed by something only another module defines, and
        // leaving those out would call such a document complete while it could not be encoded.
        foreach (var record in document.DataTypes.FixedRecordDataTypes)
        {
            foreach (var field in record.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.DataType))
                    yield return (field.DataType!.Trim(), $"{record.Name}.{field.Name}");
            }
        }

        foreach (var array in document.DataTypes.ArrayDataTypes)
        {
            if (!string.IsNullOrWhiteSpace(array.DataType))
                yield return (array.DataType!.Trim(), array.Name);
        }
    }
}
