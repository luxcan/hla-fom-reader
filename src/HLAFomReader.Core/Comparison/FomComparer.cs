using System;
using System.Collections.Generic;
using System.Linq;
using HLAFomReader.Core.Model;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// Compares two normalised <see cref="FomDocument"/>s into the merged <see cref="DiffNode"/> tree
/// consumed by the UI and the report writers.
/// </summary>
/// <remarks>
/// <para>
/// Elements are matched by normalised name — never by position — through dictionaries, so a FOM
/// holding thousands of classes still compares in linear time. Every property that is looked at is
/// recorded as a <see cref="PropertyDiff"/>, including the ones that agree, so the UI can render a
/// full side-by-side table rather than only the deltas.
/// </para>
/// <para>
/// Cross-standard comparison is deliberately strict: anything HLA 1.3 cannot express (datatypes,
/// dimensions, sharing, semantics, the 1516-only tables) is a real difference. Those differences
/// carry a <see cref="PropertyDiff.Reason"/> explaining that the concept does not exist in the
/// other standard, but they still count towards the totals.
/// </para>
/// <para>
/// <see cref="ComparisonOptions.IgnoreInexpressibleProperties"/> relaxes exactly that: a row whose
/// reason is one of the two "not expressible" tags keeps its values and its reason but is reported
/// as equal, and a table that exists on one side only because the other standard has no such table
/// is left out altogether. Nothing else changes, so a same-standard comparison — which never has a
/// reason to filter — is unaffected either way.
/// </para>
/// <para>
/// <see cref="ComparisonOptions.Depth"/> is the other relaxation, and works the same way: it decides
/// how much of a MATCHED element is inspected, never which elements are matched. Additions and
/// removals are therefore reported in full at every depth, and a property the depth does not inspect
/// is still recorded with both values and its reason — it simply stops counting, which is enough for
/// <see cref="DiffNode.Recount"/> to demote the node that held it. Both relaxations meet at
/// <c>CountsAsDifference</c>, the one place a row is allowed to become a difference.
/// </para>
/// <para>The comparer holds no state between calls and can be reused or shared.</para>
/// </remarks>
public sealed class FomComparer
{
    /// <summary>Compares two documents, labelling each side with its file name.</summary>
    /// <param name="left">The A side of the comparison.</param>
    /// <param name="right">The B side of the comparison.</param>
    /// <param name="options">Comparison knobs; strict defaults are used when null.</param>
    public ComparisonResult Compare(FomDocument left, FomDocument right, ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return Compare(left, right, DefaultLabel(left, "FOM A"), DefaultLabel(right, "FOM B"), options);
    }

    /// <summary>Compares two documents using explicit display labels for the two sides.</summary>
    /// <param name="left">The A side of the comparison.</param>
    /// <param name="right">The B side of the comparison.</param>
    /// <param name="leftLabel">Display name for the A side, e.g. the file name.</param>
    /// <param name="rightLabel">Display name for the B side.</param>
    /// <param name="options">Comparison knobs; strict defaults are used when null.</param>
    public ComparisonResult Compare(
        FomDocument left,
        FomDocument right,
        string leftLabel,
        string rightLabel,
        ComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // Cloned so a later change to the caller's instance cannot invalidate the stored result.
        var effective = (options ?? new ComparisonOptions()).Clone();

        var run = new Run(
            left,
            right,
            effective,
            string.IsNullOrWhiteSpace(leftLabel) ? "FOM A" : leftLabel.Trim(),
            string.IsNullOrWhiteSpace(rightLabel) ? "FOM B" : rightLabel.Trim());

        return run.Execute();
    }

    /// <summary>Falls back from an absent source path to a generic label.</summary>
    private static string DefaultLabel(FomDocument document, string fallback)
    {
        var path = document.SourcePath;
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        var name = System.IO.Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    /// <summary>
    /// One comparison in flight. Keeping the working state here — the option set, the standard
    /// flags and the "a normalisation actually changed a match" bookkeeping — keeps
    /// <see cref="FomComparer"/> itself stateless.
    /// </summary>
    private sealed class Run
    {
        private const string KeyIdentification = "identification";
        private const string KeyObjects = "objects";
        private const string KeyInteractions = "interactions";
        private const string KeyDataTypes = "datatypes";
        private const string KeyDimensions = "dimensions";
        private const string KeySpaces = "spaces";
        private const string KeyTransportations = "transportations";
        private const string KeySynchronizations = "synchronizations";
        private const string KeyUpdateRates = "updateRates";
        private const string KeySwitches = "switches";
        private const string KeyTags = "tags";
        private const string KeyTime = "time";
        private const string KeyNotes = "notes";

        /// <summary>Reason stamped on differences caused by HLA 1.3 having no such concept.</summary>
        private const string NotIn13 = "Not expressible in HLA 1.3";

        /// <summary>Reason stamped on differences caused by IEEE 1516 having dropped a 1.3 concept.</summary>
        private const string NotIn1516 = "Not expressible in IEEE 1516; use dimensions";

        /// <summary>Display title and path key of each of the six datatype tables.</summary>
        private static readonly (string Title, string Key) GroupBasic = ("Basic data representations", "basicData");
        private static readonly (string Title, string Key) GroupSimple = ("Simple datatypes", "simple");
        private static readonly (string Title, string Key) GroupEnumerated = ("Enumerated datatypes", "enumerated");
        private static readonly (string Title, string Key) GroupArray = ("Array datatypes", "array");
        private static readonly (string Title, string Key) GroupFixedRecord = ("Fixed record datatypes", "fixedRecord");
        private static readonly (string Title, string Key) GroupVariantRecord = ("Variant record datatypes", "variantRecord");

        private readonly FomDocument _left;
        private readonly FomDocument _right;
        private readonly ComparisonOptions _o;
        private readonly StringComparer _names;
        private readonly string _leftLabel;
        private readonly string _rightLabel;

        private readonly bool _leftIs13;
        private readonly bool _rightIs13;

        /// <summary>True when one side is HLA 1.3 and the other is one of the 1516 standards.</summary>
        private readonly bool _cross13;

        /// <summary>Paths handed out so far, so a malformed FOM with duplicate names still gets unique paths.</summary>
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        private bool _rootAliasHelped;
        private bool _caseHelped;
        private bool _transportOrderHelped;
        private bool _whitespaceHelped;

        /// <summary>Property rows that still count as a difference in the finished tree.</summary>
        private int _differingProperties;

        /// <summary>Of those, the ones that differ only because a standard cannot express them.</summary>
        private int _countedGapProperties;

        /// <summary>Format-gap rows that were reported as equal because the option is on.</summary>
        private int _hiddenGapProperties;

        /// <summary>
        /// Rows that genuinely differ and are not a filtered format gap, but were reported as equal
        /// because <see cref="ComparisonOptions.Depth"/> does not look at that property. Counted at
        /// the one choke point every row passes through, so it cannot drift from the finished tree.
        /// </summary>
        private int _depthFilteredProperties;

        private int _gapsLackedBy13;
        private int _gapsLackedBy1516;

        internal Run(FomDocument left, FomDocument right, ComparisonOptions options, string leftLabel, string rightLabel)
        {
            _left = left;
            _right = right;
            _o = options;
            _names = options.NameComparer;
            _leftLabel = leftLabel;
            _rightLabel = rightLabel;

            _leftIs13 = left.Standard == FomStandard.Hla13;
            _rightIs13 = right.Standard == FomStandard.Hla13;
            _cross13 = (_leftIs13 && Is1516(right.Standard)) || (_rightIs13 && Is1516(left.Standard));
        }

        private static bool Is1516(FomStandard standard) =>
            standard is FomStandard.Ieee1516_2000 or FomStandard.Ieee1516_2010 or FomStandard.Ieee1516_2025;

        // ---------------------------------------------------------------- top level

        /// <summary>Builds the whole tree, counts it once, prunes it if asked and wraps it up.</summary>
        internal ComparisonResult Execute()
        {
            var root = new DiffNode
            {
                Name = "Comparison",
                Path = string.Empty,
                Category = DiffCategory.Root,
                Kind = DiffKind.Unchanged,
                LeftName = _leftLabel,
                RightName = _rightLabel,
            };

            AddChild(root, BuildIdentification());
            AddChild(root, BuildObjectClasses());
            AddChild(root, BuildInteractionClasses());
            AddChild(root, BuildDataTypes());
            AddChild(root, BuildDimensions());
            AddChild(root, BuildRoutingSpaces());
            AddChild(root, BuildTransportations());
            AddChild(root, BuildSynchronizations());
            AddChild(root, BuildUpdateRates());
            AddChild(root, BuildSwitches());
            AddChild(root, BuildTags());
            AddChild(root, BuildTime());
            AddChild(root, BuildNotes());

            // Counted once, on the finished tree: pruning below must not change the totals.
            root.Recount();

            // Read off the same finished tree, and before pruning can drop the now-unchanged nodes a
            // filtered row sits on, so the advisories quote what the comparison actually did.
            MeasureFormatGaps(root);

            if (!_o.KeepUnchanged) Prune(root);

            var result = new ComparisonResult
            {
                Left = _left,
                Right = _right,
                Options = _o,
                Root = root,
                LeftLabel = _leftLabel,
                RightLabel = _rightLabel,
            };

            BuildAdvisories(result);
            return result;
        }

        /// <summary>Appends a section or group, which is null when it would have been empty.</summary>
        private static void AddChild(DiffNode parent, DiffNode? child)
        {
            if (child is not null) parent.Children.Add(child);
        }

        /// <summary>
        /// Drops unchanged nodes once the counts are in. A node that survived <see cref="DiffNode.Recount"/>
        /// as <see cref="DiffKind.Unchanged"/> has, by construction, no differing descendant, so this
        /// can never hide a difference.
        /// </summary>
        private static void Prune(DiffNode node)
        {
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (child.Kind == DiffKind.Unchanged)
                    node.Children.RemoveAt(i);
                else
                    Prune(child);
            }
        }

        // ---------------------------------------------------------------- identification

        private DiffNode? BuildIdentification()
        {
            if (_o.IgnoreIdentification) return null;

            var l = _left.Identification;
            var r = _right.Identification;

            // A FED header carries a federation name, the literal type "FED" and a FED version and
            // nothing else, so every other field is a format gap rather than something an author
            // dropped. The reason only sticks when the 1.3 side is the blank one — see ApplyReason.
            var only1516 = Reason1516Only();

            var fields = new (string Name, string Key, string? Left, string? Right, string? Reason)[]
            {
                ("Name", "name", l?.Name, r?.Name, null),
                ("Type", "type", l?.Type, r?.Type, null),
                ("Version", "version", l?.Version, r?.Version, null),
                ("ModificationDate", "modificationDate", l?.ModificationDate, r?.ModificationDate, only1516),
                ("SecurityClassification", "securityClassification", l?.SecurityClassification, r?.SecurityClassification, only1516),
                ("ReleaseRestriction", "releaseRestriction", l?.ReleaseRestriction, r?.ReleaseRestriction, only1516),
                ("Purpose", "purpose", l?.Purpose, r?.Purpose, only1516),
                ("ApplicationDomain", "applicationDomain", l?.ApplicationDomain, r?.ApplicationDomain, only1516),
                ("Description", "description", l?.Description, r?.Description, only1516),
                ("UseLimitation", "useLimitation", l?.UseLimitation, r?.UseLimitation, only1516),
                ("Reference", "reference", l?.Reference, r?.Reference, only1516),
                ("Other", "other", l?.Other, r?.Other, only1516),
                ("Keywords", "keywords", JoinList(l?.Keywords, "; "), JoinList(r?.Keywords, "; "), only1516),
                ("Points of contact", "pointsOfContact", JoinList(l?.PointsOfContact, "; "), JoinList(r?.PointsOfContact, "; "), only1516),
                ("Use history", "useHistory", JoinList(l?.UseHistory, "; "), JoinList(r?.UseHistory, "; "), only1516),
            };

            // Only fields one of the two documents actually carries; an empty header block is not a
            // table. A field the other standard has no slot for goes out with the rest of the format
            // gaps rather than being reported as an addition.
            var populated = fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Left) || !string.IsNullOrWhiteSpace(f.Right))
                .Where(f => !IsFilteredGap(ApplyReason(f.Reason, f.Left, f.Right)))
                .ToList();

            if (populated.Count == 0) return null;

            var section = Section("Model identification", KeyIdentification, DiffCategory.Identification);

            foreach (var field in populated)
            {
                var node = new DiffNode
                {
                    Name = field.Name,
                    Path = UniquePath($"{KeyIdentification}/{field.Key}"),
                    Category = DiffCategory.IdentificationField,
                    LeftName = string.IsNullOrWhiteSpace(field.Left) ? null : field.Name,
                    RightName = string.IsNullOrWhiteSpace(field.Right) ? null : field.Name,
                    Kind = string.IsNullOrWhiteSpace(field.Left)
                        ? DiffKind.Added
                        : string.IsNullOrWhiteSpace(field.Right) ? DiffKind.Removed : DiffKind.Unchanged,
                };

                AddProperty(node, field.Name, field.Left, field.Right, field.Reason);
                section.Children.Add(node);
            }

            return section;
        }

        // ---------------------------------------------------------------- object classes

        private DiffNode? BuildObjectClasses()
        {
            var left = FilterObjectClasses(_left.ObjectClasses);
            var right = FilterObjectClasses(_right.ObjectClasses);
            if (left.Count == 0 && right.Count == 0) return null;

            var section = Section("Object classes", KeyObjects, DiffCategory.Section);
            foreach (var (l, r) in MatchLists(left, right, ClassKey, RawClassKey))
                section.Children.Add(BuildObjectClass(l, r));

            return section;
        }

        private DiffNode BuildObjectClass(FomObjectClass? l, FomObjectClass? r)
        {
            var source = (l ?? r)!;
            var node = NewNode(source.Name, $"{KeyObjects}/{ClassKey(source)}", DiffCategory.ObjectClass, l, r);

            // Qualified names as written, so a cross-standard match still shows both spellings.
            node.LeftName = l is null ? null : RawClassKey(l);
            node.RightName = r is null ? null : RawClassKey(r);

            AddProperty(node, "Sharing", l?.Sharing, r?.Sharing, Reason1516Only());
            if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
            if (!_o.IgnoreNotes) AddProperty(node, "Notes", l?.Notes, r?.Notes, Reason1516Only());

            foreach (var (la, ra) in MatchLists(Items(l?.Attributes), Items(r?.Attributes), MemberKey, RawName))
                node.Children.Add(BuildAttribute(node, la, ra));

            foreach (var (lc, rc) in MatchLists(FilterObjectClasses(l?.Children), FilterObjectClasses(r?.Children), ClassKey, RawClassKey))
                node.Children.Add(BuildObjectClass(lc, rc));

            return node;
        }

        private DiffNode BuildAttribute(DiffNode owner, FomAttribute? l, FomAttribute? r)
        {
            var source = (l ?? r)!;
            var node = NewNode(source.Name, $"{owner.Path}/{MemberKey(source)}", DiffCategory.Attribute, l, r);

            AddProperty(node, "DataType", l?.DataType, r?.DataType, Reason1516Only());

            // The HLA 1.3 OMT attribute table carries these five on the attribute itself. IEEE 1516
            // moved them onto the simple datatype, so a 1516 attribute has nowhere to write them
            // down: they are the mirror image of RoutingSpace below, a format gap in the 1516
            // direction. Routing them through Reason13Only rather than a tag of their own is what
            // makes strict mode report them and IgnoreInexpressibleProperties hide them, since both
            // behaviours hang off IsFormatGap. Within one standard the helper yields null, so a
            // FED (which leaves them empty) against a 1.3 OMT (which fills them in) reads as the
            // genuine, authored difference it is.
            AddProperty(node, "Cardinality", l?.Cardinality, r?.Cardinality, Reason13Only());
            AddProperty(node, "Units", l?.Units, r?.Units, Reason13Only());
            AddProperty(node, "Resolution", l?.Resolution, r?.Resolution, Reason13Only());
            AddProperty(node, "Accuracy", l?.Accuracy, r?.Accuracy, Reason13Only());
            AddProperty(node, "AccuracyCondition", l?.AccuracyCondition, r?.AccuracyCondition, Reason13Only());

            AddProperty(node, "UpdateType", l?.UpdateType, r?.UpdateType, Reason1516Only());
            AddProperty(node, "UpdateCondition", l?.UpdateCondition, r?.UpdateCondition, Reason1516Only());
            AddProperty(node, "Ownership", l?.Ownership, r?.Ownership, Reason1516Only());
            AddProperty(node, "Sharing", l?.Sharing, r?.Sharing, Reason1516Only());
            AddTransportation(node, l?.Transportation, r?.Transportation);
            AddOrder(node, l?.Order, r?.Order);
            AddProperty(node, "Dimensions", JoinList(l?.Dimensions), JoinList(r?.Dimensions), Reason1516Only());
            AddProperty(node, "RoutingSpace", l?.RoutingSpace, r?.RoutingSpace, Reason13Only());
            if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
            if (!_o.IgnoreNotes) AddProperty(node, "Notes", l?.Notes, r?.Notes, Reason1516Only());

            return node;
        }

        // ---------------------------------------------------------------- interaction classes

        private DiffNode? BuildInteractionClasses()
        {
            var left = FilterInteractionClasses(_left.InteractionClasses);
            var right = FilterInteractionClasses(_right.InteractionClasses);
            if (left.Count == 0 && right.Count == 0) return null;

            var section = Section("Interaction classes", KeyInteractions, DiffCategory.Section);
            foreach (var (l, r) in MatchLists(left, right, ClassKey, RawClassKey))
                section.Children.Add(BuildInteractionClass(l, r));

            return section;
        }

        private DiffNode BuildInteractionClass(FomInteractionClass? l, FomInteractionClass? r)
        {
            var source = (l ?? r)!;
            var node = NewNode(source.Name, $"{KeyInteractions}/{ClassKey(source)}", DiffCategory.InteractionClass, l, r);

            node.LeftName = l is null ? null : RawClassKey(l);
            node.RightName = r is null ? null : RawClassKey(r);

            AddProperty(node, "Sharing", l?.Sharing, r?.Sharing, Reason1516Only());
            AddTransportation(node, l?.Transportation, r?.Transportation);
            AddOrder(node, l?.Order, r?.Order);
            AddProperty(node, "Dimensions", JoinList(l?.Dimensions), JoinList(r?.Dimensions), Reason1516Only());
            AddProperty(node, "RoutingSpace", l?.RoutingSpace, r?.RoutingSpace, Reason13Only());
            if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
            if (!_o.IgnoreNotes) AddProperty(node, "Notes", l?.Notes, r?.Notes, Reason1516Only());

            foreach (var (lp, rp) in MatchLists(Items(l?.Parameters), Items(r?.Parameters), MemberKey, RawName))
                node.Children.Add(BuildParameter(node, lp, rp));

            foreach (var (lc, rc) in MatchLists(FilterInteractionClasses(l?.Children), FilterInteractionClasses(r?.Children), ClassKey, RawClassKey))
                node.Children.Add(BuildInteractionClass(lc, rc));

            return node;
        }

        private DiffNode BuildParameter(DiffNode owner, FomParameter? l, FomParameter? r)
        {
            var source = (l ?? r)!;
            var node = NewNode(source.Name, $"{owner.Path}/{MemberKey(source)}", DiffCategory.Parameter, l, r);

            AddProperty(node, "DataType", l?.DataType, r?.DataType, Reason1516Only());

            // Same five 1.3-only fields as on an attribute, and for the same reason — the 1.3 OMT
            // parameter table states them per parameter, while 1516 states them once on the datatype.
            AddProperty(node, "Cardinality", l?.Cardinality, r?.Cardinality, Reason13Only());
            AddProperty(node, "Units", l?.Units, r?.Units, Reason13Only());
            AddProperty(node, "Resolution", l?.Resolution, r?.Resolution, Reason13Only());
            AddProperty(node, "Accuracy", l?.Accuracy, r?.Accuracy, Reason13Only());
            AddProperty(node, "AccuracyCondition", l?.AccuracyCondition, r?.AccuracyCondition, Reason13Only());

            if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
            if (!_o.IgnoreNotes) AddProperty(node, "Notes", l?.Notes, r?.Notes, Reason1516Only());

            return node;
        }

        // ---------------------------------------------------------------- datatypes

        private DiffNode? BuildDataTypes()
        {
            if (_o.IgnoreDataTypes) return null;

            var left = _left.DataTypes;
            var right = _right.DataTypes;
            if (left.IsEmpty && right.IsEmpty) return null;
            if (SkipInexpressibleTable(left.IsEmpty, right.IsEmpty, onlyIn1516: true)) return null;

            var section = Section("Datatypes", KeyDataTypes, DiffCategory.Section);
            var reason = TableReason(left.IsEmpty, right.IsEmpty, onlyIn1516: true);
            AddTableProperty(section, reason, left.TotalCount, right.TotalCount);

            // Name -> kind on each side, so a datatype that changed kind can explain itself.
            var leftKinds = KindMap(left);
            var rightKinds = KindMap(right);

            AddChild(section, BuildDataTypeGroup(GroupBasic, left.BasicDataRepresentations, right.BasicDataRepresentations, FillBasic, leftKinds, rightKinds, reason));
            AddChild(section, BuildDataTypeGroup(GroupSimple, left.SimpleDataTypes, right.SimpleDataTypes, FillSimple, leftKinds, rightKinds, reason));
            AddChild(section, BuildDataTypeGroup(GroupEnumerated, left.EnumeratedDataTypes, right.EnumeratedDataTypes, FillEnumerated, leftKinds, rightKinds, reason));
            AddChild(section, BuildDataTypeGroup(GroupArray, left.ArrayDataTypes, right.ArrayDataTypes, FillArray, leftKinds, rightKinds, reason));
            AddChild(section, BuildDataTypeGroup(GroupFixedRecord, left.FixedRecordDataTypes, right.FixedRecordDataTypes, FillFixedRecord, leftKinds, rightKinds, reason));
            AddChild(section, BuildDataTypeGroup(GroupVariantRecord, left.VariantRecordDataTypes, right.VariantRecordDataTypes, FillVariantRecord, leftKinds, rightKinds, reason));

            return section;
        }

        /// <summary>Maps every datatype name on one side to the display name of the table it lives in.</summary>
        private Dictionary<string, string> KindMap(FomDataTypeTables tables)
        {
            var map = new Dictionary<string, string>(_names);

            void Register(IEnumerable<FomNode> nodes, string kind)
            {
                foreach (var node in nodes)
                {
                    var key = MemberKey(node);
                    if (key.Length != 0) map[key] = kind;
                }
            }

            Register(tables.BasicDataRepresentations, GroupBasic.Title);
            Register(tables.SimpleDataTypes, GroupSimple.Title);
            Register(tables.EnumeratedDataTypes, GroupEnumerated.Title);
            Register(tables.ArrayDataTypes, GroupArray.Title);
            Register(tables.FixedRecordDataTypes, GroupFixedRecord.Title);
            Register(tables.VariantRecordDataTypes, GroupVariantRecord.Title);
            return map;
        }

        /// <summary>Builds one of the six datatype tables, with its enumerators / fields / alternatives.</summary>
        private DiffNode? BuildDataTypeGroup<T>(
            (string Title, string Key) group,
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            Action<DiffNode, T?, T?> fill,
            Dictionary<string, string> leftKinds,
            Dictionary<string, string> rightKinds,
            string? tableReason)
            where T : FomNode
        {
            if (left.Count == 0 && right.Count == 0) return null;

            var groupPath = $"{KeyDataTypes}/{group.Key}";
            var groupNode = new DiffNode
            {
                Name = group.Title,
                Path = UniquePath(groupPath),
                Category = DiffCategory.DataTypeGroup,
                Kind = DiffKind.Unchanged,
                LeftName = group.Title,
                RightName = group.Title,
            };

            foreach (var (l, r) in MatchLists(left, right, MemberKey, RawName))
            {
                var source = (l ?? r)!;
                var key = MemberKey(source);
                var node = NewNode(source.Name, $"{groupPath}/{key}", DiffCategory.DataType, l, r);

                AddKindChange(node, key, group.Title, l, r, leftKinds, rightKinds);
                AddPresenceProperty(node, l, r, tableReason);
                fill(node, l, r);

                groupNode.Children.Add(node);
            }

            return groupNode;
        }

        /// <summary>
        /// A datatype that exists on both sides but in different tables is reported as a removal plus
        /// an addition; this records which kinds were involved so the pair can be read as one change.
        /// </summary>
        private void AddKindChange<T>(
            DiffNode node,
            string key,
            string thisKind,
            T? l,
            T? r,
            Dictionary<string, string> leftKinds,
            Dictionary<string, string> rightKinds)
            where T : FomNode
        {
            if (key.Length == 0) return;
            if (l is not null && r is not null) return;

            string leftKind;
            string rightKind;

            if (l is not null)
            {
                leftKind = thisKind;
                if (!rightKinds.TryGetValue(key, out var other) || _names.Equals(other, thisKind)) return;
                rightKind = other;
            }
            else
            {
                rightKind = thisKind;
                if (!leftKinds.TryGetValue(key, out var other) || _names.Equals(other, thisKind)) return;
                leftKind = other;
            }

            // Not a format gap — somebody moved the datatype between tables — so this reason is
            // never filtered; the choke point leaves it counting at every depth but Structure.
            var kindReason =
                $"Datatype '{key}' is a {leftKind.ToLowerInvariant()} entry in {_leftLabel} and a " +
                $"{rightKind.ToLowerInvariant()} entry in {_rightLabel}; it is reported as a removal plus an addition.";

            node.Properties.Add(new PropertyDiff(
                "Datatype kind",
                leftKind,
                rightKind,
                CountsAsDifference(true, kindReason, "Datatype kind", node.Category),
                kindReason));
        }

        private void FillBasic(DiffNode node, BasicDataType? l, BasicDataType? r)
        {
            AddProperty(node, "Size", l?.Size, r?.Size);
            AddProperty(node, "Interpretation", l?.Interpretation, r?.Interpretation);
            AddProperty(node, "Endian", l?.Endian, r?.Endian);
            AddProperty(node, "Encoding", l?.Encoding, r?.Encoding);
            AddCommonProse(node, l, r);
        }

        private void FillSimple(DiffNode node, SimpleDataType? l, SimpleDataType? r)
        {
            AddProperty(node, "Representation", l?.Representation, r?.Representation);
            AddProperty(node, "Units", l?.Units, r?.Units);
            AddProperty(node, "Resolution", l?.Resolution, r?.Resolution);
            AddProperty(node, "Accuracy", l?.Accuracy, r?.Accuracy);
            AddCommonProse(node, l, r);
        }

        private void FillEnumerated(DiffNode node, EnumeratedDataType? l, EnumeratedDataType? r)
        {
            AddProperty(node, "Representation", l?.Representation, r?.Representation);
            AddCommonProse(node, l, r);

            AddMembers(node, Items(l?.Enumerators), Items(r?.Enumerators), (member, ml, mr) =>
            {
                AddProperty(member, "Values", ml?.Values, mr?.Values);
                AddCommonProse(member, ml, mr);
            });
        }

        private void FillArray(DiffNode node, ArrayDataType? l, ArrayDataType? r)
        {
            AddProperty(node, "DataType", l?.DataType, r?.DataType);
            AddProperty(node, "Cardinality", l?.Cardinality, r?.Cardinality);
            AddProperty(node, "Encoding", l?.Encoding, r?.Encoding);
            AddCommonProse(node, l, r);
        }

        private void FillFixedRecord(DiffNode node, FixedRecordDataType? l, FixedRecordDataType? r)
        {
            AddProperty(node, "Encoding", l?.Encoding, r?.Encoding);
            AddProperty(node, "Include", l?.Include, r?.Include);
            AddCommonProse(node, l, r);

            AddMembers(node, Items(l?.Fields), Items(r?.Fields), (member, ml, mr) =>
            {
                AddProperty(member, "DataType", ml?.DataType, mr?.DataType);
                AddCommonProse(member, ml, mr);
            });
        }

        private void FillVariantRecord(DiffNode node, VariantRecordDataType? l, VariantRecordDataType? r)
        {
            AddProperty(node, "Discriminant", l?.Discriminant, r?.Discriminant);
            AddProperty(node, "DataType", l?.DataType, r?.DataType);
            AddProperty(node, "Encoding", l?.Encoding, r?.Encoding);
            AddCommonProse(node, l, r);

            AddMembers(node, Items(l?.Alternatives), Items(r?.Alternatives), (member, ml, mr) =>
            {
                AddProperty(member, "Enumerator", ml?.Enumerator, mr?.Enumerator);
                AddProperty(member, "DataType", ml?.DataType, mr?.DataType);
                AddCommonProse(member, ml, mr);
            });
        }

        /// <summary>Adds the enumerator / field / alternative children of a datatype.</summary>
        private void AddMembers<T>(DiffNode owner, IReadOnlyList<T> left, IReadOnlyList<T> right, Action<DiffNode, T?, T?> fill)
            where T : FomNode
        {
            foreach (var (l, r) in MatchLists(left, right, MemberKey, RawName))
            {
                var source = (l ?? r)!;
                var node = NewNode(source.Name, $"{owner.Path}/{MemberKey(source)}", DiffCategory.DataTypeMember, l, r);
                fill(node, l, r);
                owner.Children.Add(node);
            }
        }

        // ---------------------------------------------------------------- simple tables

        private DiffNode? BuildDimensions()
        {
            if (_o.IgnoreDimensions) return null;

            return BuildTable("Dimensions", KeyDimensions, DiffCategory.Dimension, _left.Dimensions, _right.Dimensions,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "DataType", l?.DataType, r?.DataType, Reason1516Only());
                    AddProperty(node, "UpperBound", l?.UpperBound, r?.UpperBound, Reason1516Only());
                    AddProperty(node, "Normalization", l?.Normalization, r?.Normalization, Reason1516Only());
                    AddProperty(node, "Value", l?.Value, r?.Value, Reason1516Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
                });
        }

        private DiffNode? BuildRoutingSpaces()
        {
            if (_o.IgnoreDimensions) return null;

            return BuildTable("Routing spaces", KeySpaces, DiffCategory.RoutingSpace, _left.RoutingSpaces, _right.RoutingSpaces,
                onlyIn1516: false,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "Dimensions", JoinList(l?.Dimensions), JoinList(r?.Dimensions), Reason13Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics);
                });
        }

        private DiffNode? BuildTransportations() =>
            BuildTable("Transportations", KeyTransportations, DiffCategory.Transportation, _left.Transportations, _right.Transportations,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "Reliable", l?.Reliable, r?.Reliable, Reason1516Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
                });

        private DiffNode? BuildSynchronizations() =>
            BuildTable("Synchronizations", KeySynchronizations, DiffCategory.Synchronization, _left.Synchronizations, _right.Synchronizations,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "Capability", l?.Capability, r?.Capability, Reason1516Only());
                    AddProperty(node, "DataType", l?.DataType, r?.DataType, Reason1516Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
                });

        private DiffNode? BuildUpdateRates() =>
            BuildTable("Update rates", KeyUpdateRates, DiffCategory.UpdateRate, _left.UpdateRates, _right.UpdateRates,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "Rate", l?.Rate, r?.Rate, Reason1516Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
                });

        private DiffNode? BuildSwitches() =>
            BuildTable("Switches", KeySwitches, DiffCategory.Switch, _left.Switches, _right.Switches,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "IsEnabled", l?.IsEnabled, r?.IsEnabled, Reason1516Only());
                    AddProperty(node, "ResignSwitch", l?.ResignSwitch, r?.ResignSwitch, Reason1516Only());
                });

        private DiffNode? BuildTags() =>
            BuildTable("Tags", KeyTags, DiffCategory.Tag, _left.Tags, _right.Tags,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "DataType", l?.DataType, r?.DataType, Reason1516Only());
                    if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
                });

        private DiffNode? BuildNotes() =>
            BuildTable("Notes", KeyNotes, DiffCategory.Note, _left.Notes, _right.Notes,
                onlyIn1516: true,
                fill: (node, l, r) =>
                {
                    AddProperty(node, "Label", l?.Label, r?.Label, Reason1516Only());
                    AddProperty(node, "Text", l?.Text, r?.Text, Reason1516Only());
                });

        private DiffNode? BuildTime()
        {
            var l = _left.Time;
            var r = _right.Time;
            var leftEmpty = l is null || l.IsEmpty;
            var rightEmpty = r is null || r.IsEmpty;
            if (leftEmpty && rightEmpty) return null;
            if (SkipInexpressibleTable(leftEmpty, rightEmpty, onlyIn1516: true)) return null;

            var section = Section("Time representation", KeyTime, DiffCategory.Section);
            var reason = TableReason(leftEmpty, rightEmpty, onlyIn1516: true);
            AddTableProperty(section, reason, leftEmpty ? 0 : 1, rightEmpty ? 0 : 1);

            var node = new DiffNode
            {
                Name = "Time representation",
                Path = UniquePath($"{KeyTime}/timeRepresentation"),
                Category = DiffCategory.Time,
                LeftName = leftEmpty ? null : "Time representation",
                RightName = rightEmpty ? null : "Time representation",
                Kind = leftEmpty ? DiffKind.Added : rightEmpty ? DiffKind.Removed : DiffKind.Unchanged,
            };

            if (reason is not null && (leftEmpty || rightEmpty))
            {
                node.Properties.Add(new PropertyDiff(
                    "Defined", leftEmpty ? "no" : "yes", rightEmpty ? "no" : "yes",
                    CountsAsDifference(true, reason, "Defined", node.Category), reason));
            }

            AddProperty(node, "TimeStamp datatype", l?.TimeStampDataType, r?.TimeStampDataType, Reason1516Only());
            if (!_o.IgnoreSemantics)
                AddProperty(node, "TimeStamp semantics", l?.TimeStampSemantics, r?.TimeStampSemantics, Reason1516Only());
            AddProperty(node, "Lookahead datatype", l?.LookaheadDataType, r?.LookaheadDataType, Reason1516Only());
            if (!_o.IgnoreSemantics)
                AddProperty(node, "Lookahead semantics", l?.LookaheadSemantics, r?.LookaheadSemantics, Reason1516Only());

            section.Children.Add(node);
            return section;
        }

        /// <summary>Builds one flat OMT table section: match by name, then fill each node's properties.</summary>
        /// <param name="onlyIn1516">
        /// True for a table HLA 1.3 has no equivalent of, false for a 1.3-only table (routing spaces).
        /// Drives the "not expressible" reason when the table is absent on one side of a
        /// cross-standard comparison.
        /// </param>
        private DiffNode? BuildTable<T>(
            string title,
            string sectionKey,
            DiffCategory category,
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            bool onlyIn1516,
            Action<DiffNode, T?, T?> fill)
            where T : FomNode
        {
            if (left.Count == 0 && right.Count == 0) return null;
            if (SkipInexpressibleTable(left.Count == 0, right.Count == 0, onlyIn1516)) return null;

            var section = Section(title, sectionKey, DiffCategory.Section);
            var reason = TableReason(left.Count == 0, right.Count == 0, onlyIn1516);
            AddTableProperty(section, reason, left.Count, right.Count);

            foreach (var (l, r) in MatchLists(left, right, MemberKey, RawName))
            {
                var source = (l ?? r)!;
                var node = NewNode(source.Name, $"{sectionKey}/{MemberKey(source)}", category, l, r);
                AddPresenceProperty(node, l, r, reason);
                fill(node, l, r);
                section.Children.Add(node);
            }

            return section;
        }

        // ---------------------------------------------------------------- cross-standard reasons

        /// <summary>The reason to stamp on a value HLA 1.3 has no way of writing down.</summary>
        private string? Reason1516Only() => _cross13 ? NotIn13 : null;

        /// <summary>The reason to stamp on a value IEEE 1516 replaced with dimensions.</summary>
        private string? Reason13Only() => _cross13 ? NotIn1516 : null;

        /// <summary>
        /// Keeps a structural reason only when it actually explains the difference — that is, when
        /// the side that cannot express the concept is the empty one.
        /// </summary>
        private string? ApplyReason(string? candidate, string? leftValue, string? rightValue)
        {
            if (candidate is null) return null;

            if (candidate == NotIn13)
            {
                var thirteenValue = _leftIs13 ? leftValue : rightValue;
                return string.IsNullOrWhiteSpace(thirteenValue) ? candidate : null;
            }

            var sixteenValue = _leftIs13 ? rightValue : leftValue;
            return string.IsNullOrWhiteSpace(sixteenValue) ? candidate : null;
        }

        /// <summary>
        /// The reason for a whole table being present on one side only because of the standard,
        /// or null when the emptiness has nothing to do with the standards in play.
        /// </summary>
        private string? TableReason(bool leftEmpty, bool rightEmpty, bool onlyIn1516)
        {
            if (!_cross13) return null;
            if (leftEmpty == rightEmpty) return null;

            if (onlyIn1516)
            {
                var thirteenEmpty = _leftIs13 ? leftEmpty : rightEmpty;
                return thirteenEmpty ? NotIn13 : null;
            }

            var sixteenEmpty = _leftIs13 ? rightEmpty : leftEmpty;
            return sixteenEmpty ? NotIn1516 : null;
        }

        /// <summary>Records, on the section itself, that the whole table is missing on one side.</summary>
        private void AddTableProperty(DiffNode section, string? reason, int leftCount, int rightCount)
        {
            if (reason is null) return;

            section.Properties.Add(new PropertyDiff(
                "Table", DescribeCount(leftCount), DescribeCount(rightCount),
                CountsAsDifference(true, reason, "Table", section.Category), reason));
        }

        /// <summary>Marks a one-sided child of such a table with the same structural reason.</summary>
        private void AddPresenceProperty<T>(DiffNode node, T? l, T? r, string? reason) where T : class
        {
            if (reason is null) return;
            if (l is not null && r is not null) return;

            node.Properties.Add(new PropertyDiff(
                "Defined", l is null ? "no" : "yes", r is null ? "no" : "yes",
                CountsAsDifference(true, reason, "Defined", node.Category), reason));
        }

        // ---------------------------------------------------------------- format-gap filtering

        /// <summary>
        /// True for the two reasons that mean "the other standard has no way of writing this down".
        /// Other reasons — the datatype-kind explanation, for instance — describe a difference
        /// somebody authored and are never filtered.
        /// </summary>
        private static bool IsFormatGap(string? reason) => reason is NotIn13 or NotIn1516;

        /// <summary>True when a reason is a format gap this run has been asked to stop counting.</summary>
        private bool IsFilteredGap(string? reason) =>
            _o.IgnoreInexpressibleProperties && IsFormatGap(reason);

        /// <summary>
        /// Whether a row that differs is allowed to count. Under
        /// <see cref="ComparisonOptions.IgnoreInexpressibleProperties"/> a format gap keeps its
        /// values and its reason — the user must still see why a cell is blank — but is reported as
        /// equal, so <see cref="DiffNode.Recount"/> collapses the nodes whose only change it was.
        /// <see cref="ComparisonOptions.Depth"/> composes with that filter here, and for the same
        /// reason: a row the current depth does not inspect is still recorded with both values, it
        /// simply stops counting.
        /// </summary>
        /// <remarks>
        /// This is the single place a property row is allowed to become a difference. Everything —
        /// ordinary properties, the folded transportation/order rows, the "Table" and "Defined"
        /// presence rows and the datatype-kind explanation — goes through it, so depth, format-gap
        /// filtering and the tally behind the depth advisory can never drift apart.
        /// </remarks>
        /// <param name="property">The row's display name, e.g. <c>DataType</c> or <c>Sharing</c>.</param>
        /// <param name="category">The category of the node the row belongs to.</param>
        private bool CountsAsDifference(bool different, string? reason, string property, DiffCategory category)
        {
            if (!different || IsFilteredGap(reason)) return false;

            if (CountsAtDepth(property, category)) return true;

            _depthFilteredProperties++;
            return false;
        }

        /// <summary>
        /// Whether the requested <see cref="ComparisonOptions.Depth"/> inspects this property at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Depth governs how much of a MATCHED element is looked at, never which elements are
        /// matched, so this is only ever consulted for property rows. Additions and removals are
        /// decided by <see cref="DiffNode.Kind"/> and are reported in full at every depth.
        /// </para>
        /// <para>
        /// At <see cref="ComparisonDepth.DataTypes"/> a row counts when it is the datatype of an
        /// attribute or of a parameter, or when it belongs to a datatype definition or one of its
        /// members — a record's fields, an enumeration's enumerators, a representation, an encoding
        /// and a cardinality are what the datatype IS, so a change to any of them is a real change
        /// to the type. Everything else — sharing, ownership, update type and condition,
        /// transportation, order, dimensions, routing space, units, resolution, accuracy, prose and
        /// the whole modelIdentification header — is recorded but does not count.
        /// </para>
        /// <para>
        /// The cross-standard bookkeeping rows ("Table", "Defined", "Datatype kind") are judged by
        /// the category of the node they sit on, which is the conservative reading: they hang off
        /// elements that are already reported as added or removed in their own right, so silencing
        /// them below <see cref="ComparisonDepth.Full"/> can never hide an element, only stop the
        /// same fact from being counted a second time as a property.
        /// </para>
        /// </remarks>
        private bool CountsAtDepth(string property, DiffCategory category) => _o.Depth switch
        {
            // Every property the OMT defines: the historical behaviour, unchanged.
            ComparisonDepth.Full => true,

            // Presence only. No property row is a difference at this depth.
            ComparisonDepth.Structure => false,

            _ => category is DiffCategory.DataType or DiffCategory.DataTypeGroup or DiffCategory.DataTypeMember
                 || (category is DiffCategory.Attribute or DiffCategory.Parameter
                     && string.Equals(property, "DataType", StringComparison.Ordinal)),
        };

        /// <summary>
        /// True when a whole table is absent on one side purely because that standard has no such
        /// table and the caller asked to compare only what both standards can express. The section
        /// is then left out altogether, exactly as if the matching Ignore* option had been set,
        /// rather than reported as a pile of one-sided rows.
        /// </summary>
        private bool SkipInexpressibleTable(bool leftEmpty, bool rightEmpty, bool onlyIn1516) =>
            IsFilteredGap(TableReason(leftEmpty, rightEmpty, onlyIn1516));

        private static string DescribeCount(int count) => count switch
        {
            0 => "absent",
            1 => "1 entry",
            _ => $"{count} entries",
        };

        // ---------------------------------------------------------------- properties

        /// <summary>
        /// Records one compared property, whether or not it differs, so the UI can show the whole
        /// side-by-side row set. Prose is trimmed (and optionally whitespace-collapsed) first.
        /// </summary>
        private void AddProperty(DiffNode node, string property, string? left, string? right, string? reason = null)
        {
            var leftText = OmtNormalizer.NormalizeText(left, _o);
            var rightText = OmtNormalizer.NormalizeText(right, _o);
            var different = !TextEquals(leftText, rightText);

            if (!different) NoteValueNormalisation(left, right);

            // The reason is worked out from the raw comparison, then decides whether the row counts:
            // filtering must not make the explanation disappear along with the difference.
            var applied = different ? ApplyReason(reason, leftText, rightText) : null;

            node.Properties.Add(new PropertyDiff(
                property, leftText, rightText,
                CountsAsDifference(different, applied, property, node.Category), applied));
        }

        /// <summary>Compares transportation on meaning while still showing the token as written.</summary>
        private void AddTransportation(DiffNode node, string? left, string? right) =>
            AddFoldedProperty(node, "Transportation", left, right, v => OmtNormalizer.NormalizeTransportation(v, _o));

        /// <summary>Compares ordering on meaning while still showing the token as written.</summary>
        private void AddOrder(DiffNode node, string? left, string? right) =>
            AddFoldedProperty(node, "Order", left, right, v => OmtNormalizer.NormalizeOrder(v, _o));

        private void AddFoldedProperty(DiffNode node, string property, string? left, string? right, Func<string?, string?> fold)
        {
            var different = !TextEquals(
                OmtNormalizer.NormalizeText(fold(left), _o),
                OmtNormalizer.NormalizeText(fold(right), _o));

            if (!different && _o.NormalizeTransportAndOrder && !OrdinalEquals(left, right))
                _transportOrderHelped = true;

            // A folded row never carries a reason — both standards can express transportation and
            // order — but it still goes through the choke point so depth applies to it too.
            node.Properties.Add(new PropertyDiff(
                property, OmtNormalizer.NormalizeText(left, _o), OmtNormalizer.NormalizeText(right, _o),
                CountsAsDifference(different, null, property, node.Category)));
        }

        /// <summary>Adds the semantics and note-reference rows shared by most OMT elements.</summary>
        private void AddCommonProse(DiffNode node, FomNode? l, FomNode? r)
        {
            if (!_o.IgnoreSemantics) AddProperty(node, "Semantics", l?.Semantics, r?.Semantics, Reason1516Only());
            if (!_o.IgnoreNotes) AddProperty(node, "Notes", l?.Notes, r?.Notes, Reason1516Only());
        }

        private bool TextEquals(string? left, string? right)
        {
            if (left is null && right is null) return true;
            if (left is null || right is null) return false;
            return _names.Equals(left, right);
        }

        private static bool OrdinalEquals(string? left, string? right) =>
            string.Equals(left, right, StringComparison.Ordinal);

        /// <summary>Remembers that a normalisation, rather than the files agreeing, produced a match.</summary>
        private void NoteValueNormalisation(string? rawLeft, string? rawRight)
        {
            if (rawLeft is null || rawRight is null) return;
            if (OrdinalEquals(rawLeft, rawRight)) return;

            if (string.Equals(rawLeft, rawRight, StringComparison.OrdinalIgnoreCase))
            {
                // Same text, different capitalisation: only IgnoreCase can have matched these.
                if (_o.IgnoreCase) _caseHelped = true;
            }
            else
            {
                // Anything else that still matched was folded by the whitespace pass.
                _whitespaceHelped = true;
            }
        }

        // ---------------------------------------------------------------- matching

        /// <summary>
        /// Pairs two lists by normalised key: left items keep their source order and carry their
        /// counterpart, then the unmatched right items follow in their own order. Duplicate keys in
        /// a malformed document are paired first-come, first-served instead of colliding.
        /// </summary>
        private List<(T? Left, T? Right)> MatchLists<T>(
            IReadOnlyList<T> left,
            IReadOnlyList<T> right,
            Func<T, string> keyOf,
            Func<T, string> rawKeyOf)
            where T : class
        {
            var pairs = new List<(T? Left, T? Right)>(left.Count + right.Count);
            if (left.Count == 0 && right.Count == 0) return pairs;

            var buckets = new Dictionary<string, Queue<int>>(_names);
            for (var i = 0; i < right.Count; i++)
            {
                var key = keyOf(right[i]);
                if (!buckets.TryGetValue(key, out var queue))
                {
                    queue = new Queue<int>();
                    buckets[key] = queue;
                }

                queue.Enqueue(i);
            }

            var consumed = new bool[right.Count];
            foreach (var item in left)
            {
                if (buckets.TryGetValue(keyOf(item), out var queue) && queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    consumed[index] = true;
                    NoteNameNormalisation(rawKeyOf(item), rawKeyOf(right[index]));
                    pairs.Add((item, right[index]));
                }
                else
                {
                    pairs.Add((item, null));
                }
            }

            for (var i = 0; i < right.Count; i++)
            {
                if (!consumed[i]) pairs.Add((null, right[i]));
            }

            return pairs;
        }

        /// <summary>Remembers that two differently spelled names were only matched thanks to an option.</summary>
        private void NoteNameNormalisation(string rawLeft, string rawRight)
        {
            if (OrdinalEquals(rawLeft, rawRight)) return;

            if (string.Equals(rawLeft, rawRight, StringComparison.OrdinalIgnoreCase))
            {
                if (_o.IgnoreCase) _caseHelped = true;
            }
            else if (_o.NormalizeRootNames)
            {
                _rootAliasHelped = true;
            }
        }

        private List<FomObjectClass> FilterObjectClasses(IEnumerable<FomObjectClass>? classes)
        {
            if (classes is null) return new List<FomObjectClass>();
            if (!_o.IgnoreManagementObjectModel) return classes.ToList();

            return classes.Where(c => !OmtNormalizer.IsManagementClass(RawClassKey(c))).ToList();
        }

        private List<FomInteractionClass> FilterInteractionClasses(IEnumerable<FomInteractionClass>? classes)
        {
            if (classes is null) return new List<FomInteractionClass>();
            if (!_o.IgnoreManagementObjectModel) return classes.ToList();

            return classes.Where(c => !OmtNormalizer.IsManagementClass(RawClassKey(c))).ToList();
        }

        /// <summary>The dotted name as written, falling back to the local name for a partial parse.</summary>
        private static string RawClassKey(FomNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.QualifiedName)) return node.QualifiedName;
            return string.IsNullOrWhiteSpace(node.Name) ? string.Empty : node.Name;
        }

        /// <summary>The local name as written, falling back to the qualified name when it is missing.</summary>
        private static string RawName(FomNode node) =>
            string.IsNullOrWhiteSpace(node.Name) ? RawClassKey(node) : node.Name;

        /// <summary>Normalised dotted name, used both as the match key and as the path segment.</summary>
        private string ClassKey(FomNode node) =>
            OmtNormalizer.NormalizeQualifiedName(RawClassKey(node), _o)?.Trim() ?? string.Empty;

        /// <summary>Normalised local name, used to match attributes, parameters and table rows.</summary>
        private string MemberKey(FomNode node) =>
            OmtNormalizer.NormalizeName(RawName(node), _o)?.Trim() ?? string.Empty;

        private static IReadOnlyList<T> Items<T>(IReadOnlyList<T>? list) => list ?? Array.Empty<T>();

        private static string? JoinList(IReadOnlyList<string>? values, string separator = ", ")
        {
            if (values is null || values.Count == 0) return null;
            return string.Join(separator, values);
        }

        // ---------------------------------------------------------------- node plumbing

        private DiffNode Section(string title, string sectionKey, DiffCategory category) => new()
        {
            Name = title,
            Path = UniquePath(sectionKey),
            Category = category,
            Kind = DiffKind.Unchanged,
            LeftName = title,
            RightName = title,
        };

        /// <summary>
        /// Creates a node for a matched or one-sided pair. Matched nodes start as
        /// <see cref="DiffKind.Unchanged"/>; <see cref="DiffNode.Recount"/> promotes them to
        /// <see cref="DiffKind.Modified"/> once the properties and children are in place.
        /// </summary>
        private DiffNode NewNode<T>(string name, string path, DiffCategory category, T? left, T? right)
            where T : FomNode
        {
            return new DiffNode
            {
                Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name,
                Path = UniquePath(path),
                Category = category,
                LeftName = left is null ? null : RawName(left),
                RightName = right is null ? null : RawName(right),
                Kind = left is null ? DiffKind.Added : right is null ? DiffKind.Removed : DiffKind.Unchanged,
            };
        }

        /// <summary>
        /// Guarantees the path really is unique, even when a document repeats a name or leaves one
        /// blank, so the UI can key its selection and expansion state off it.
        /// </summary>
        private string UniquePath(string path)
        {
            if (_paths.Add(path)) return path;

            for (var i = 2; ; i++)
            {
                var candidate = $"{path}#{i}";
                if (_paths.Add(candidate)) return candidate;
            }
        }

        // ---------------------------------------------------------------- advisories

        /// <summary>
        /// Walks the finished, counted tree once and tallies what the advisories need: how many
        /// property rows still differ, how many of those are format gaps, and — in filtered mode —
        /// how many rows were reported as equal because of one.
        /// </summary>
        private void MeasureFormatGaps(DiffNode root)
        {
            foreach (var property in root.DescendantsAndSelf().SelectMany(n => n.Properties))
            {
                if (property.IsDifferent) _differingProperties++;
                if (!IsFormatGap(property.Reason)) continue;

                // A format-gap row that stopped counting did so for one of two reasons, and only one
                // of them is what the "hidden" advisory talks about. Attributing a depth-filtered
                // row to IgnoreInexpressibleProperties would claim the user ticked a box they did
                // not tick; depth reports itself in its own advisory instead.
                if (property.IsDifferent) _countedGapProperties++;
                else if (_o.IgnoreInexpressibleProperties) _hiddenGapProperties++;

                if (property.Reason == NotIn13) _gapsLackedBy13++;
                else _gapsLackedBy1516++;
            }
        }

        /// <summary>The standard that is short of the concept behind most of the format gaps.</summary>
        private string LackingStandard()
        {
            var thirteen = _leftIs13 ? _left.StandardDisplayName : _right.StandardDisplayName;
            var sixteen = _leftIs13 ? _right.StandardDisplayName : _left.StandardDisplayName;
            return _gapsLackedBy13 >= _gapsLackedBy1516 ? thirteen : sixteen;
        }

        /// <summary>
        /// States, in one line, what a less-than-exhaustive comparison actually looked at and how
        /// much it set aside — using the tally taken while the tree was built, so the number is the
        /// number of rows really sitting in the finished tree with their values and a false
        /// <see cref="PropertyDiff.IsDifferent"/>. Nothing is said at
        /// <see cref="ComparisonDepth.Full"/>, where nothing is set aside.
        /// </summary>
        private void AddDepthAdvisory(ComparisonResult result)
        {
            if (_o.Depth == ComparisonDepth.Full) return;

            var scope = _o.Depth == ComparisonDepth.Structure
                ? "Compared element names only, so only additions and removals are counted."
                : "Compared element names and datatypes only.";

            if (_depthFilteredProperties == 0)
            {
                result.Advisories.Add(
                    $"{scope} No other property differed, so Full depth would report the same totals.");
                return;
            }

            var differences = _depthFilteredProperties == 1
                ? "1 further property difference was"
                : $"{_depthFilteredProperties} further property differences were";

            result.Advisories.Add(
                $"{scope} {differences} recorded but not counted; switch to Full depth to include them.");
        }

        private void BuildAdvisories(ComparisonResult result)
        {
            AddDepthAdvisory(result);

            if (_cross13)
            {
                result.Advisories.Add(
                    $"Comparing {_left.StandardDisplayName} against {_right.StandardDisplayName}. " +
                    "HLA 1.3 has no datatype, dimension or sharing model, so those appear as differences.");

                var thirteenSpaces = _leftIs13 ? _left.RoutingSpaces.Count : _right.RoutingSpaces.Count;
                if (thirteenSpaces > 0 && !_o.IgnoreDimensions)
                {
                    result.Advisories.Add(
                        "HLA 1.3 routing spaces have no IEEE 1516 counterpart; they are reported against the dimension table.");
                }

                // Real numbers off the finished tree, so the user can tell how much of the diff is
                // the two standards disagreeing rather than the two models.
                if (_countedGapProperties > 0)
                {
                    result.Advisories.Add(
                        $"{_countedGapProperties} of the {_differingProperties} property differences exist only " +
                        $"because {LackingStandard()} cannot express them. Tick 'Only compare what both standards " +
                        $"can express' to hide them and see the {_differingProperties - _countedGapProperties} " +
                        "authored differences.");
                }

                if (_hiddenGapProperties > 0)
                {
                    result.Advisories.Add(
                        $"{_hiddenGapProperties} property differences were hidden because {LackingStandard()} " +
                        "cannot express them; they are still shown on each row with a reason.");
                }
            }
            else if (_left.Standard != _right.Standard)
            {
                result.Advisories.Add(
                    $"Comparing {_left.StandardDisplayName} against {_right.StandardDisplayName}. " +
                    "Some differences may follow from the change of standard rather than from the model.");
            }

            if (_left.Standard == FomStandard.Unknown || _right.Standard == FomStandard.Unknown)
            {
                result.Advisories.Add(
                    "At least one document has an unrecognised HLA standard, so no cross-standard allowances were made.");
            }

            if (_rootAliasHelped)
                result.Advisories.Add("HLA 1.3 / 1516 root class names were treated as equivalent.");

            if (_caseHelped)
                result.Advisories.Add("Names were compared case-insensitively; some elements matched only because of that.");

            if (_transportOrderHelped)
                result.Advisories.Add("Transportation and order spellings were folded onto their 1516 form (reliable to HLAreliable, timestamp to TimeStamp).");

            if (_whitespaceHelped)
            {
                result.Advisories.Add(_o.NormalizeWhitespace
                    ? "Runs of whitespace inside prose were collapsed before comparing, so some values matched despite different layout."
                    : "Leading and trailing whitespace was trimmed before comparing, so some values matched despite different layout.");
            }

            if (_o.IgnoreManagementObjectModel)
                result.Advisories.Add("The management object model (HLAmanager / Manager) was excluded from both sides.");

            if (_o.IgnoreDataTypes)
                result.Advisories.Add("Datatype tables were excluded from the comparison.");

            if (_o.IgnoreDimensions)
                result.Advisories.Add("Dimension and routing space tables were excluded from the comparison.");

            if (_o.IgnoreIdentification)
                result.Advisories.Add("The model identification header was excluded from the comparison.");

            if (_o.IgnoreSemantics)
                result.Advisories.Add("Semantics prose was excluded from the comparison.");

            if (_o.IgnoreNotes)
                result.Advisories.Add("Note references on elements were excluded from the comparison.");

            if (!_o.KeepUnchanged)
                result.Advisories.Add("Unchanged elements were removed from the tree; the totals still include them.");

            if (_left.HasErrors)
                result.Advisories.Add($"{_leftLabel} was read with parse errors, so the comparison may be incomplete.");

            if (_right.HasErrors)
                result.Advisories.Add($"{_rightLabel} was read with parse errors, so the comparison may be incomplete.");
        }
    }
}
