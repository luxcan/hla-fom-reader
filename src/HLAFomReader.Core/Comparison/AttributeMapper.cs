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

    /// <summary>
    /// Lists one document's object classes for a class picker, each with the size of its effective
    /// attribute set.
    /// </summary>
    /// <param name="document">The FOM to inventory.</param>
    /// <param name="options">Matching knobs; strict defaults are used when null. Only
    /// <see cref="ComparisonOptions.IgnoreManagementObjectModel"/> and the name folding affect the
    /// result.</param>
    /// <returns>
    /// Every object class in the document's own tree order — root first, then depth-first through
    /// the children — which is the order the FOM is written in and the order somebody who knows it
    /// expects to scroll through.
    /// </returns>
    /// <remarks>
    /// The counts come from the same memoised walk <see cref="Build"/> uses, so the figure beside a
    /// class in the picker is exactly the number of rows choosing it produces. Nothing here resolves
    /// a datatype, and the run's two resolvers are deferred, so this costs a tree walk and no more.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static IReadOnlyList<ObjectClassSummary> ListClasses(
        FomDocument document, ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var effective = (options ?? new ComparisonOptions()).Clone();

        // The same document on both sides: a listing needs one document's class tree and the
        // effective-set walk, both of which Run already owns, and neither of which reads the other
        // side. The resolvers this would otherwise build are the reason they are Lazy.
        return new Run(document, document, effective).ListClasses(document);
    }

    /// <summary>
    /// Maps <b>one</b> class of <paramref name="left"/> against <b>one</b> class of
    /// <paramref name="right"/>, whatever the two are called.
    /// </summary>
    /// <param name="left">The A side.</param>
    /// <param name="right">The B side.</param>
    /// <param name="leftClassName">Qualified name of the class chosen in A, or null for none.</param>
    /// <param name="rightClassName">Qualified name of the class chosen in B, or null for none.</param>
    /// <param name="options">Matching knobs; strict defaults are used when null.</param>
    /// <returns>
    /// One row per attribute of the union of the two effective sets. With a class on one side only,
    /// every row is <see cref="AttributeMapStatus.Unpaired"/> and carries that side alone. With
    /// neither, the map is empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is what the Attribute data screen runs, and it is a different question from
    /// <see cref="Build"/>. Build asks "how do these two FOMs line up?" and answers class by matched
    /// class. This asks "if I move this class's data onto that one, what happens?" — a question about
    /// a pairing the user made, which no name matching can discover. RPR 2.0 splits RPR 1.0's
    /// <c>Aircraft</c> across a reworked hierarchy, and lining the old class up against the new one
    /// is precisely the judgement the screen exists to support.
    /// </para>
    /// <para>
    /// A name that matches no class resolves to null and behaves exactly like an unpicked side,
    /// per the house rule that content problems yield a thinner result rather than an exception.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either document is null.</exception>
    public static AttributeDataMap BuildForClasses(
        FomDocument left,
        FomDocument right,
        string? leftClassName,
        string? rightClassName,
        ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var effective = (options ?? new ComparisonOptions()).Clone();

        return new Run(left, right, effective).ExecuteForClasses(leftClassName, rightClassName);
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
        internal EffectiveAttribute(
            string key, string name, string? dataType, string declaredIn, string declaredInQualified)
        {
            Key = key;
            Name = name;
            DataType = dataType;
            DeclaredIn = declaredIn;
            DeclaredInQualified = declaredInQualified;
        }

        /// <summary>Normalised name, used to line the attribute up with the other side.</summary>
        internal string Key { get; }

        /// <summary>The name as the document spells it, which is what the reader sees.</summary>
        internal string Name { get; }

        /// <summary>Datatype as written; null for a document with no datatype table.</summary>
        internal string? DataType { get; }

        /// <summary>Local name of the declaring class — an ancestor whenever the attribute is inherited.</summary>
        internal string DeclaredIn { get; }

        /// <summary>
        /// Dotted name of the declaring class. Kept beside the local one because two independently
        /// chosen classes may sit in unrelated trees that both contain a <c>Platform</c>.
        /// </summary>
        internal string DeclaredInQualified { get; }
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
        /// <remarks>
        /// Deferred because <see cref="ListClasses"/> uses the same run purely for its memoised
        /// effective-attribute walk and resolves no datatype at all. Building both resolvers for a
        /// picker inventory would read every datatype table of the document to answer a question
        /// about class names.
        /// </remarks>
        private readonly Lazy<DataTypeResolver> _leftTypes;

        /// <summary>Datatype resolution for the B side; see <see cref="_leftTypes"/>.</summary>
        private readonly Lazy<DataTypeResolver> _rightTypes;

        private bool _leftTypeless;
        private bool _rightTypeless;

        /// <summary>
        /// Whether a change of declaring class is worth reporting as <see cref="AttributeMapStatus.Moved"/>.
        /// </summary>
        /// <remarks>
        /// True for the whole-FOM map, where both sides walk the same matched class and a different
        /// ancestor really is a move. False for a class pair the user chose by hand: see the comment
        /// in <see cref="MatchedRow"/>.
        /// </remarks>
        private bool _reportMoves = true;

        internal Run(FomDocument left, FomDocument right, ComparisonOptions options)
        {
            _left = left;
            _right = right;
            _o = options;

            _leftTypes = new Lazy<DataTypeResolver>(() => new DataTypeResolver(left));
            _rightTypes = new Lazy<DataTypeResolver>(() => new DataTypeResolver(right));
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
                    AddOneSidedClass(rows, className, leftClass, AttributeMapStatus.OnlyInLeft, onLeft: true);
                }
            }

            // Classes only B has come last: they are additions, and a reader works through what they
            // already have before reading what is new.
            foreach (var rightClass in rightClasses)
            {
                if (matchedRight.Contains(rightClass)) continue;
                AddOneSidedClass(rows, RawClassKey(rightClass), rightClass, AttributeMapStatus.OnlyInRight, onLeft: false);
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

        /// <summary>Backs <see cref="AttributeMapper.ListClasses"/>.</summary>
        internal IReadOnlyList<ObjectClassSummary> ListClasses(FomDocument document)
        {
            var classes = ClassesInTreeOrder(document);
            var summaries = new List<ObjectClassSummary>(classes.Count);

            foreach (var objectClass in classes)
            {
                var name = RawClassKey(objectClass);

                // A class with no name is the mark of a partial parse. It could never be picked,
                // and an unnamed entry in the drop-down is unreachable by typing.
                if (name.Length == 0) continue;

                summaries.Add(new ObjectClassSummary(name, Resolve(objectClass).Count));
            }

            return summaries;
        }

        /// <summary>Backs <see cref="AttributeMapper.BuildForClasses"/>.</summary>
        internal AttributeDataMap ExecuteForClasses(string? leftClassName, string? rightClassName)
        {
            var leftClasses = ClassesInTreeOrder(_left);
            var rightClasses = ClassesInTreeOrder(_right);

            // Whether a side carries datatypes at all stays a property of the whole document rather
            // than of the chosen class: a FED has no datatype table anywhere, and saying so from one
            // class's attributes would depend on which class the user happened to pick.
            _leftTypeless = IsTypeless(leftClasses);
            _rightTypeless = IsTypeless(rightClasses);

            var leftClass = FindClass(leftClasses, leftClassName);
            var rightClass = FindClass(rightClasses, rightClassName);

            var rows = new List<AttributeMapRow>();

            if (leftClass is not null && rightClass is not null)
            {
                // The two classes were chosen by hand, so a different declaring ancestor on each
                // side is the pairing itself rather than a move. See MatchedRow.
                _reportMoves = SameClass(leftClass, rightClass);

                // A's spelling leads for a matched pair, as it does on the whole-FOM map; the two
                // qualified names are carried on the map itself, where they can say which is which.
                AddMatchedClass(rows, RawClassKey(leftClass), leftClass, rightClass, TypelessNote());
            }
            else if (leftClass is not null)
            {
                AddOneSidedClass(
                    rows, RawClassKey(leftClass), leftClass, AttributeMapStatus.Unpaired, onLeft: true);
            }
            else if (rightClass is not null)
            {
                AddOneSidedClass(
                    rows, RawClassKey(rightClass), rightClass, AttributeMapStatus.Unpaired, onLeft: false);
            }

            var map = new AttributeDataMap
            {
                Rows = rows,
                LeftLabel = Label(_left, LeftSideName),
                RightLabel = Label(_right, RightSideName),

                // The name as the document spells it, not as the caller typed it: the two are the
                // same string today, but the map is read back by the export and the headline, and
                // echoing the caller would let a differently cased request rename the class on screen.
                LeftClassName = leftClass is null ? null : RawClassKey(leftClass),
                RightClassName = rightClass is null ? null : RawClassKey(rightClass),
            };

            if (_leftTypeless) map.Advisories.Add(TypelessMessage(LeftSideName));
            if (_rightTypeless) map.Advisories.Add(TypelessMessage(RightSideName));

            if (map.RenamedCount > 0)
                map.Advisories.Add(RenameMessage(map.RenamedCount, map.DataTypeChangedCount));

            return map;
        }

        /// <summary>
        /// The class a picker's qualified name names, or null when nothing is chosen or nothing
        /// matches. Folded through the same normalisation the rest of the matching uses, so a
        /// dialect spelling of the root does not lose the class.
        /// </summary>
        private FomObjectClass? FindClass(List<FomObjectClass> classes, string? qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName)) return null;

            var wanted = OmtNormalizer.NormalizeQualifiedName(qualifiedName.Trim(), _o)?.Trim();
            if (string.IsNullOrEmpty(wanted)) return null;

            foreach (var objectClass in classes)
            {
                var key = ClassKey(objectClass);
                if (key.Length != 0 && string.Equals(key, wanted, _o.NameComparison)) return objectClass;
            }

            return null;
        }

        /// <summary>True when both sides picked what is, after folding, the same class.</summary>
        private bool SameClass(FomObjectClass left, FomObjectClass right)
        {
            var a = ClassKey(left);
            var b = ClassKey(right);

            return a.Length != 0 && b.Length != 0 && string.Equals(a, b, _o.NameComparison);
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
                    rows.Add(OneSidedRow(className, item, AttributeMapStatus.OnlyInLeft, onLeft: true));
                }
            }

            // Attributes B adds to the class trail the ones A already had, so the block still reads in
            // the order the detail screen shows.
            foreach (var item in rightItems)
            {
                if (matched.Contains(item.Key)) continue;
                rows.Add(OneSidedRow(className, item, AttributeMapStatus.OnlyInRight, onLeft: false));
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
            AttributeMapStatus status,
            bool onLeft)
        {
            foreach (var item in Resolve(objectClass))
                rows.Add(OneSidedRow(className, item, status, onLeft));
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
            var leftSignature = leftType is null ? null : _leftTypes.Value.Resolve(left.DataType);
            var rightSignature = rightType is null ? null : _rightTypes.Value.Resolve(right.DataType);

            // The attribute is still on the class either way — inheritance sees to that — so a change
            // of declaring class is context for the reader rather than work for them.
            //
            // Only ever reported when the two sides are looking at the same class. Once the user
            // picks Aircraft against FixedWingAircraft, the two attributes are necessarily declared
            // on different ancestors of two different trees, so every single row would come back
            // "Moved" — restating the pairing the user made themselves as though it were a finding.
            var moved = _reportMoves && !SameDeclaringClass(left.DeclaredIn, right.DeclaredIn);

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
                // The same spelling is not the same type. Each side's name is resolved through its
                // OWN document's tables, so two FOMs can both declare a WorldLocationStruct whose
                // fields differ — a generation keeping a struct's name and changing its contents is
                // the silent-corruption case a remap most needs to be told about, and reporting
                // Same on the name alone is exactly how it stays silent. It would also contradict
                // the two encoding columns sitting beside the verdict.
                status = ReEncodes(leftSignature, rightSignature)
                    ? AttributeMapStatus.DataTypeChanged
                    : moved ? AttributeMapStatus.Moved : AttributeMapStatus.Same;
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

                // Only when the spellings genuinely differ. The two sides matched on a normalised
                // key, which folds privilegeToDelete onto HLAprivilegeToDeleteObject, and the
                // side-by-side sheet has to be able to print both names.
                RightAttributeName =
                    string.Equals(left.Name, right.Name, StringComparison.Ordinal) ? null : right.Name,

                LeftDeclaredIn = left.DeclaredIn,
                LeftDeclaredInQualified = left.DeclaredInQualified,
                LeftDataType = leftType,
                LeftEncoding = leftSignature?.Canonical,
                RightDeclaredIn = right.DeclaredIn,
                RightDeclaredInQualified = right.DeclaredInQualified,
                RightDataType = rightType,
                RightEncoding = rightSignature?.Canonical,
                Status = status,
                Note = rowNote,
            };
        }

        /// <summary>
        /// A row carrying one side only.
        /// </summary>
        /// <param name="onLeft">
        /// Which side's columns to fill. Passed rather than derived from
        /// <paramref name="status"/>, because <see cref="AttributeMapStatus.Unpaired"/> says nothing
        /// about which side the attribute came from — only that the other one has no class chosen.
        /// </param>
        private AttributeMapRow OneSidedRow(
            string className, EffectiveAttribute item, AttributeMapStatus status, bool onLeft)
        {
            var dataType = OmtNormalizer.NormalizeText(item.DataType, _o);

            // A one-sided row has nothing to compare against, but its encoding is still worth
            // showing: it is what a reader needs in order to choose the attribute on the other side
            // that this data could be moved onto.
            var encoding = dataType is null
                ? null
                : (onLeft ? _leftTypes.Value : _rightTypes.Value).Resolve(item.DataType).Canonical;

            return new AttributeMapRow
            {
                ClassName = className,
                AttributeName = item.Name,
                LeftDeclaredIn = onLeft ? item.DeclaredIn : null,
                LeftDeclaredInQualified = onLeft ? item.DeclaredInQualified : null,
                LeftDataType = onLeft ? dataType : null,
                LeftEncoding = onLeft ? encoding : null,
                RightDeclaredIn = onLeft ? null : item.DeclaredIn,
                RightDeclaredInQualified = onLeft ? null : item.DeclaredInQualified,
                RightDataType = onLeft ? null : dataType,
                RightEncoding = onLeft ? null : encoding,
                Status = status,
            };
        }

        /// <summary>
        /// True when both sides resolved and resolved to different encodings — the row moves
        /// different bytes.
        /// </summary>
        /// <remarks>
        /// A side that could not be resolved is evidence of nothing: it neither proves the encoding
        /// changed nor proves it held. So an unresolved name answers false here and the row keeps
        /// whatever its names said, rather than being flagged on a resolution that never happened.
        /// This is <see cref="AttributeMapRow.EncodingDiffers"/>'s reasoning, applied while the row
        /// is still being classified. Note that <see cref="DataTypeSignature.EncodesTheSameAs"/>
        /// cannot be negated to get this: it answers false for an unresolved side too.
        /// </remarks>
        private static bool ReEncodes(DataTypeSignature? left, DataTypeSignature? right)
        {
            if (left is null || right is null) return false;
            if (!left.IsResolved || !right.IsResolved) return false;

            return !string.Equals(left.Canonical, right.Canonical, StringComparison.Ordinal);
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

                    items.Add(new EffectiveAttribute(
                        key, RawName(attribute), attribute.DataType, RawName(owner), RawClassKey(owner)));
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
