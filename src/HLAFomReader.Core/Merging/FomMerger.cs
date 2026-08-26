using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Merging;

/// <summary>
/// Combines a parsed HLA 1.3 <c>.fed</c> with its parsed <c>.omt</c> / <c>.omd</c> companion into one
/// complete 1.3 document.
/// </summary>
/// <remarks>
/// <para>
/// Neither 1.3 file says everything. The FED is what the RTI loads, so it is the spine: its class and
/// interaction trees, transportation, order and routing spaces are taken as authoritative and are
/// never overwritten. The OMT is the document the RTI never reads, and it is the only place
/// datatypes, cardinality, units, ownership, sharing, prose and the datatype tables exist, so it is
/// overlaid onto the spine wherever the FED left a property empty.
/// </para>
/// <para>
/// The two files are structured differently and matching has to allow for it: the FED tree is rooted
/// at <c>ObjectRoot</c> / <c>InteractionRoot</c>, while the OMT has no such wrapper — its topmost
/// class is the first real class. So FED <c>ObjectRoot.BaseEntity.Aircraft</c> is OMT
/// <c>BaseEntity.Aircraft</c>, and the leading root segment is stripped before any lookup.
/// </para>
/// <para>
/// Nothing the caller passed in is mutated: the FED is deep-copied into the merged document and the
/// values taken from the OMT are copied by value, so both inputs remain exactly as they were parsed.
/// Content problems are never thrown — they become <see cref="ParseDiagnostic"/>s on the merged
/// document, alongside the diagnostics carried over from both inputs.
/// </para>
/// </remarks>
public static class FomMerger
{
    /// <summary>
    /// Most mismatch entries reported per list. A drifted pair can mismatch in the hundreds, and a
    /// registration dialogue needs a readable sample rather than the whole flood.
    /// </summary>
    private const int MaxReportedMismatches = 50;

    /// <summary>
    /// Merges the OMT's meaning onto the FED's structure.
    /// </summary>
    /// <param name="fed">The parsed <c>.fed</c>. Supplies the structure and is never modified.</param>
    /// <param name="omt">The parsed <c>.omt</c> / <c>.omd</c>. Supplies datatypes and prose, and is never modified.</param>
    /// <returns>
    /// The merged document together with the enrichment counts and the elements that failed to line up.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fed"/> or <paramref name="omt"/> is null.</exception>
    public static FomMergeResult Merge(FomDocument fed, FomDocument omt)
    {
        ArgumentNullException.ThrowIfNull(fed);
        ArgumentNullException.ThrowIfNull(omt);

        var merged = CloneSpine(fed);
        var result = new FomMergeResult { Document = merged };

        // Gathered in full and capped only at the end, so the diagnostics can state the real totals.
        var fedOnly = new List<string>();
        var omtOnly = new List<string>();

        MergeObjectClasses(merged, omt, result, fedOnly, omtOnly);
        MergeInteractionClasses(merged, omt, result, fedOnly, omtOnly);

        CopyTablesOnlyTheOmtHas(merged, omt);
        MergeIdentification(merged.Identification, omt.Identification);

        Cap(result.UnmatchedInFed, fedOnly);
        Cap(result.UnmatchedInOmt, omtOnly);

        Report(result, fed, omt, fedOnly.Count, omtOnly.Count);

        // Both files' own findings still matter after the merge, so nothing is dropped.
        foreach (var diagnostic in fed.Diagnostics)
            merged.Diagnostics.Add(Clone(diagnostic));
        foreach (var diagnostic in omt.Diagnostics)
            merged.Diagnostics.Add(Clone(diagnostic));

        return result;
    }

    // ------------------------------------------------------------------- object classes

    /// <summary>
    /// Walks the merged (FED-shaped) class tree, enriching each class the OMT describes and recording
    /// both directions of mismatch.
    /// </summary>
    private static void MergeObjectClasses(
        FomDocument merged, FomDocument omt, FomMergeResult result, List<string> fedOnly, List<string> omtOnly)
    {
        var index = new NameIndex<FomObjectClass>(omt.AllObjectClasses());
        var matched = new HashSet<FomObjectClass>();

        // A FED class the OMT never described is still "accounted for" when it is the tree root, so
        // that ObjectRoot's absence from the OMT does not suppress the report on everything below it.
        var accounted = new HashSet<FomObjectClass>();

        foreach (var root in merged.ObjectClasses)
        {
            // Pre-order, so a parent is always decided before its children are examined.
            foreach (var fedClass in root.DescendantsAndSelf())
            {
                var match = index.Find(fedClass);

                if (match is null)
                {
                    if (fedClass.Parent is null)
                    {
                        // The FED's ObjectRoot has no OMT counterpart by construction; not a mismatch.
                        accounted.Add(fedClass);
                        continue;
                    }

                    // Report a subtree once, at the topmost class that went missing.
                    if (accounted.Contains(fedClass.Parent))
                        fedOnly.Add($"Object class '{fedClass.QualifiedName}'");

                    continue;
                }

                accounted.Add(fedClass);
                matched.Add(match);

                if (EnrichObjectClass(fedClass, match))
                    result.EnrichedClassCount++;

                MergeAttributes(fedClass, match, result, fedOnly, omtOnly);
            }
        }

        foreach (var omtClass in omt.AllObjectClasses())
        {
            if (matched.Contains(omtClass))
                continue;

            if (omtClass.Parent is not null && !matched.Contains(omtClass.Parent))
                continue;

            omtOnly.Add($"Object class '{omtClass.QualifiedName}'");
        }
    }

    /// <summary>Matches attributes by name inside one already-matched class pair.</summary>
    private static void MergeAttributes(
        FomObjectClass fedClass, FomObjectClass omtClass, FomMergeResult result, List<string> fedOnly, List<string> omtOnly)
    {
        var byName = BuildNameMap(omtClass.Attributes);
        var matched = new HashSet<FomAttribute>();

        foreach (var attribute in fedClass.Attributes)
        {
            if (byName.TryGetValue(attribute.Name, out var source))
            {
                matched.Add(source);

                if (EnrichAttribute(attribute, source))
                    result.EnrichedAttributeCount++;

                continue;
            }

            // The delete privilege is an RTI-supplied attribute; the OMT commonly omits it.
            if (IsPrivilegeToDelete(attribute.Name))
                continue;

            fedOnly.Add($"Attribute '{attribute.QualifiedName}'");
        }

        foreach (var source in omtClass.Attributes)
        {
            if (matched.Contains(source) || IsPrivilegeToDelete(source.Name))
                continue;

            omtOnly.Add($"Attribute '{source.QualifiedName}'");
        }
    }

    // -------------------------------------------------------------- interaction classes

    /// <summary>Walks the merged interaction tree; the mirror of <see cref="MergeObjectClasses"/>.</summary>
    private static void MergeInteractionClasses(
        FomDocument merged, FomDocument omt, FomMergeResult result, List<string> fedOnly, List<string> omtOnly)
    {
        var index = new NameIndex<FomInteractionClass>(omt.AllInteractionClasses());
        var matched = new HashSet<FomInteractionClass>();
        var accounted = new HashSet<FomInteractionClass>();

        foreach (var root in merged.InteractionClasses)
        {
            foreach (var fedClass in root.DescendantsAndSelf())
            {
                var match = index.Find(fedClass);

                if (match is null)
                {
                    if (fedClass.Parent is null)
                    {
                        // InteractionRoot, the FED's own wrapper; the OMT has no counterpart for it.
                        accounted.Add(fedClass);
                        continue;
                    }

                    if (accounted.Contains(fedClass.Parent))
                        fedOnly.Add($"Interaction class '{fedClass.QualifiedName}'");

                    continue;
                }

                accounted.Add(fedClass);
                matched.Add(match);

                if (EnrichInteractionClass(fedClass, match))
                    result.EnrichedInteractionCount++;

                MergeParameters(fedClass, match, result, fedOnly, omtOnly);
            }
        }

        foreach (var omtClass in omt.AllInteractionClasses())
        {
            if (matched.Contains(omtClass))
                continue;

            if (omtClass.Parent is not null && !matched.Contains(omtClass.Parent))
                continue;

            omtOnly.Add($"Interaction class '{omtClass.QualifiedName}'");
        }
    }

    /// <summary>Matches parameters by name inside one already-matched interaction pair.</summary>
    private static void MergeParameters(
        FomInteractionClass fedClass, FomInteractionClass omtClass, FomMergeResult result, List<string> fedOnly, List<string> omtOnly)
    {
        var byName = BuildNameMap(omtClass.Parameters);
        var matched = new HashSet<FomParameter>();

        foreach (var parameter in fedClass.Parameters)
        {
            if (byName.TryGetValue(parameter.Name, out var source))
            {
                matched.Add(source);

                if (EnrichParameter(parameter, source))
                    result.EnrichedParameterCount++;

                continue;
            }

            fedOnly.Add($"Parameter '{parameter.QualifiedName}'");
        }

        foreach (var source in omtClass.Parameters)
        {
            if (matched.Contains(source))
                continue;

            omtOnly.Add($"Parameter '{source.QualifiedName}'");
        }
    }

    // ------------------------------------------------------------------------ matching

    /// <summary>
    /// Looks OMT elements up by the several spellings one FED element may correspond to.
    /// </summary>
    /// <remarks>
    /// The FED tree carries an <c>ObjectRoot</c> / <c>InteractionRoot</c> wrapper that the OMT does
    /// not have, so the qualified name minus its leading segment is tried first. The unqualified leaf
    /// name is the last resort and is only ever used when it is unique in the whole OMT — matching the
    /// wrong class would silently give an attribute the wrong datatype, which is worse than leaving
    /// it untyped.
    /// </remarks>
    private sealed class NameIndex<T> where T : FomNode
    {
        private readonly Dictionary<string, T> _byQualifiedName = new(StringComparer.Ordinal);

        /// <summary>Leaf name to node; a null value marks a name that more than one node carries.</summary>
        private readonly Dictionary<string, T?> _byLeafName = new(StringComparer.Ordinal);

        public NameIndex(IEnumerable<T> nodes)
        {
            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.QualifiedName))
                    _byQualifiedName.TryAdd(node.QualifiedName, node);

                if (string.IsNullOrEmpty(node.Name))
                    continue;

                if (_byLeafName.ContainsKey(node.Name))
                    _byLeafName[node.Name] = null;
                else
                    _byLeafName[node.Name] = node;
            }
        }

        /// <summary>The OMT element corresponding to <paramref name="fedNode"/>, or null.</summary>
        public T? Find(FomNode fedNode)
        {
            var withoutRoot = StripRootSegment(fedNode.QualifiedName);
            if (withoutRoot is not null && _byQualifiedName.TryGetValue(withoutRoot, out var stripped))
                return stripped;

            // Some tools do write the root wrapper into the OMT as well.
            if (!string.IsNullOrEmpty(fedNode.QualifiedName) &&
                _byQualifiedName.TryGetValue(fedNode.QualifiedName, out var exact))
            {
                return exact;
            }

            if (!string.IsNullOrEmpty(fedNode.Name) && _byLeafName.TryGetValue(fedNode.Name, out var leaf))
                return leaf;   // null when the name is ambiguous, which counts as "no match".

            return null;
        }
    }

    /// <summary>
    /// The qualified name with its first segment removed, or null when there is no segment to remove
    /// (the name is the FED root itself).
    /// </summary>
    private static string? StripRootSegment(string? qualifiedName)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            return null;

        var dot = qualifiedName.IndexOf('.');
        return dot < 0 || dot == qualifiedName.Length - 1 ? null : qualifiedName[(dot + 1)..];
    }

    /// <summary>Indexes the members of one owner by name; the first of a repeated name wins.</summary>
    private static Dictionary<string, T> BuildNameMap<T>(IEnumerable<T> nodes) where T : FomNode
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.Name))
                map.TryAdd(node.Name, node);
        }

        return map;
    }

    /// <summary>True for either dialect's spelling of the RTI-supplied delete privilege.</summary>
    private static bool IsPrivilegeToDelete(string name) =>
        string.Equals(name, "privilegeToDelete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, OmtNormalizer.PrivilegeToDeleteName, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------- enrichment

    /// <summary>
    /// Takes <paramref name="incoming"/> only when the FED left the property empty, so a value the
    /// RTI actually loads is never replaced by the documentation's opinion of it.
    /// </summary>
    /// <param name="changed">Set to true when a value was taken; left alone otherwise.</param>
    private static string? Fill(string? current, string? incoming, ref bool changed)
    {
        if (!string.IsNullOrEmpty(current) || string.IsNullOrWhiteSpace(incoming))
            return current;

        changed = true;
        return incoming;
    }

    /// <summary>Overlays the OMT's sharing and prose onto a FED object class.</summary>
    private static bool EnrichObjectClass(FomObjectClass target, FomObjectClass source)
    {
        var changed = false;
        target.Sharing = Fill(target.Sharing, source.Sharing, ref changed);
        target.Semantics = Fill(target.Semantics, source.Semantics, ref changed);
        target.Notes = Fill(target.Notes, source.Notes, ref changed);
        return changed;
    }

    /// <summary>
    /// Overlays the OMT attribute table onto a FED attribute. Transportation and order are listed
    /// here too, but the FED states them for every well-formed attribute, so they are only ever taken
    /// when the FED line was short of tokens.
    /// </summary>
    private static bool EnrichAttribute(FomAttribute target, FomAttribute source)
    {
        var changed = false;
        target.DataType = Fill(target.DataType, source.DataType, ref changed);
        target.Cardinality = Fill(target.Cardinality, source.Cardinality, ref changed);
        target.Units = Fill(target.Units, source.Units, ref changed);
        target.Resolution = Fill(target.Resolution, source.Resolution, ref changed);
        target.Accuracy = Fill(target.Accuracy, source.Accuracy, ref changed);
        target.AccuracyCondition = Fill(target.AccuracyCondition, source.AccuracyCondition, ref changed);
        target.UpdateType = Fill(target.UpdateType, source.UpdateType, ref changed);
        target.UpdateCondition = Fill(target.UpdateCondition, source.UpdateCondition, ref changed);
        target.Ownership = Fill(target.Ownership, source.Ownership, ref changed);
        target.Sharing = Fill(target.Sharing, source.Sharing, ref changed);
        target.Semantics = Fill(target.Semantics, source.Semantics, ref changed);
        target.Notes = Fill(target.Notes, source.Notes, ref changed);
        target.Transportation = Fill(target.Transportation, source.Transportation, ref changed);
        target.Order = Fill(target.Order, source.Order, ref changed);
        return changed;
    }

    /// <summary>
    /// Overlays the OMT's sharing and prose onto a FED interaction class. Transportation and order
    /// are taken only when the FED left them undefined, because the FED is what the RTI obeys.
    /// </summary>
    private static bool EnrichInteractionClass(FomInteractionClass target, FomInteractionClass source)
    {
        var changed = false;
        target.Sharing = Fill(target.Sharing, source.Sharing, ref changed);
        target.Semantics = Fill(target.Semantics, source.Semantics, ref changed);
        target.Notes = Fill(target.Notes, source.Notes, ref changed);
        target.Transportation = Fill(target.Transportation, source.Transportation, ref changed);
        target.Order = Fill(target.Order, source.Order, ref changed);
        return changed;
    }

    /// <summary>Overlays the OMT parameter table onto a FED parameter, which carries only a name.</summary>
    private static bool EnrichParameter(FomParameter target, FomParameter source)
    {
        var changed = false;
        target.DataType = Fill(target.DataType, source.DataType, ref changed);
        target.Cardinality = Fill(target.Cardinality, source.Cardinality, ref changed);
        target.Units = Fill(target.Units, source.Units, ref changed);
        target.Resolution = Fill(target.Resolution, source.Resolution, ref changed);
        target.Accuracy = Fill(target.Accuracy, source.Accuracy, ref changed);
        target.AccuracyCondition = Fill(target.AccuracyCondition, source.AccuracyCondition, ref changed);
        target.Semantics = Fill(target.Semantics, source.Semantics, ref changed);
        target.Notes = Fill(target.Notes, source.Notes, ref changed);
        return changed;
    }

    // ------------------------------------------------------- tables only the OMT can hold

    /// <summary>
    /// Copies the tables a FED cannot express at all. These are not a merge — the FED has nothing to
    /// disagree with — so they are taken wholesale, deep-copied so the OMT keeps its own graph.
    /// </summary>
    private static void CopyTablesOnlyTheOmtHas(FomDocument merged, FomDocument omt)
    {
        var target = merged.DataTypes;
        var source = omt.DataTypes;

        foreach (var item in source.BasicDataRepresentations) target.BasicDataRepresentations.Add(CloneBasic(item));
        foreach (var item in source.SimpleDataTypes) target.SimpleDataTypes.Add(CloneSimple(item));
        foreach (var item in source.EnumeratedDataTypes) target.EnumeratedDataTypes.Add(CloneEnumerated(item));
        foreach (var item in source.ArrayDataTypes) target.ArrayDataTypes.Add(CloneArray(item));
        foreach (var item in source.FixedRecordDataTypes) target.FixedRecordDataTypes.Add(CloneFixedRecord(item));
        foreach (var item in source.VariantRecordDataTypes) target.VariantRecordDataTypes.Add(CloneVariantRecord(item));

        foreach (var note in omt.Notes)
        {
            // A FED has no notes, but guard anyway rather than duplicating one.
            if (merged.Notes.Any(existing =>
                    string.Equals(existing.Label, note.Label, StringComparison.Ordinal) &&
                    string.Equals(existing.Text, note.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Notes.Add(CloneNote(note));
        }

        if (merged.Time.IsEmpty && !omt.Time.IsEmpty)
            merged.Time = CloneTime(omt.Time);
    }

    /// <summary>
    /// Folds the OMT's identification block onto the FED's. The FED knows only the federation name
    /// and the FED version, so the OMT's richer value is preferred wherever it has one.
    /// </summary>
    private static void MergeIdentification(ModelIdentification target, ModelIdentification source)
    {
        target.Name = Prefer(source.Name, target.Name);
        target.Type = Prefer(source.Type, target.Type);
        target.Version = Prefer(source.Version, target.Version);
        target.ModificationDate = Prefer(source.ModificationDate, target.ModificationDate);
        target.SecurityClassification = Prefer(source.SecurityClassification, target.SecurityClassification);
        target.ReleaseRestriction = Prefer(source.ReleaseRestriction, target.ReleaseRestriction);
        target.Purpose = Prefer(source.Purpose, target.Purpose);
        target.ApplicationDomain = Prefer(source.ApplicationDomain, target.ApplicationDomain);
        target.Description = Prefer(source.Description, target.Description);
        target.UseLimitation = Prefer(source.UseLimitation, target.UseLimitation);
        target.Reference = Prefer(source.Reference, target.Reference);
        target.Other = Prefer(source.Other, target.Other);
        target.Glyph = Prefer(source.Glyph, target.Glyph);

        AddMissing(target.Keywords, source.Keywords);
        AddMissing(target.PointsOfContact, source.PointsOfContact);
        AddMissing(target.UseHistory, source.UseHistory);
    }

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static void AddMissing(List<string> target, List<string> source)
    {
        foreach (var value in source)
        {
            if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.Ordinal))
                target.Add(value);
        }
    }

    // ----------------------------------------------------------------------- reporting

    /// <summary>Trims a mismatch list to <see cref="MaxReportedMismatches"/> entries, the last naming the overflow.</summary>
    private static void Cap(List<string> target, List<string> entries)
    {
        if (entries.Count <= MaxReportedMismatches)
        {
            target.AddRange(entries);
            return;
        }

        target.AddRange(entries.Take(MaxReportedMismatches - 1));
        target.Add($"... and {entries.Count - (MaxReportedMismatches - 1)} more");
    }

    /// <summary>
    /// Records what the merge did and, when the two files disagree, why the pair should be looked at.
    /// The counts quoted are the true totals, not the capped samples.
    /// </summary>
    private static void Report(FomMergeResult result, FomDocument fed, FomDocument omt, int fedOnly, int omtOnly)
    {
        var merged = result.Document;

        merged.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Info,
            $"Merged the HLA 1.3 pair '{Describe(fed)}' (structure) with '{Describe(omt)}' (meaning): " +
            $"{result.EnrichedClassCount} object class(es), {result.EnrichedAttributeCount} attribute(s), " +
            $"{result.EnrichedInteractionCount} interaction class(es) and {result.EnrichedParameterCount} parameter(s) " +
            $"took values from the OMT; {merged.DataTypeCount} datatype(s) and {merged.Notes.Count} note(s) were copied " +
            "across. The diagnostics of both source files follow."));

        if (fedOnly > 0)
        {
            merged.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning,
                $"{fedOnly} element(s) declared by the FED are not described by '{Describe(omt)}'; " +
                "they keep their structure but stay untyped and undocumented."));
        }

        if (omtOnly > 0)
        {
            merged.Diagnostics.Add(new ParseDiagnostic(DiagnosticSeverity.Warning,
                $"{omtOnly} element(s) described by '{Describe(omt)}' do not exist in '{Describe(fed)}'. " +
                "The two files are meant to describe the same federation, so they have drifted apart and " +
                "the pair should not be trusted without review."));
        }
    }

    /// <summary>Names a document for a diagnostic: its file name, else its model name.</summary>
    private static string Describe(FomDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.SourcePath))
        {
            var fileName = Path.GetFileName(document.SourcePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                return fileName;
        }

        return string.IsNullOrWhiteSpace(document.Identification.Name)
            ? "(unnamed document)"
            : document.Identification.Name!;
    }

    // -------------------------------------------------------------------------- cloning

    /// <summary>
    /// Deep-copies the FED into a fresh document. The caller may still want its original — and the
    /// registry keeps parsed documents around — so the merge writes only into this copy.
    /// </summary>
    internal static FomDocument CloneSpine(FomDocument fed)
    {
        var merged = new FomDocument
        {
            // The result is still an HLA 1.3 model, read from the file the RTI loads.
            Standard = FomStandard.Hla13,
            SourcePath = fed.SourcePath,
            SourceNamespace = fed.SourceNamespace,
            Identification = CloneIdentification(fed.Identification),
            Time = CloneTime(fed.Time),
        };

        foreach (var root in fed.ObjectClasses)
            merged.ObjectClasses.Add(CloneObjectClass(root, parent: null));

        foreach (var root in fed.InteractionClasses)
            merged.InteractionClasses.Add(CloneInteractionClass(root, parent: null));

        foreach (var space in fed.RoutingSpaces)
        {
            var clone = new FomRoutingSpace
            {
                Name = space.Name,
                QualifiedName = space.QualifiedName,
                Semantics = space.Semantics,
                Notes = space.Notes,
            };
            clone.Dimensions.AddRange(space.Dimensions);
            merged.RoutingSpaces.Add(clone);
        }

        // A FED populates none of the tables below, but copying them keeps the merge honest for any
        // caller that hands in a document assembled some other way.
        foreach (var item in fed.Dimensions)
        {
            merged.Dimensions.Add(new FomDimension
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                DataType = item.DataType,
                UpperBound = item.UpperBound,
                Normalization = item.Normalization,
                Value = item.Value,
            });
        }

        foreach (var item in fed.Transportations)
        {
            merged.Transportations.Add(new FomTransportation
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                Reliable = item.Reliable,
            });
        }

        foreach (var item in fed.Synchronizations)
        {
            merged.Synchronizations.Add(new FomSynchronization
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                Capability = item.Capability,
                DataType = item.DataType,
            });
        }

        foreach (var item in fed.UpdateRates)
        {
            merged.UpdateRates.Add(new FomUpdateRate
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                Rate = item.Rate,
            });
        }

        foreach (var item in fed.Switches)
        {
            merged.Switches.Add(new FomSwitch
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                IsEnabled = item.IsEnabled,
                ResignSwitch = item.ResignSwitch,
            });
        }

        foreach (var item in fed.Tags)
        {
            merged.Tags.Add(new FomTag
            {
                Name = item.Name,
                QualifiedName = item.QualifiedName,
                Semantics = item.Semantics,
                Notes = item.Notes,
                DataType = item.DataType,
            });
        }

        foreach (var note in fed.Notes)
            merged.Notes.Add(CloneNote(note));

        return merged;
    }

    internal static FomObjectClass CloneObjectClass(FomObjectClass source, FomObjectClass? parent)
    {
        var clone = new FomObjectClass
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            Sharing = source.Sharing,
            Parent = parent,
        };

        foreach (var attribute in source.Attributes)
            clone.Attributes.Add(CloneAttribute(attribute));

        foreach (var child in source.Children)
            clone.Children.Add(CloneObjectClass(child, clone));

        return clone;
    }

    internal static FomAttribute CloneAttribute(FomAttribute source)
    {
        var clone = new FomAttribute
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            DataType = source.DataType,
            UpdateType = source.UpdateType,
            UpdateCondition = source.UpdateCondition,
            Ownership = source.Ownership,
            Sharing = source.Sharing,
            Transportation = source.Transportation,
            Order = source.Order,
            RoutingSpace = source.RoutingSpace,
            Cardinality = source.Cardinality,
            Units = source.Units,
            Resolution = source.Resolution,
            Accuracy = source.Accuracy,
            AccuracyCondition = source.AccuracyCondition,
        };

        clone.Dimensions.AddRange(source.Dimensions);
        return clone;
    }

    internal static FomInteractionClass CloneInteractionClass(FomInteractionClass source, FomInteractionClass? parent)
    {
        var clone = new FomInteractionClass
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            Sharing = source.Sharing,
            Transportation = source.Transportation,
            Order = source.Order,
            RoutingSpace = source.RoutingSpace,
            Parent = parent,
        };

        clone.Dimensions.AddRange(source.Dimensions);

        foreach (var parameter in source.Parameters)
            clone.Parameters.Add(CloneParameter(parameter));

        foreach (var child in source.Children)
            clone.Children.Add(CloneInteractionClass(child, clone));

        return clone;
    }

    internal static FomParameter CloneParameter(FomParameter source) => new()
    {
        Name = source.Name,
        QualifiedName = source.QualifiedName,
        Semantics = source.Semantics,
        Notes = source.Notes,
        DataType = source.DataType,
        Cardinality = source.Cardinality,
        Units = source.Units,
        Resolution = source.Resolution,
        Accuracy = source.Accuracy,
        AccuracyCondition = source.AccuracyCondition,
    };

    internal static ModelIdentification CloneIdentification(ModelIdentification source)
    {
        var clone = new ModelIdentification
        {
            Name = source.Name,
            Type = source.Type,
            Version = source.Version,
            ModificationDate = source.ModificationDate,
            SecurityClassification = source.SecurityClassification,
            ReleaseRestriction = source.ReleaseRestriction,
            Purpose = source.Purpose,
            ApplicationDomain = source.ApplicationDomain,
            Description = source.Description,
            UseLimitation = source.UseLimitation,
            Reference = source.Reference,
            Other = source.Other,
            Glyph = source.Glyph,
        };

        clone.Keywords.AddRange(source.Keywords);
        clone.PointsOfContact.AddRange(source.PointsOfContact);
        clone.UseHistory.AddRange(source.UseHistory);
        return clone;
    }

    internal static FomTime CloneTime(FomTime source) => new()
    {
        TimeStampDataType = source.TimeStampDataType,
        TimeStampSemantics = source.TimeStampSemantics,
        LookaheadDataType = source.LookaheadDataType,
        LookaheadSemantics = source.LookaheadSemantics,
    };

    internal static FomNote CloneNote(FomNote source) => new()
    {
        Name = source.Name,
        QualifiedName = source.QualifiedName,
        Semantics = source.Semantics,
        Notes = source.Notes,
        Label = source.Label,
        Text = source.Text,
    };

    internal static BasicDataType CloneBasic(BasicDataType source) => new()
    {
        Name = source.Name,
        QualifiedName = source.QualifiedName,
        Semantics = source.Semantics,
        Notes = source.Notes,
        Size = source.Size,
        Interpretation = source.Interpretation,
        Endian = source.Endian,
        Encoding = source.Encoding,
    };

    internal static SimpleDataType CloneSimple(SimpleDataType source) => new()
    {
        Name = source.Name,
        QualifiedName = source.QualifiedName,
        Semantics = source.Semantics,
        Notes = source.Notes,
        Representation = source.Representation,
        Units = source.Units,
        Resolution = source.Resolution,
        Accuracy = source.Accuracy,
    };

    internal static EnumeratedDataType CloneEnumerated(EnumeratedDataType source)
    {
        var clone = new EnumeratedDataType
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            Representation = source.Representation,
        };

        foreach (var enumerator in source.Enumerators)
        {
            clone.Enumerators.Add(new EnumeratorValue
            {
                Name = enumerator.Name,
                QualifiedName = enumerator.QualifiedName,
                Semantics = enumerator.Semantics,
                Notes = enumerator.Notes,
                Values = enumerator.Values,
            });
        }

        return clone;
    }

    internal static ArrayDataType CloneArray(ArrayDataType source) => new()
    {
        Name = source.Name,
        QualifiedName = source.QualifiedName,
        Semantics = source.Semantics,
        Notes = source.Notes,
        DataType = source.DataType,
        Cardinality = source.Cardinality,
        Encoding = source.Encoding,
    };

    internal static FixedRecordDataType CloneFixedRecord(FixedRecordDataType source)
    {
        var clone = new FixedRecordDataType
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            Encoding = source.Encoding,
            Include = source.Include,
        };

        foreach (var field in source.Fields)
        {
            clone.Fields.Add(new RecordField
            {
                Name = field.Name,
                QualifiedName = field.QualifiedName,
                Semantics = field.Semantics,
                Notes = field.Notes,
                DataType = field.DataType,
            });
        }

        return clone;
    }

    internal static VariantRecordDataType CloneVariantRecord(VariantRecordDataType source)
    {
        var clone = new VariantRecordDataType
        {
            Name = source.Name,
            QualifiedName = source.QualifiedName,
            Semantics = source.Semantics,
            Notes = source.Notes,
            Discriminant = source.Discriminant,
            DataType = source.DataType,
            Encoding = source.Encoding,
        };

        foreach (var alternative in source.Alternatives)
        {
            clone.Alternatives.Add(new VariantAlternative
            {
                Name = alternative.Name,
                QualifiedName = alternative.QualifiedName,
                Semantics = alternative.Semantics,
                Notes = alternative.Notes,
                Enumerator = alternative.Enumerator,
                DataType = alternative.DataType,
            });
        }

        return clone;
    }

    internal static ParseDiagnostic Clone(ParseDiagnostic source) =>
        new(source.Severity, source.Message, source.Line, source.Path);
}
