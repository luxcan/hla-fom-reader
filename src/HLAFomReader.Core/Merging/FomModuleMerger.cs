using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Merging;

/// <summary>What a module merge produced, and what it could not reconcile.</summary>
public sealed class FomModuleMergeResult
{
    /// <summary>The union of every input, in dependency order. Never null.</summary>
    public required FomDocument Document { get; init; }

    /// <summary>
    /// Elements a later module redefined incompatibly, e.g. the same attribute with two datatypes.
    /// Empty is the normal case; anything here is a genuine problem with the module set.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

    /// <summary>Object classes the union gained from a module beyond the first.</summary>
    public int AddedClasses { get; init; }

    /// <summary>Attributes added to classes that already existed.</summary>
    public int ExtendedAttributes { get; init; }

    /// <summary>Datatype definitions the union gained beyond the first module.</summary>
    public int AddedDataTypes { get; init; }
}

/// <summary>
/// Combines a set of IEEE 1516-2010 FOM modules into the one document a federation would actually
/// run — the effective FDD the RTI builds at <c>createFederationExecution</c>.
/// </summary>
/// <remarks>
/// <para>
/// A FOM module carries only what it adds. NETN-Physical declares <c>Aircraft</c> as a bare name
/// with no attributes, because its real definition lives in RPR-Physical; the empty class exists
/// only to hang <c>NETN_Aircraft</c> off the right branch. Read on its own such a module is not a
/// small FOM, it is a misleading one: sixteen of its twenty-seven classes look empty, and comparing
/// it against a complete FOM reports the missing modules' attributes as deletions somebody made.
/// </para>
/// <para>
/// This is a different operation from <see cref="FomMerger"/> and shares nothing but the deep-copy
/// helpers. That one reconciles two views of a single model — an HLA 1.3 FED's structure with its
/// OMT's meaning — where both sides describe the same elements and the question is which file to
/// believe. This one is strictly additive over N documents that describe different elements, and
/// the interesting case is not disagreement but extension.
/// </para>
/// <para>
/// Modules are supplied in dependency order, bases first. Where two modules do describe the same
/// element the earlier one wins every property it filled in, and a later module contradicting it —
/// the same attribute under a different datatype — is reported as a conflict rather than silently
/// overwritten. Nothing is guessed: an unstated property stays unstated.
/// </para>
/// <para>
/// Nothing the caller passed in is mutated. The union is built from deep copies, so every input
/// remains exactly as it was parsed.
/// </para>
/// </remarks>
public static class FomModuleMerger
{
    /// <summary>Most conflicts reported before the list is truncated; a mismatched pair can flood.</summary>
    private const int MaxReportedConflicts = 50;

    /// <summary>
    /// Merges modules into one document.
    /// </summary>
    /// <param name="modules">
    /// The modules, in dependency order — bases first, the module being registered last. At least
    /// one is required.
    /// </param>
    /// <returns>The union, the counts it gained, and anything that could not be reconciled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modules"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="modules"/> is empty or contains a null.</exception>
    /// <summary>
    /// Gives a merged document the identity of the compiled FOM it now is, rather than the identity
    /// of the last module it was built from.
    /// </summary>
    /// <param name="compiled">The document <see cref="Merge"/> produced. Modified in place.</param>
    /// <param name="name">What to call the result. Blank leaves the inherited name alone.</param>
    /// <param name="moduleNames">
    /// The modules that went into it, in compile order — file names rather than full paths, since
    /// the record is meant to survive the file being moved.
    /// </param>
    /// <remarks>
    /// <para>
    /// The dependency references are the reason this exists. A module declares what it must be
    /// loaded alongside — <c>&lt;reference&gt;&lt;type&gt;Dependency&lt;/type&gt;</c> — and
    /// <see cref="Merge"/> inherits the last module's identification wholesale, so without this the
    /// compiled file would go on asking for the very modules it now contains. Anything reading it,
    /// this application included, would be told to go and find them.
    /// </para>
    /// <para>
    /// Only dependency references are dropped. A reference to the standard the model implements, or
    /// to the document that specifies it, is still true of the compiled result and is somebody's
    /// citation rather than an instruction to a loader.
    /// </para>
    /// <para>
    /// What went into the compile is added to the use history instead of overwriting the
    /// description, so the provenance travels inside the file without displacing anything the
    /// modules said about themselves.
    /// </para>
    /// </remarks>
    public static void StampAsCompiled(
        FomDocument compiled, string? name, IReadOnlyList<string>? moduleNames = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        var identification = compiled.Identification;

        if (!string.IsNullOrWhiteSpace(name))
            identification.Name = name!.Trim();

        identification.Reference = WithoutDependencies(identification.Reference);

        if (moduleNames is { Count: > 1 })
        {
            identification.UseHistory.Add(
                "Compiled from " + moduleNames.Count + " modules, in order: "
                + string.Join(", ", moduleNames) + ".");
        }
    }

    /// <summary>
    /// Drops the <c>Dependency</c> entries from a flattened reference list, keeping the rest.
    /// </summary>
    /// <remarks>
    /// The parser renders each reference as "type: identification" and joins them with "; ", so that
    /// is the shape being taken apart here. An entry with no type is kept: it cannot be shown to be
    /// a dependency, and dropping a citation is worse than keeping a stale loader hint.
    /// </remarks>
    private static string? WithoutDependencies(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return reference;

        var kept = reference
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !entry.StartsWith("Dependency:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    public static FomModuleMergeResult Merge(IReadOnlyList<FomDocument> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        if (modules.Count == 0)
            throw new ArgumentException("At least one module is required.", nameof(modules));

        if (modules.Any(m => m is null))
            throw new ArgumentException("Modules cannot contain a null document.", nameof(modules));

        var run = new Run();

        foreach (var module in modules)
            run.Absorb(module);

        // Identity comes from the last module — the one being registered. Everything before it is a
        // dependency that was pulled in to complete the picture, and naming the union after a base
        // would leave the registry listing the same FOM several times under the same name.
        var last = modules[^1];
        run.Document.Identification = FomMerger.CloneIdentification(last.Identification);
        run.Document.Standard = last.Standard;
        run.Document.SourcePath = last.SourcePath;
        run.Document.SourceNamespace = last.SourceNamespace;

        return new FomModuleMergeResult
        {
            Document = run.Document,
            Conflicts = run.Conflicts,
            AddedClasses = run.AddedClasses,
            ExtendedAttributes = run.ExtendedAttributes,
            AddedDataTypes = run.AddedDataTypes,
        };
    }

    /// <summary>One merge in progress. Holds the union and the indexes that keep it O(n).</summary>
    private sealed class Run
    {
        // Qualified name is the identity of a class across modules: NETN-Physical's Aircraft and
        // RPR-Physical's Aircraft are the same class precisely because the path matches, which is
        // the whole mechanism by which a module attaches to its base.
        private readonly Dictionary<string, FomObjectClass> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FomInteractionClass> _interactions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dataTypes = new(StringComparer.Ordinal);
        private readonly List<string> _conflicts = new();

        private bool _first = true;

        public FomDocument Document { get; } = new();
        public IReadOnlyList<string> Conflicts => _conflicts;
        public int AddedClasses { get; private set; }
        public int ExtendedAttributes { get; private set; }
        public int AddedDataTypes { get; private set; }

        public void Absorb(FomDocument module)
        {
            foreach (var root in module.ObjectClasses)
                AbsorbObjectClass(root, parent: null);

            foreach (var root in module.InteractionClasses)
                AbsorbInteractionClass(root, parent: null);

            AbsorbDataTypes(module);
            AbsorbTables(module);

            foreach (var diagnostic in module.Diagnostics)
                Document.Diagnostics.Add(FomMerger.Clone(diagnostic));

            _first = false;
        }

        // ---- object classes -------------------------------------------------------------------

        private void AbsorbObjectClass(FomObjectClass source, FomObjectClass? parent)
        {
            if (!_objects.TryGetValue(source.QualifiedName, out var target))
            {
                // New to the union. Cloned without its children, which are absorbed individually
                // below so that each one is indexed and can be extended by a later module in turn.
                target = new FomObjectClass
                {
                    Name = source.Name,
                    QualifiedName = source.QualifiedName,
                    Semantics = source.Semantics,
                    Notes = source.Notes,
                    Sharing = source.Sharing,
                    Parent = parent,
                };

                foreach (var attribute in source.Attributes)
                    target.Attributes.Add(FomMerger.CloneAttribute(attribute));

                if (parent is null) Document.ObjectClasses.Add(target);
                else parent.Children.Add(target);

                _objects[source.QualifiedName] = target;
                if (!_first) AddedClasses++;
            }
            else
            {
                // Already present. This is the scaffolding case in reverse and the common one: a
                // base module defined the class, and the module now being absorbed either restates
                // it as a bare name or genuinely adds to it.
                FillClass(target, source);
                MergeAttributes(target, source);
            }

            foreach (var child in source.Children)
                AbsorbObjectClass(child, target);
        }

        private void FillClass(FomObjectClass target, FomObjectClass source)
        {
            target.Sharing ??= source.Sharing;
            target.Semantics ??= source.Semantics;
            target.Notes ??= source.Notes;
        }

        private void MergeAttributes(FomObjectClass target, FomObjectClass source)
        {
            var byName = target.Attributes.ToDictionary(a => a.Name, StringComparer.Ordinal);

            foreach (var attribute in source.Attributes)
            {
                if (byName.TryGetValue(attribute.Name, out var existing))
                {
                    // Redeclaring an attribute is legal — a module may restate one it inherits — but
                    // giving it a different datatype is not: the two modules disagree about what is
                    // on the wire, and merging them would produce a document neither author wrote.
                    if (Differs(existing.DataType, attribute.DataType))
                    {
                        Report($"{target.QualifiedName}.{attribute.Name}: datatype "
                             + $"'{existing.DataType}' then '{attribute.DataType}'");
                    }

                    FillAttribute(existing, attribute);
                    continue;
                }

                target.Attributes.Add(FomMerger.CloneAttribute(attribute));
                if (!_first) ExtendedAttributes++;
            }
        }

        private static void FillAttribute(FomAttribute target, FomAttribute source)
        {
            target.DataType ??= source.DataType;
            target.UpdateType ??= source.UpdateType;
            target.UpdateCondition ??= source.UpdateCondition;
            target.Ownership ??= source.Ownership;
            target.Sharing ??= source.Sharing;
            target.Transportation ??= source.Transportation;
            target.Order ??= source.Order;
            target.RoutingSpace ??= source.RoutingSpace;
            target.Cardinality ??= source.Cardinality;
            target.Units ??= source.Units;
            target.Resolution ??= source.Resolution;
            target.Accuracy ??= source.Accuracy;
            target.AccuracyCondition ??= source.AccuracyCondition;
            target.Semantics ??= source.Semantics;
            target.Notes ??= source.Notes;

            foreach (var dimension in source.Dimensions)
            {
                if (!target.Dimensions.Contains(dimension, StringComparer.Ordinal))
                    target.Dimensions.Add(dimension);
            }
        }

        // ---- interaction classes --------------------------------------------------------------

        private void AbsorbInteractionClass(FomInteractionClass source, FomInteractionClass? parent)
        {
            if (!_interactions.TryGetValue(source.QualifiedName, out var target))
            {
                target = new FomInteractionClass
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

                foreach (var dimension in source.Dimensions)
                    target.Dimensions.Add(dimension);

                foreach (var parameter in source.Parameters)
                    target.Parameters.Add(FomMerger.CloneParameter(parameter));

                if (parent is null) Document.InteractionClasses.Add(target);
                else parent.Children.Add(target);

                _interactions[source.QualifiedName] = target;
                if (!_first) AddedClasses++;
            }
            else
            {
                target.Sharing ??= source.Sharing;
                target.Transportation ??= source.Transportation;
                target.Order ??= source.Order;
                target.RoutingSpace ??= source.RoutingSpace;
                target.Semantics ??= source.Semantics;
                target.Notes ??= source.Notes;

                MergeParameters(target, source);
            }

            foreach (var child in source.Children)
                AbsorbInteractionClass(child, target);
        }

        private void MergeParameters(FomInteractionClass target, FomInteractionClass source)
        {
            var byName = target.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

            foreach (var parameter in source.Parameters)
            {
                if (byName.TryGetValue(parameter.Name, out var existing))
                {
                    if (Differs(existing.DataType, parameter.DataType))
                    {
                        Report($"{target.QualifiedName}.{parameter.Name}: datatype "
                             + $"'{existing.DataType}' then '{parameter.DataType}'");
                    }

                    existing.DataType ??= parameter.DataType;
                    existing.Semantics ??= parameter.Semantics;
                    existing.Notes ??= parameter.Notes;
                    existing.Cardinality ??= parameter.Cardinality;
                    existing.Units ??= parameter.Units;
                    existing.Resolution ??= parameter.Resolution;
                    existing.Accuracy ??= parameter.Accuracy;
                    existing.AccuracyCondition ??= parameter.AccuracyCondition;
                    continue;
                }

                target.Parameters.Add(FomMerger.CloneParameter(parameter));
                if (!_first) ExtendedAttributes++;
            }
        }

        // ---- datatypes and the remaining tables -------------------------------------------------

        private void AbsorbDataTypes(FomDocument module)
        {
            var t = module.DataTypes;
            var into = Document.DataTypes;

            Take(t.BasicDataRepresentations, into.BasicDataRepresentations, FomMerger.CloneBasic);
            Take(t.SimpleDataTypes, into.SimpleDataTypes, FomMerger.CloneSimple);
            Take(t.EnumeratedDataTypes, into.EnumeratedDataTypes, FomMerger.CloneEnumerated);
            Take(t.ArrayDataTypes, into.ArrayDataTypes, FomMerger.CloneArray);
            Take(t.FixedRecordDataTypes, into.FixedRecordDataTypes, FomMerger.CloneFixedRecord);
            Take(t.VariantRecordDataTypes, into.VariantRecordDataTypes, FomMerger.CloneVariantRecord);
        }

        /// <summary>
        /// Copies the definitions this module introduces, keyed on name across every datatype table
        /// at once — the OMT namespace for types is flat, so a name taken by a record is taken for
        /// an enumeration too.
        /// </summary>
        private void Take<T>(List<T> source, List<T> target, Func<T, T> clone) where T : FomNode
        {
            foreach (var item in source)
            {
                if (!_dataTypes.Add(item.Name))
                {
                    // Already defined by an earlier module. Two modules defining the same type is
                    // normal — a module often restates what it borrows — and only worth reporting
                    // when the definitions are not the same type of thing.
                    if (!target.Any(existing => string.Equals(existing.Name, item.Name, StringComparison.Ordinal)))
                        Report($"datatype '{item.Name}' is defined in two different tables");

                    continue;
                }

                target.Add(clone(item));
                if (!_first) AddedDataTypes++;
            }
        }

        private void AbsorbTables(FomDocument module)
        {
            Union(module.Dimensions, Document.Dimensions);
            Union(module.Transportations, Document.Transportations);
            Union(module.Synchronizations, Document.Synchronizations);
            Union(module.UpdateRates, Document.UpdateRates);
            Union(module.Switches, Document.Switches);
            Union(module.Tags, Document.Tags);
            Union(module.RoutingSpaces, Document.RoutingSpaces);

            foreach (var note in module.Notes)
            {
                if (!Document.Notes.Any(n => string.Equals(n.Name, note.Name, StringComparison.Ordinal)))
                    Document.Notes.Add(FomMerger.CloneNote(note));
            }

            // The time representation is a property of the federation, not of a module, so the first
            // module to state one settles it.
            if (Document.Time.IsEmpty)
                Document.Time = FomMerger.CloneTime(module.Time);
        }

        /// <summary>
        /// Adds by name whatever the union does not have yet. These tables are flat lists of named
        /// rows with no structure to reconcile, so first-writer-wins needs no more than this.
        /// </summary>
        private static void Union<T>(List<T> source, List<T> target) where T : FomNode
        {
            var seen = new HashSet<string>(target.Select(x => x.Name), StringComparer.Ordinal);

            foreach (var item in source)
            {
                if (seen.Add(item.Name)) target.Add(item);
            }
        }

        // ---- conflicts ---------------------------------------------------------------------------

        private static bool Differs(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
            && !string.Equals(a, b, StringComparison.Ordinal);

        private void Report(string conflict)
        {
            if (_conflicts.Count < MaxReportedConflicts) _conflicts.Add(conflict);
            else if (_conflicts.Count == MaxReportedConflicts) _conflicts.Add("…further conflicts not listed.");
        }
    }
}
