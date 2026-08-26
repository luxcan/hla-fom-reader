using System;
using System.Collections.Generic;
using System.IO;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// Builds the <see cref="AttributeDataMap"/>: the class-by-attribute worksheet somebody reads when
/// they have to move data from one FOM onto another.
/// </summary>
/// <remarks>
/// <para>
/// Only two things are looked at, because on the wire only two things exist: which attributes a
/// class carries, and what each one is typed as. Sharing, ownership, update type, semantics and
/// qualified names describe the model rather than the data, so they are deliberately absent from
/// every row.
/// </para>
/// <para>
/// "Carries" means the <b>effective</b> set. HLA classes inherit every attribute their ancestors
/// declare, so a class that declares nothing still publishes its ancestors' set — in the RPR FOM,
/// <c>Aircraft</c> declares zero attributes and inherits forty-five. Mapping the declared set would
/// report <c>Aircraft</c> as empty, which is worse than saying nothing at all. The inheritance walk
/// mirrors the detail screen exactly — root-down, with a redeclaration on a subclass overriding the
/// inherited attribute rather than appearing beside it — so the two screens cannot disagree.
/// </para>
/// <para>
/// Content problems never throw: a malformed document produces a thinner map, not an exception.
/// </para>
/// <para>
/// Datatypes are compared on what they <b>encode as</b>, not on what they are called. Each side's
/// name is resolved through its own document's datatype tables by a <see cref="DataTypeResolver"/>,
/// and the canonical forms are what decide the row. A generational migration renames nearly
/// everything — <c>octet</c> to <c>Octet</c>, <c>unsigned long</c> to <c>UnsignedInteger32</c> —
/// and reporting those as changes buries the few that genuinely re-encode, so a rename gets its own
/// status and is kept out of <see cref="AttributeDataMap.ActionableCount"/>.
/// </para>
/// </remarks>
public static class AttributeMapper
{
    /// <summary>Generic name of the A side, used when a row or advisory has to point at a side.</summary>
    private const string LeftSideName = "FOM A";

    /// <summary>Generic name of the B side.</summary>
    private const string RightSideName = "FOM B";

    /// <summary>
    /// Maps every attribute of every object class in <paramref name="left"/> against
    /// <paramref name="right"/>.
    /// </summary>
    /// <param name="left">The A side of the map.</param>
    /// <param name="right">The B side of the map.</param>
    /// <param name="options">Matching knobs; strict defaults are used when null.</param>
    /// <returns>
    /// One row per attribute per class, grouped by class in the A document's own tree order, with
    /// classes that exist only in B appended at the end.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either document is null.</exception>
    public static AttributeDataMap Build(FomDocument left, FomDocument right, ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // Cloned so a later change to the caller's instance cannot invalidate a map already handed out.
        var effective = (options ?? new ComparisonOptions()).Clone();

        return new Run(left, right, effective).Execute();
    }

    /// <summary>The advisory shown for a side that carries no datatype information whatsoever.</summary>
    private static string TypelessMessage(string sideName) =>
        $"{sideName} has no datatypes (HLA 1.3 FED); register its .omt to compare encodings";

    /// <summary>
    /// The advisory that tells the reader how much of the datatype churn is spelling rather than
    /// substance — the single most useful sentence on a generational migration.
    /// </summary>
    private static string RenameMessage(int renamed, int changed) =>
        $"{renamed} attribute{(renamed == 1 ? "" : "s")} changed datatype name only — the encoding " +
        $"is identical, so no conversion is needed. {changed} genuinely re-encode{(changed == 1 ? "s" : "")}.";

    /// <summary>One attribute of a class's effective set, together with the class that declares it.</summary>
    private sealed class EffectiveAttribute
    {
        internal EffectiveAttribute(string key, string name, string? dataType, string declaredIn)
        {
            Key = key;
            Name = name;
            DataType = dataType;
            DeclaredIn = declaredIn;
        }

        /// <summary>Normalised name, used to line the attribute up with the other side.</summary>
        internal string Key { get; }

        /// <summary>The name as the document spells it, which is what the reader sees.</summary>
        internal string Name { get; }

        /// <summary>Datatype as written; null for a document with no datatype table.</summary>
        internal string? DataType { get; }

        /// <summary>Local name of the declaring class — an ancestor whenever the attribute is inherited.</summary>
        internal string DeclaredIn { get; }
    }

    /// <summary>One build. Holds the per-class resolution cache, so it is never shared between calls.</summary>
    private sealed class Run
    {
        private readonly FomDocument _left;
        private readonly FomDocument _right;
        private readonly ComparisonOptions _o;

        /// <summary>
        /// Effective sets already worked out, keyed by class instance. <see cref="FomObjectClass"/>
        /// does not override Equals, so the default comparer is reference identity — which is what is
        /// wanted here, since two different classes may legitimately share a local name, and since
        /// both documents share this one cache.
        /// </summary>
        private readonly Dictionary<FomObjectClass, IReadOnlyList<EffectiveAttribute>> _resolved = new();

        /// <summary>
        /// Datatype resolution for the A side. One per document, built once and memoising every
        /// answer: a real FOM types thousands of attributes with a few hundred datatypes, so
        /// building a resolver per attribute would re-walk the same records over and over. A name
        /// must be resolved against its <em>own</em> document's tables, which is why there are two.
        /// </summary>
        private readonly DataTypeResolver _leftTypes;

        /// <summary>Datatype resolution for the B side; see <see cref="_leftTypes"/>.</summary>
        private readonly DataTypeResolver _rightTypes;

        private bool _leftTypeless;
        private bool _rightTypeless;

        internal Run(FomDocument left, FomDocument right, ComparisonOptions options)
        {
            _left = left;
            _right = right;
            _o = options;

            _leftTypes = new DataTypeResolver(left);
            _rightTypes = new DataTypeResolver(right);
        }

        internal AttributeDataMap Execute()
        {
            var leftClasses = ClassesInTreeOrder(_left);
            var rightClasses = ClassesInTreeOrder(_right);

            // Whether a side carries datatypes at all is a property of the whole map rather than of a
            // row, and it decides how every matched row is judged, so it is settled up front. The
            // resolution work is cached, so this pass costs nothing the row building would not.
            _leftTypeless = IsTypeless(leftClasses);
            _rightTypeless = IsTypeless(rightClasses);

            var note = TypelessNote();

            var rightByKey = new Dictionary<string, FomObjectClass>(_o.NameComparer);
            foreach (var objectClass in rightClasses)
            {
                var key = ClassKey(objectClass);
                if (key.Length == 0) continue;

                // A repeated qualified name means a malformed document. Keeping the first is
                // deterministic, and quietly ignoring the rest beats throwing over bad content.
                rightByKey.TryAdd(key, objectClass);
            }

            var rows = new List<AttributeMapRow>();
            var matchedRight = new HashSet<FomObjectClass>();

            foreach (var leftClass in leftClasses)
            {
                var key = ClassKey(leftClass);

                // The A document's spelling wins for a class both sides have: the reader is working
                // from what they already know.
                var className = RawClassKey(leftClass);

                if (key.Length != 0 && rightByKey.TryGetValue(key, out var rightClass))
                {
                    matchedRight.Add(rightClass);
                    AddMatchedClass(rows, className, leftClass, rightClass, note);
                }
                else
                {
                    AddOneSidedClass(rows, className, leftClass, AttributeMapStatus.OnlyInLeft);
                }
            }

            // Classes only B has come last: they are additions, and a reader works through what they
            // already have before reading what is new.
            foreach (var rightClass in rightClasses)
            {
                if (matchedRight.Contains(rightClass)) continue;
                AddOneSidedClass(rows, RawClassKey(rightClass), rightClass, AttributeMapStatus.OnlyInRight);
            }

            var map = new AttributeDataMap
            {
                Rows = rows,
                LeftLabel = Label(_left, LeftSideName),
                RightLabel = Label(_right, RightSideName),
            };

            // Once, not per row: the rows carry the same text as a Note, but the reader needs to be
            // told the map's fidelity before they start reading it.
            if (_leftTypeless) map.Advisories.Add(TypelessMessage(LeftSideName));
            if (_rightTypeless) map.Advisories.Add(TypelessMessage(RightSideName));

            // The headline of a generational migration: how much of the datatype churn is a rename.
            // Said only when there is something to say — on a pair that renamed nothing, the line
            // would be a zero taking up room the real advisories need.
            if (map.RenamedCount > 0)
                map.Advisories.Add(RenameMessage(map.RenamedCount, map.DataTypeChangedCount));

            return map;
        }

        // ------------------------------------------------------------------------- rows

        /// <summary>Emits the rows for a class both documents have.</summary>
        private void AddMatchedClass(
            List<AttributeMapRow> rows,
            string className,
            FomObjectClass leftClass,
            FomObjectClass rightClass,
            string? note)
        {
            var leftItems = Resolve(leftClass);
            var rightItems = Resolve(rightClass);

            var rightByKey = new Dictionary<string, EffectiveAttribute>(_o.NameComparer);
            foreach (var item in rightItems) rightByKey.TryAdd(item.Key, item);

            var matched = new HashSet<string>(_o.NameComparer);

            foreach (var item in leftItems)
            {
                if (rightByKey.TryGetValue(item.Key, out var counterpart))
                {
                    matched.Add(item.Key);
                    rows.Add(MatchedRow(className, item, counterpart, note));
                }
                else
                {
                    rows.Add(OneSidedRow(className, item, AttributeMapStatus.OnlyInLeft));
                }
            }

            // Attributes B adds to the class trail the ones A already had, so the block still reads in
            // the order the detail screen shows.
            foreach (var item in rightItems)
            {
                if (matched.Contains(item.Key)) continue;
                rows.Add(OneSidedRow(className, item, AttributeMapStatus.OnlyInRight));
            }
        }

        /// <summary>
        /// Emits the rows for a class only one document has. A whole class appearing or disappearing is
        /// precisely what a remap has to account for, so every effective attribute it carries earns a
        /// row rather than vanishing with the class.
        /// </summary>
        private void AddOneSidedClass(
            List<AttributeMapRow> rows,
            string className,
            FomObjectClass objectClass,
            AttributeMapStatus status)
        {
            foreach (var item in Resolve(objectClass))
                rows.Add(OneSidedRow(className, item, status));
        }

        private AttributeMapRow MatchedRow(
            string className,
            EffectiveAttribute left,
            EffectiveAttribute right,
            string? note)
        {
            var leftType = OmtNormalizer.NormalizeText(left.DataType, _o);
            var rightType = OmtNormalizer.NormalizeText(right.DataType, _o);

            // Resolved even when the classification will not need it: the encoding columns are the
            // reader's evidence, and an unresolved "?(Foo)" showing there is itself the answer to
            // "why is this row still flagged when the two names look interchangeable?".
            var leftSignature = leftType is null ? null : _leftTypes.Resolve(left.DataType);
            var rightSignature = rightType is null ? null : _rightTypes.Resolve(right.DataType);

            // The attribute is still on the class either way — inheritance sees to that — so a change
            // of declaring class is context for the reader rather than work for them.
            var moved = !SameDeclaringClass(left.DeclaredIn, right.DeclaredIn);

            AttributeMapStatus status;
            var rowNote = note;

            if (note is not null)
            {
                // One side has no datatype table at all, so "the datatype changed" would be true of
                // every single row and would tell the reader nothing they can act on. The note says
                // what actually happened, and the names lining up is reported as agreement. This
                // takes precedence: with no types to resolve, neither a change nor a rename can be
                // established.
                status = moved ? AttributeMapStatus.Moved : AttributeMapStatus.Same;
            }
            else if (SameText(leftType, rightType))
            {
                status = moved ? AttributeMapStatus.Moved : AttributeMapStatus.Same;
            }
            else if (leftSignature is not null && leftSignature.EncodesTheSameAs(rightSignature))
            {
                // Different spelling, identical bits. The mapping is one-to-one and needs no code,
                // which is exactly what the reader must not have to rediscover 614 times by hand.
                status = AttributeMapStatus.Renamed;
            }
            else
            {
                // The row that means real work: the value still exists but its encoding changed.
                status = AttributeMapStatus.DataTypeChanged;

                // Unless, that is, a name simply defeated the resolver. The row stays flagged —
                // silence would be a claim that the encoding held, which nothing here establishes —
                // but the reader is told the verdict rests on the names alone.
                var unresolved = UnresolvedName(leftSignature) ?? UnresolvedName(rightSignature);
                if (unresolved is not null)
                    rowNote = $"Encoding of '{unresolved}' could not be resolved; compared by name only.";
            }

            return new AttributeMapRow
            {
                ClassName = className,
                AttributeName = left.Name,
                LeftDeclaredIn = left.DeclaredIn,
                LeftDataType = leftType,
                LeftEncoding = leftSignature?.Canonical,
                RightDeclaredIn = right.DeclaredIn,
                RightDataType = rightType,
                RightEncoding = rightSignature?.Canonical,
                Status = status,
                Note = rowNote,
            };
        }

        private AttributeMapRow OneSidedRow(string className, EffectiveAttribute item, AttributeMapStatus status)
        {
            var dataType = OmtNormalizer.NormalizeText(item.DataType, _o);
            var onLeft = status == AttributeMapStatus.OnlyInLeft;

            // A one-sided row has nothing to compare against, but its encoding is still worth
            // showing: it is what a reader needs in order to choose the attribute on the other side
            // that this data could be moved onto.
            var encoding = dataType is null
                ? null
                : (onLeft ? _leftTypes : _rightTypes).Resolve(item.DataType).Canonical;

            return new AttributeMapRow
            {
                ClassName = className,
                AttributeName = item.Name,
                LeftDeclaredIn = onLeft ? item.DeclaredIn : null,
                LeftDataType = onLeft ? dataType : null,
                LeftEncoding = onLeft ? encoding : null,
                RightDeclaredIn = onLeft ? null : item.DeclaredIn,
                RightDataType = onLeft ? null : dataType,
                RightEncoding = onLeft ? null : encoding,
                Status = status,
            };
        }

        /// <summary>
        /// The datatype name behind a signature that could not be resolved, or null when the
        /// signature is absent or did resolve. Used to name the culprit in a row's note.
        /// </summary>
        private static string? UnresolvedName(DataTypeSignature? signature)
        {
            if (signature is null || signature.IsResolved) return null;
            return string.IsNullOrWhiteSpace(signature.SourceName) ? null : signature.SourceName;
        }

        // ------------------------------------------------------------- effective attributes

        /// <summary>
        /// Every attribute the class actually has: everything inherited from its ancestors, in
        /// ancestor order, followed by the ones it declares itself.
        /// </summary>
        /// <remarks>
        /// An attribute redeclared on a subclass overrides the inherited one rather than appearing
        /// twice, matching the detail screen so the two never disagree. Results are memoised: a real
        /// FOM has thousands of attributes across a deep tree, and re-walking the ancestry for every
        /// class would make the map quadratic in the depth of the tree for no reason.
        /// </remarks>
        private IReadOnlyList<EffectiveAttribute> Resolve(FomObjectClass objectClass)
        {
            if (_resolved.TryGetValue(objectClass, out var cached)) return cached;

            // Climb only as far as the nearest ancestor already resolved — normally the direct parent,
            // because classes are visited root-down — so each class costs its own attributes rather
            // than a fresh walk of the whole ancestry. The guard set also stops a malformed parent
            // cycle from looping for ever.
            var chain = new List<FomObjectClass>();
            var guard = new HashSet<FomObjectClass>();
            IReadOnlyList<EffectiveAttribute> resolved = Array.Empty<EffectiveAttribute>();

            for (var current = objectClass; current is not null && guard.Add(current); current = current.Parent)
            {
                if (_resolved.TryGetValue(current, out var known))
                {
                    resolved = known;
                    break;
                }

                chain.Add(current);
            }

            // The chain was collected leaf-first; replay it root-down so inherited attributes lead and
            // the subclass gets the last word on a name.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var owner = chain[i];

                var items = new List<EffectiveAttribute>(resolved);
                var seen = new HashSet<string>(_o.NameComparer);
                foreach (var item in items) seen.Add(item.Key);

                foreach (var attribute in owner.Attributes)
                {
                    var key = MemberKey(attribute);

                    // A nameless attribute is the mark of a partial parse: it can never be matched, and
                    // admitting it would collide with every other nameless one.
                    if (key.Length == 0) continue;
                    if (!seen.Add(key)) continue;

                    items.Add(new EffectiveAttribute(key, RawName(attribute), attribute.DataType, RawName(owner)));
                }

                resolved = items;
                _resolved[owner] = items;
            }

            return resolved;
        }

        /// <summary>
        /// True when the side has attributes but not one of them names a datatype — the signature of an
        /// HLA 1.3 FED, which has no datatype table at all.
        /// </summary>
        /// <remarks>
        /// A side with no attributes is not typeless, it is empty, and must not raise the advisory.
        /// </remarks>
        private bool IsTypeless(List<FomObjectClass> classes)
        {
            var anyAttributes = false;

            foreach (var objectClass in classes)
            {
                foreach (var item in Resolve(objectClass))
                {
                    anyAttributes = true;
                    if (OmtNormalizer.NormalizeText(item.DataType, _o) is not null) return false;
                }
            }

            return anyAttributes;
        }

        /// <summary>The note carried by every matched row when a side cannot express datatypes.</summary>
        private string? TypelessNote()
        {
            if (_leftTypeless && _rightTypeless)
                return $"{TypelessMessage(LeftSideName)} {TypelessMessage(RightSideName)}";

            if (_leftTypeless) return TypelessMessage(LeftSideName);
            if (_rightTypeless) return TypelessMessage(RightSideName);

            return null;
        }

        // ---------------------------------------------------------------------- matching

        /// <summary>
        /// Every object class of the document, root first and then depth-first through the children —
        /// the order the map is read in.
        /// </summary>
        private List<FomObjectClass> ClassesInTreeOrder(FomDocument document)
        {
            var ordered = new List<FomObjectClass>();

            foreach (var root in document.ObjectClasses)
            {
                foreach (var node in root.DescendantsAndSelf())
                {
                    // The MOM is the RTI's own housekeeping model rather than federation data, and it
                    // is a subtree, so skipping a class skips everything below it too.
                    if (_o.IgnoreManagementObjectModel && OmtNormalizer.IsManagementClass(RawClassKey(node)))
                        continue;

                    ordered.Add(node);
                }
            }

            return ordered;
        }

        /// <summary>Datatype equality, where null and empty are the same thing.</summary>
        private bool SameText(string? left, string? right)
        {
            if (left is null || right is null) return left is null && right is null;
            return string.Equals(left, right, _o.NameComparison);
        }

        /// <summary>
        /// True when both sides declare the attribute on the same class, folding the dialect spellings
        /// of the root so <c>ObjectRoot</c> and <c>HLAobjectRoot</c> are not read as a move.
        /// </summary>
        /// <remarks>
        /// An unknown declaring class on either side counts as agreement: a partial parse should not
        /// invent work for the reader.
        /// </remarks>
        private bool SameDeclaringClass(string? left, string? right)
        {
            var a = OmtNormalizer.NormalizeName(left, _o)?.Trim();
            var b = OmtNormalizer.NormalizeName(right, _o)?.Trim();

            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;

            return string.Equals(a, b, _o.NameComparison);
        }

        /// <summary>The dotted name as written, falling back to the local name for a partial parse.</summary>
        private static string RawClassKey(FomNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.QualifiedName)) return node.QualifiedName.Trim();
            return string.IsNullOrWhiteSpace(node.Name) ? string.Empty : node.Name.Trim();
        }

        /// <summary>The local name as written, falling back to the qualified name when it is missing.</summary>
        private static string RawName(FomNode node) =>
            string.IsNullOrWhiteSpace(node.Name) ? RawClassKey(node) : node.Name.Trim();

        /// <summary>Normalised dotted name, used to match a class with its counterpart.</summary>
        private string ClassKey(FomNode node) =>
            OmtNormalizer.NormalizeQualifiedName(RawClassKey(node), _o)?.Trim() ?? string.Empty;

        /// <summary>
        /// Normalised local name, used to match attributes — which is also what folds
        /// <c>privilegeToDelete</c> onto <c>HLAprivilegeToDeleteObject</c>.
        /// </summary>
        private string MemberKey(FomNode node) =>
            OmtNormalizer.NormalizeName(RawName(node), _o)?.Trim() ?? string.Empty;

        /// <summary>
        /// Names a side: the model name, else the file name, else the generic label. The model name
        /// leads because that is what the author called the FOM; the path is only where it happens to
        /// sit on this machine.
        /// </summary>
        private static string Label(FomDocument document, string fallback)
        {
            var name = document.Identification.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

            var path = document.SourcePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var file = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(file)) return file;
            }

            return fallback;
        }
    }
}
