using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// One entry in a class picker: an object class of one FOM, with the size of its effective
/// attribute set.
/// </summary>
/// <remarks>
/// The leaf name is held apart from the path it sits under because the picker is used by typing.
/// Every class in a FOM is named <c>ObjectRoot.something.something.Aircraft</c>, so a list showing
/// qualified names is a list of identical prefixes: filtering on the whole name would match every
/// class in the FOM on the first keystroke and never narrow. The leaf leads, and the path follows
/// it as quiet context for the two classes that share a leaf name.
/// </remarks>
public sealed class ObjectClassOption
{
    private ObjectClassOption(string qualifiedName, string leafName, string? path, int attributeCount)
    {
        QualifiedName = qualifiedName;
        LeafName = leafName;
        Path = path;
        AttributeCount = attributeCount;
    }

    /// <summary>The fully qualified dotted name.</summary>
    public string QualifiedName { get; }

    /// <summary>The segment after the last dot — "Aircraft".</summary>
    public string LeafName { get; }

    /// <summary>Everything before the last dot, or null when there is nothing above the leaf.</summary>
    public string? Path { get; }

    /// <summary>
    /// How many attributes the class effectively carries — declared plus everything inherited.
    /// </summary>
    /// <remarks>
    /// Counted by the mapper, so it is exactly the number of rows picking this class produces. RPR's
    /// <c>Aircraft</c> declares zero attributes and inherits forty-five; a declared count would
    /// advertise it as empty.
    /// </remarks>
    public int AttributeCount { get; }

    /// <summary>Splits a qualified class name into the leaf the user types and the path behind it.</summary>
    public static ObjectClassOption ForClass(ObjectClassSummary summary)
    {
        var name = summary.QualifiedName;
        var lastDot = name.LastIndexOf('.');

        // A trailing dot or a bare root name leaves nothing to split, and a leaf is required — a
        // blank entry would be unreachable by typing and unreadable in the list.
        var leaf = lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
        var path = lastDot > 0 && lastDot < name.Length - 1 ? name[..lastDot] : null;

        return new ObjectClassOption(name, leaf, path, summary.AttributeCount);
    }

    /// <summary>
    /// True when the typed text appears anywhere in the leaf name or the path.
    /// </summary>
    /// <remarks>
    /// Substring rather than prefix, which is the whole point of filtering the list instead of
    /// leaning on WPF's built-in type-to-select: on a real FOM the class somebody wants is
    /// <c>ObjectRoot.BaseEntity.PhysicalEntity.Platform.Aircraft</c>, and typing "air" has to reach
    /// it. The path is searchable too, so "Platform" gathers the branch.
    /// </remarks>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;

        var needle = filter.Trim();

        return LeafName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (Path?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>What the collapsed picker shows once a class is chosen.</summary>
    public override string ToString() => LeafName;
}

/// <summary>
/// The "Attribute data" tab of the Compare screen: one class chosen in each FOM, and one flat row
/// per attribute they carry, with the datatype on each side beside it.
/// </summary>
/// <remarks>
/// <para>
/// This screen answers a narrower question than the difference tree — "what data changed, and how do
/// I remap it?" — so it shows only the two things that exist on the wire: which attributes a class
/// carries, and what each one is typed as. Sharing, ownership, update type, semantics and qualified
/// names are properties of the model rather than of the data, and are deliberately absent.
/// </para>
/// <para>
/// The two classes are chosen <b>independently</b>, and that is the point. Matching classes by name
/// answers "how do these two FOMs line up?", which the difference tree already does. The question
/// here is "if I move this class's data onto that one, what happens?" — and across a generational
/// step the counterpart is rarely the same name. RPR 2.0 reworks the hierarchy RPR 1.0 declared, so
/// deciding that its <c>Aircraft</c> is what the old class becomes is a judgement only the user can
/// make. The screen exists to let them make it and then read the consequences.
/// </para>
/// <para>
/// The rows come from <see cref="AttributeMapper.BuildForClasses"/>, which resolves each class to
/// its <b>effective</b> attribute set — declared plus everything inherited from its ancestors. A
/// class that declares nothing still publishes its ancestors' attributes, so the declared set would
/// report most of a deep FOM as empty.
/// </para>
/// <para>
/// Beside each datatype name is the encoding it resolves to through that FOM's own datatype tables.
/// Names are unreliable across a version step — RPR 2.0 renames nearly every type it inherits from
/// RPR 1.0 — so the encodings are what the two sides are actually judged on, and they are what turns
/// a wall of "datatype changed" into a short list of attributes that genuinely re-encode.
/// </para>
/// </remarks>
public sealed class AttributeMapViewModel : ViewModelBase
{
    private const string DefaultLeftLabel = "FOM A";
    private const string DefaultRightLabel = "FOM B";

    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;

    private long? _leftId;
    private long? _rightId;
    private string _leftLabel = DefaultLeftLabel;
    private string _rightLabel = DefaultRightLabel;

    // Both documents, held for the lifetime of the pair. Reading one out of SQLite is the expensive
    // part of this screen, and the user is about to compare many pairs of classes out of the same
    // two documents: reloading per pick would turn a few milliseconds of work into a few hundred.
    private FomDocument? _leftDocument;
    private FomDocument? _rightDocument;

    private AttributeDataMap? _map;
    private AttributeMapRow? _selectedRow;

    // Kept from the pair load so clicking an encoding cell answers instantly. A resolver holds only
    // the document's datatype tables, not its class tree, so this is a fraction of the cost of
    // reloading the document. See ShowEncoding.
    private DataTypeResolver? _leftResolver;
    private DataTypeResolver? _rightResolver;

    private ObjectClassOption? _selectedClassA;
    private ObjectClassOption? _selectedClassB;
    private string _classFilterA = "";
    private string _classFilterB = "";

    // Defaults OFF: the map is read as a whole worksheet — you look up a class pair and see
    // everything they carry, changed or not — rather than as a filtered to-do list.
    private bool _onlyDifferences;
    private string _searchText = "";

    // Every chip starts on, to agree with _onlyDifferences being off. Same and Renamed in
    // particular have to start together: one chip covers both statuses, so a pair that starts
    // apart puts the grid and the chip at odds before the user has touched either.
    private bool _showSame = true;
    private bool _showChanged = true;
    private bool _showRenamed = true;
    private bool _showMoved = true;
    private bool _showOnlyLeft = true;
    private bool _showOnlyRight = true;

    private bool _isActive;
    private bool _isStale = true;

    // Bumped by each pair load and re-checked when that load's worker returns. See LoadPairAsync.
    private int _generation;

    // The same guard for comparisons, which are far more frequent: one per keystroke-free pick.
    private int _compareGeneration;

    // Comparisons are chained rather than run concurrently. Arrowing down a picker fires one per
    // class, and serialising them means at most one repaint lands: every superseded compare in the
    // queue fails its generation check the moment it is reached and costs nothing.
    private Task _compareChain = Task.CompletedTask;

    /// <summary>Creates the screen. Nothing is read until a pair is set and the tab is shown.</summary>
    /// <param name="repository">Store both FOM documents are rebuilt from.</param>
    /// <param name="dialogs">Used for the save dialog and for surfacing repository failures.</param>
    public AttributeMapViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        // One view per side, and never the default view of the collection: a default view is shared
        // by everything that binds the same collection, so B's filter would narrow A's picker.
        ClassesA = new ListCollectionView(ClassOptionsA) { Filter = PassesA };
        ClassesB = new ListCollectionView(ClassOptionsB) { Filter = PassesB };

        RefreshCommand = new RelayCommand(Refresh);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        ClearClassACommand = new RelayCommand(() => SelectedClassA = null, () => SelectedClassA is not null);
        ClearClassBCommand = new RelayCommand(() => SelectedClassB = null, () => SelectedClassB is not null);

        ExportCommand = new RelayCommand(ExportWorkbook, () => Map is not null && Rows.Count > 0);

        ShowLeftDataTypeCommand = new RelayCommand<AttributeMapRow>(
            row => ShowDataType(row, left: true), row => CanShowDataType(row, left: true));

        ShowRightDataTypeCommand = new RelayCommand<AttributeMapRow>(
            row => ShowDataType(row, left: false), row => CanShowDataType(row, left: false));
    }

    /// <summary>The rows that survive the current filters, in the order the mapper produced them.</summary>
    public ObservableCollection<AttributeMapRow> Rows { get; } = new();

    /// <summary>
    /// Every object class of FOM A, in that document's own tree order.
    /// </summary>
    /// <remarks>
    /// Deliberately left in the document's own order rather than sorted. Root first and then
    /// depth-first through the children is the order the FOM itself is written in and the order
    /// somebody who knows it expects to scroll through; sorting alphabetically would put
    /// <c>Aircraft</c> beside <c>AmphibiousVehicle</c> and tear the hierarchy apart. Typing into the
    /// picker is what finds one class quickly, not the ordering.
    /// </remarks>
    public ObservableCollection<ObjectClassOption> ClassOptionsA { get; } = new();

    /// <summary>Every object class of FOM B; see <see cref="ClassOptionsA"/>.</summary>
    public ObservableCollection<ObjectClassOption> ClassOptionsB { get; } = new();

    /// <summary>
    /// What FOM A's picker actually shows: <see cref="ClassOptionsA"/> narrowed by what the user has
    /// typed into it.
    /// </summary>
    /// <remarks>
    /// A filtered view rather than a rebuilt list, and the difference matters. Rebuilding empties the
    /// collection for an instant, and WPF's Selector drops <c>SelectedItem</c> the moment the
    /// selected item leaves the items collection — which makes the ComboBox rewrite its own text box
    /// from a now-null selection and wipe the half-typed word out from under the user. A view's
    /// predicate is under our control instead, and it always admits the current selection, so the
    /// selection can never leave and the text is never rewritten.
    /// </remarks>
    public ICollectionView ClassesA { get; }

    /// <summary>What FOM B's picker shows; see <see cref="ClassesA"/>.</summary>
    public ICollectionView ClassesB { get; }

    /// <summary>Fidelity notes from the mapper, e.g. one side being a FED with no datatype table.</summary>
    public ObservableCollection<string> Advisories { get; } = new();

    /// <summary>
    /// How the map is built. A separate instance from the difference tree's options on purpose: the
    /// tree's depth and property switches say nothing about attribute data, and a user narrowing the
    /// tree should not silently change the remap worksheet under it.
    /// </summary>
    public ComparisonOptions Options { get; } = new();

    /// <summary>Re-reads both FOMs and rebuilds the two class lists.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Empties <see cref="SearchText"/>.</summary>
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Unpicks FOM A's class. Disabled while nothing is picked there.</summary>
    public RelayCommand ClearClassACommand { get; }

    /// <summary>Unpicks FOM B's class.</summary>
    public RelayCommand ClearClassBCommand { get; }

    /// <summary>Writes the visible rows out as the leveled remap worksheet.</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>Opens the datatype inspector for a row's FOM A datatype.</summary>
    public RelayCommand<AttributeMapRow> ShowLeftDataTypeCommand { get; }

    /// <summary>Opens the datatype inspector for a row's FOM B datatype.</summary>
    public RelayCommand<AttributeMapRow> ShowRightDataTypeCommand { get; }

    /// <summary>
    /// The work currently in flight, or a completed task when the screen is idle.
    /// </summary>
    /// <remarks>
    /// The entry points below are property setters and void callbacks, so they cannot hand their
    /// task back to whoever triggered them. Parking it here costs nothing and means the work is
    /// observable rather than merely discarded: a caller that must not race it can wait on this
    /// instead of guessing at a delay.
    /// </remarks>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>The current comparison; null until a class has been picked on at least one side.</summary>
    public AttributeDataMap? Map
    {
        get => _map;
        private set
        {
            if (!SetProperty(ref _map, value)) return;
            OnPropertyChanged(nameof(Summary), nameof(SameCount), nameof(ChangedCount),
                nameof(RenamedCount), nameof(SameOrRenamedCount), nameof(MovedCount), nameof(OnlyLeftCount),
                nameof(OnlyRightCount), nameof(ComparesBothSides));
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The highlighted row. Kept across a filter change whenever it is still visible.</summary>
    public AttributeMapRow? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    /// <summary>
    /// The class chosen in FOM A, or null while none is. This and its B counterpart are the screen's
    /// primary controls.
    /// </summary>
    /// <remarks>
    /// Setting either side compares immediately. On the user's real pair each FOM holds a couple of
    /// hundred classes and the question is always about one of them, so choosing is the whole
    /// interaction; there is deliberately no Compare button here to press afterwards.
    /// </remarks>
    public ObjectClassOption? SelectedClassA
    {
        get => _selectedClassA;
        set
        {
            if (!SetProperty(ref _selectedClassA, value)) return;

            // The predicate admits the current selection, so the view has to be told the selection
            // moved or the previously chosen class stays pinned into a filtered list.
            ClassesA.Refresh();

            OnPropertyChanged(nameof(HasClassA), nameof(Summary));
            ClearClassACommand.RaiseCanExecuteChanged();
            ScheduleCompare();
        }
    }

    /// <summary>The class chosen in FOM B; see <see cref="SelectedClassA"/>.</summary>
    public ObjectClassOption? SelectedClassB
    {
        get => _selectedClassB;
        set
        {
            if (!SetProperty(ref _selectedClassB, value)) return;

            ClassesB.Refresh();

            OnPropertyChanged(nameof(HasClassB), nameof(Summary));
            ClearClassBCommand.RaiseCanExecuteChanged();
            ScheduleCompare();
        }
    }

    /// <summary>What the user has typed into FOM A's picker. Narrows the list; never picks anything.</summary>
    /// <remarks>
    /// Deliberately <b>not</b> bound to the ComboBox's Text. That property is written by the control
    /// itself as well as by the user — it echoes the chosen class's name on every selection, and an
    /// editable ComboBox commits a selection on each arrow key while its list is open. Bound, those
    /// echoes arrive here as filters, and a user who typed "airc" to narrow two hundred classes to
    /// three would see the list snap back to all two hundred on the first press of Down. So the view
    /// pushes here only from real keystrokes; see AttributeMapView.xaml.cs.
    ///
    /// Typing must not move the selection either: a filter that selected its first match would fire
    /// a comparison on every keystroke.
    /// </remarks>
    public string ClassFilterA
    {
        get => _classFilterA;
        set
        {
            if (!SetProperty(ref _classFilterA, value ?? "")) return;
            ClassesA.Refresh();
        }
    }

    /// <summary>What the user has typed into FOM B's picker; see <see cref="ClassFilterA"/>.</summary>
    public string ClassFilterB
    {
        get => _classFilterB;
        set
        {
            if (!SetProperty(ref _classFilterB, value ?? "")) return;
            ClassesB.Refresh();
        }
    }

    /// <summary>True once a class is chosen in FOM A.</summary>
    public bool HasClassA => _selectedClassA is not null;

    /// <summary>True once a class is chosen in FOM B.</summary>
    public bool HasClassB => _selectedClassB is not null;

    /// <summary>True when both sides have a class, so the rows are a real comparison.</summary>
    public bool ComparesBothSides => Map?.ComparesBothSides ?? false;

    /// <summary>
    /// Hides every row that needs no decision — attributes that line up with the same datatype, and
    /// attributes whose datatype was only renamed.
    /// </summary>
    /// <remarks>
    /// A rename is excluded here for the same reason a match is. On the user's RPR 1.0 to RPR 2.0
    /// pair, 614 rows report a different datatype name and all but a few dozen are pure renames —
    /// <c>unsigned long</c> to <c>UnsignedInteger32</c> and the like, the same bits on the wire. They
    /// are not work, and leaving them in this view buries the rows that are.
    /// </remarks>
    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (!SetProperty(ref _onlyDifferences, value)) return;

            // This checkbox and the "Same" chip are two controls over one idea, so the fields are
            // set directly rather than through the sibling property — that notifies without
            // re-filtering twice or bouncing back into this setter. Both statuses move together for
            // the reason the chip's own setter moves them: the chip is one control over the pair, and
            // leaving Renamed behind would show rows the chip reports as hidden.
            SetProperty(ref _showSame, !value, nameof(ShowSame));
            SetProperty(ref _showRenamed, !value, nameof(ShowRenamed));
            ApplyFilter();
        }
    }

    /// <summary>Free-text filter over the attribute name and either datatype.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Shows the rows that need no work: identical datatypes, and datatypes that were only renamed.
    /// One control for both, because the screen presents them as one thing.
    /// </summary>
    public bool ShowSame
    {
        get => _showSame;
        set
        {
            if (_showSame == value) return;
            _showSame = value;
            _showRenamed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRenamed));
            ApplyFilter();
        }
    }

    /// <summary>Everything that needs no conversion — the count behind the single "Same" chip.</summary>
    public int SameOrRenamedCount => SameCount + RenamedCount;

    /// <summary>Show attributes present on both sides but typed differently.</summary>
    public bool ShowChanged
    {
        get => _showChanged;
        set
        {
            if (!SetProperty(ref _showChanged, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Show attributes whose datatype name changed but whose encoding did not.
    /// </summary>
    /// <remarks>
    /// Defaults to true so that widening the view shows renames alongside everything else. It has no
    /// effect while <see cref="OnlyDifferences"/> is set, which filters renames out ahead of it — a
    /// rename needs no attention.
    /// </remarks>
    public bool ShowRenamed
    {
        get => _showRenamed;
        set
        {
            if (!SetProperty(ref _showRenamed, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Show attributes that only moved to another class in the hierarchy.
    /// </summary>
    /// <remarks>
    /// Only ever populated when both sides picked the same class. Across two classes the user paired
    /// by hand, a different declaring ancestor is the pairing rather than a finding, and the mapper
    /// does not report one.
    /// </remarks>
    public bool ShowMoved
    {
        get => _showMoved;
        set
        {
            if (!SetProperty(ref _showMoved, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Show attributes that exist in FOM A's class only.</summary>
    public bool ShowOnlyLeft
    {
        get => _showOnlyLeft;
        set
        {
            if (!SetProperty(ref _showOnlyLeft, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Show attributes that exist in FOM B's class only.</summary>
    public bool ShowOnlyRight
    {
        get => _showOnlyRight;
        set
        {
            if (!SetProperty(ref _showOnlyRight, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Display name of FOM A, or a placeholder before a pair is chosen.</summary>
    public string LeftLabel
    {
        get => _leftLabel;
        private set => SetProperty(ref _leftLabel, value);
    }

    /// <summary>Display name of FOM B, or a placeholder before a pair is chosen.</summary>
    public string RightLabel
    {
        get => _rightLabel;
        private set => SetProperty(ref _rightLabel, value);
    }

    /// <summary>Attributes that line up with the same datatype on both sides.</summary>
    public int SameCount => Map?.SameCount ?? 0;

    /// <summary>Attributes present on both sides but typed differently — the ones that need converting.</summary>
    public int ChangedCount => Map?.DataTypeChangedCount ?? 0;

    /// <summary>Attributes whose datatype was renamed without changing the encoding — no work.</summary>
    public int RenamedCount => Map?.RenamedCount ?? 0;

    /// <summary>Attributes declared on a different class, but still inherited and identically typed.</summary>
    public int MovedCount => Map?.MovedCount ?? 0;

    /// <summary>Attributes only FOM A's class has.</summary>
    public int OnlyLeftCount => Map?.OnlyInLeftCount ?? 0;

    /// <summary>Attributes only FOM B's class has.</summary>
    public int OnlyRightCount => Map?.OnlyInRightCount ?? 0;

    /// <summary>True once the parent has handed over both FOMs.</summary>
    public bool HasPair => _leftId is not null && _rightId is not null;

    /// <summary>True when the grid has at least one row after filtering.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True before this pair has been read — the pickers are empty because nothing ran yet.</summary>
    public bool IsAwaitingCompare => _isStale;

    /// <summary>
    /// Headline on the right of the filter strip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With both classes chosen it names the pairing and then the figures for it — "Aircraft →
    /// FixedWingAircraft · 45 attributes · 3 re-encode · 12 renamed only · 1 only in A". Re-encode
    /// leads because it is the number somebody has to act on; renames follow immediately and are
    /// worded "renamed only" so the two are never read as one figure, since counting a rename as
    /// work is what made the old headline useless on a large migration.
    /// </para>
    /// <para>
    /// With one class chosen it says so plainly and offers no figures at all. Half a pairing has been
    /// compared against nothing, and any number quoted there would be read as a result.
    /// </para>
    /// <para>
    /// Zero segments are dropped: most class pairs change in one way or none, and a line of zeroes
    /// hides the one number that is not.
    /// </para>
    /// </remarks>
    public string Summary
    {
        get
        {
            if (Map is not { } map) return "";

            var total = map.Rows.Count;

            if (!map.ComparesBothSides)
            {
                var side = map.LeftClassName is not null ? LeafOf(map.LeftClassName) : LeafOf(map.RightClassName);
                var waiting = map.LeftClassName is not null ? "nothing chosen in B" : "nothing chosen in A";

                return $"{side} · {total} attribute{Plural(total)} · {waiting}";
            }

            // Leaf names only: the paths are on screen in both pickers, and repeating
            // "ObjectRoot.BaseEntity.PhysicalEntity.Platform." twice here would push the counts —
            // the part being read — off the end of the strip.
            var parts = new List<string>
            {
                $"{LeafOf(map.LeftClassName)} → {LeafOf(map.RightClassName)}",
                $"{total} attribute{Plural(total)}",
            };

            Add(map.DataTypeChangedCount, "re-encode");
            Add(map.RenamedCount, "renamed only");
            Add(map.OnlyInLeftCount, "only in A");
            Add(map.OnlyInRightCount, "only in B");
            Add(map.MovedCount, "moved");

            return string.Join(" · ", parts);

            void Add(int count, string label)
            {
                if (count > 0) parts.Add($"{count} {label}");
            }
        }
    }

    /// <summary>Explains an empty grid — the reason differs and the user cannot be left guessing.</summary>
    public string EmptyMessage =>
        !HasPair ? "Choose FOM A and FOM B above to see which attributes carry data on each side."
        : IsAwaitingCompare ? "Press Compare to read both FOMs and list their object classes."
        : ClassOptionsA.Count == 0 && ClassOptionsB.Count == 0 ? "Neither FOM declares a single object class."
        : Map is null && (HasClassA || HasClassB) ? "The attribute map could not be built for these two classes."
        : !HasClassA && !HasClassB
            ? "Pick a class in each FOM above to compare the attribute data they carry. "
              + "The two do not have to be named the same."
        : Map is null ? "The attribute map could not be built for these two classes."
        : Map.Rows.Count == 0 ? "Neither class declares or inherits a single attribute."
        : "No attributes match the current filters.";

    /// <summary>
    /// True while the "Attribute data" tab is the visible one. Bound from the TabItem.
    /// </summary>
    /// <remarks>
    /// Selection only — showing this tab does not read anything. The Compare screen fills all three
    /// of its tabs in one pass, so that a single overlay covers the whole wait and every tab has its
    /// data by the time it lifts. A tab that quietly read itself on arrival made a partial load look
    /// like a complete one. See CompareViewModel.CompareAsync.
    /// </remarks>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Re-points the screen at a new pair of FOMs. Called by the Compare screen whenever its two
    /// pickers change, so every tab always describes the same pair.
    /// </summary>
    /// <param name="left">FOM A, or null when the picker is empty.</param>
    /// <param name="right">FOM B, or null when the picker is empty.</param>
    public void SetPair(FomRegistryEntry? left, FomRegistryEntry? right)
    {
        _leftId = left?.Id;
        _rightId = right?.Id;

        LeftLabel = string.IsNullOrWhiteSpace(left?.DisplayName) ? DefaultLeftLabel : left!.DisplayName;
        RightLabel = string.IsNullOrWhiteSpace(right?.DisplayName) ? DefaultRightLabel : right!.DisplayName;

        ClearPair();

        // Marked stale and left that way. Reading it costs two full document loads, and doing that
        // here would run once per picker — twice for a user setting both — for a result the next
        // Compare throws away. Compare is what fills this tab. See IsActive.
        _isStale = true;

        OnPropertyChanged(nameof(HasPair));
        RaiseRowState();
    }

    // ---- the datatype inspector ---------------------------------------------------------

    /// <summary>
    /// True when there is a datatype on that side to open. A row that is only in one class has
    /// nothing on the other, and a failed load has no resolver, so the cell stays plain text.
    /// </summary>
    private bool CanShowDataType(AttributeMapRow? row, bool left)
    {
        if (row is null) return false;

        var resolver = left ? _leftResolver : _rightResolver;
        var dataType = left ? row.LeftDataType : row.RightDataType;

        return resolver is not null && !string.IsNullOrWhiteSpace(dataType);
    }

    /// <summary>
    /// Opens the inspector on one side's datatype.
    /// </summary>
    /// <remarks>
    /// The encoding column answers whether two attributes move the same bytes, which is what the map
    /// is for. It cannot answer what the field may hold, because the canonical form deliberately
    /// drops everything that would say so — units, resolution, accuracy, enumerator labels, field
    /// names. This reads that half back out of the FOM, on demand, for the one datatype clicked.
    /// </remarks>
    private void ShowDataType(AttributeMapRow? row, bool left)
    {
        if (!CanShowDataType(row, left)) return;

        var resolver = (left ? _leftResolver : _rightResolver)!;
        var dataType = left ? row!.LeftDataType : row!.RightDataType;

        try
        {
            var detail = resolver.Explain(dataType);

            _dialogs.ShowDataTypeDetail(new DataTypeDetailViewModel(
                detail,
                sideLabel: left ? "FOM A" : "FOM B",
                fomLabel: left ? LeftLabel : RightLabel,
                attributeName: row.QualifiedName));
        }
        catch (Exception ex)
        {
            // Explain is documented never to throw on content, so anything landing here is a bug
            // rather than a malformed FOM. Say so instead of taking the shell down with it.
            _dialogs.ShowError("Datatype", $"'{dataType}' could not be read.\n\n{ex.Message}");
        }
    }

    // ---- reading the pair --------------------------------------------------------------------

    /// <summary>
    /// Selects this tab, reads both FOMs and fills the two class pickers, completing only once the
    /// screen has settled.
    /// </summary>
    /// <param name="showBusy">
    /// False when the caller already owns an overlay covering this view. The Compare screen's scrim
    /// spans the whole tab strip, so raising this one underneath it would stack two scrims — twice
    /// the dimming, with the inner progress bar showing faintly through the outer one.
    /// </param>
    /// <remarks>
    /// This is what the Compare button uses. It deliberately does <b>not</b> pick a class: which two
    /// classes to lay against each other is the judgement this screen exists to support, and
    /// choosing one on the user's behalf would present a guess as an answer. What it does guarantee
    /// is that the pickers are populated and any restored pair has finished comparing before the
    /// caller's overlay lifts.
    /// </remarks>
    public async Task ActivateAsync(bool showBusy = true)
    {
        if (!_isActive)
        {
            // Written to the field rather than through the property: the setter would kick off the
            // very fire-and-forget work this method exists to await instead.
            _isActive = true;
            OnPropertyChanged(nameof(IsActive));
        }

        if (!_isStale)
        {
            // Not stale does not mean not busy. Waiting on a finished task costs nothing, and
            // returning early would lift the caller's overlay off a grid still filling itself in.
            await PendingWork.ConfigureAwait(true);
            return;
        }

        _isStale = false;
        RaiseRowState();

        var work = LoadPairAsync(showBusy);
        PendingWork = work;
        await work.ConfigureAwait(true);

        // The load may have restored a class pair, which schedules a comparison of its own.
        await _compareChain.ConfigureAwait(true);
    }

    private void Refresh()
    {
        _isStale = false;
        PendingWork = LoadPairAsync(showBusy: true);
    }

    /// <summary>
    /// Reads both documents and lists their object classes, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load is two whole <c>FomDocument</c>s rebuilt out of SQLite — every class, attribute and
    /// datatype row for both FOMs. Run inline it blocks the dispatcher, which means the busy overlay
    /// it raises can never be painted: the flag goes up and back down inside a single dispatcher
    /// turn, so WPF is never given a frame to draw it in. The screen simply freezes, which on an
    /// RPR-sized pair lasts long enough to read as a hang. On a worker thread the overlay paints,
    /// its bar keeps animating, and the freeze is gone.
    /// </para>
    /// <para>
    /// Both documents are then kept. Every subsequent class pick compares two already-loaded trees,
    /// which is the difference between a wait the user notices and one they do not.
    /// </para>
    /// </remarks>
    private async Task LoadPairAsync(bool showBusy)
    {
        // Read before ClearPair empties the pickers: a Refresh, or a reload after the options
        // change, should leave the user looking at the same two classes.
        var previousA = SelectedClassA?.QualifiedName;
        var previousB = SelectedClassB?.QualifiedName;

        ClearPair();

        if (_leftId is not { } leftId || _rightId is not { } rightId)
        {
            RaiseRowState();
            return;
        }

        // Stamped before the await, checked after it. Changing a picker mid-load starts a second
        // load, and without this the slower of the two wins: the screen ends up showing a full,
        // plausible-looking inventory of a pair the user has already moved off.
        var generation = ++_generation;

        // Cloned for the reason the Compare screen clones its own: the options are bound to live
        // controls, and the worker has to read against the settings in force when it started.
        var options = Options.Clone();

        var busy = showBusy ? BeginBusy("Reading both FOMs…") : null;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var left = _repository.LoadDocument(leftId);
                var right = _repository.LoadDocument(rightId);

                // The resolvers are built here rather than on demand: both documents are already in
                // hand, and this is the only point at which they are.
                return (Left: left,
                        Right: right,
                        ClassesLeft: AttributeMapper.ListClasses(left, options),
                        ClassesRight: AttributeMapper.ListClasses(right, options),
                        LeftResolver: new DataTypeResolver(left),
                        RightResolver: new DataTypeResolver(right));
            }).ConfigureAwait(true);

            if (generation != _generation) return;

            _leftDocument = loaded.Left;
            _rightDocument = loaded.Right;
            _leftResolver = loaded.LeftResolver;
            _rightResolver = loaded.RightResolver;

            foreach (var summary in loaded.ClassesLeft)
                ClassOptionsA.Add(ObjectClassOption.ForClass(summary));

            foreach (var summary in loaded.ClassesRight)
                ClassOptionsB.Add(ObjectClassOption.ForClass(summary));

            ClassesA.Refresh();
            ClassesB.Refresh();

            StatusMessage =
                $"{ClassOptionsA.Count} class{(ClassOptionsA.Count == 1 ? "" : "es")} in A, " +
                $"{ClassOptionsB.Count} in B — pick one on each side";

            RestoreSelection(previousA, previousB);
        }
        catch (Exception ex)
        {
            // A superseded load fails quietly. Its pair is no longer on screen, so a dialog about it
            // would name two FOMs the user has already moved off.
            if (generation != _generation) return;

            ClearPair();
            RaiseRowState();
            _dialogs.ShowError("Attribute data",
                $"The object classes could not be read for these two FOMs.\n\n{ex.Message}");
        }
        finally
        {
            busy?.Dispose();
        }
    }

    /// <summary>
    /// Puts the user back on the two classes they were looking at, where those classes still exist.
    /// </summary>
    /// <remarks>
    /// Written through the fields rather than the properties, so the two sides do not fire a
    /// comparison each. One is scheduled at the end, against the pair as a whole.
    /// </remarks>
    private void RestoreSelection(string? previousA, string? previousB)
    {
        _selectedClassA = Find(ClassOptionsA, previousA);
        _selectedClassB = Find(ClassOptionsB, previousB);

        ClassesA.Refresh();
        ClassesB.Refresh();

        OnPropertyChanged(nameof(SelectedClassA), nameof(SelectedClassB),
            nameof(HasClassA), nameof(HasClassB), nameof(Summary));

        ClearClassACommand.RaiseCanExecuteChanged();
        ClearClassBCommand.RaiseCanExecuteChanged();

        if (_selectedClassA is not null || _selectedClassB is not null) ScheduleCompare();
        else RaiseRowState();

        static ObjectClassOption? Find(IEnumerable<ObjectClassOption> options, string? qualifiedName) =>
            qualifiedName is null
                ? null
                : options.FirstOrDefault(
                    option => string.Equals(option.QualifiedName, qualifiedName, StringComparison.Ordinal));
    }

    private void ClearPair()
    {
        Map = null;

        _leftDocument = null;
        _rightDocument = null;

        // Dropped with the documents they describe: a resolver kept past its pair would answer for
        // the wrong FOM, which is worse than answering not at all.
        _leftResolver = null;
        _rightResolver = null;

        Rows.Clear();
        Advisories.Clear();
        SelectedRow = null;

        ClassOptionsA.Clear();
        ClassOptionsB.Clear();

        _selectedClassA = null;
        _selectedClassB = null;

        ClassesA.Refresh();
        ClassesB.Refresh();

        OnPropertyChanged(nameof(SelectedClassA), nameof(SelectedClassB),
            nameof(HasClassA), nameof(HasClassB), nameof(ComparesBothSides));

        ClearClassACommand.RaiseCanExecuteChanged();
        ClearClassBCommand.RaiseCanExecuteChanged();
    }

    // ---- comparing ---------------------------------------------------------------------------

    /// <summary>Queues a comparison of the two currently chosen classes.</summary>
    private void ScheduleCompare()
    {
        var generation = ++_compareGeneration;
        _compareChain = CompareAsync(_compareChain, generation);
        PendingWork = _compareChain;
    }

    /// <summary>
    /// Compares the two chosen classes, off the UI thread, after whatever comparison was already in
    /// flight has finished.
    /// </summary>
    /// <param name="previous">The comparison this one queues behind.</param>
    /// <param name="generation">
    /// Stamped when this comparison was asked for. Any later pick makes it stale, and a stale
    /// comparison must never paint: the grid would end up showing a full, plausible-looking map of a
    /// pairing the user has already moved off, with nothing on screen saying so.
    /// </param>
    private async Task CompareAsync(Task previous, int generation)
    {
        // The predecessor's own outcome is not this comparison's business; its failure was already
        // reported to the user by the run that owned it.
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Deliberately swallowed — see above.
        }

        if (generation != _compareGeneration) return;

        var leftName = _selectedClassA?.QualifiedName;
        var rightName = _selectedClassB?.QualifiedName;

        if (_leftDocument is not { } left || _rightDocument is not { } right ||
            (leftName is null && rightName is null))
        {
            Map = null;
            Rows.Clear();
            Advisories.Clear();
            SelectedRow = null;
            RaiseRowState();
            return;
        }

        var options = Options.Clone();

        // Armed rather than raised. Two already-loaded classes compare in well under a millisecond,
        // so this all but never paints; it is here for the pathological FOM where it would.
        using var busy = BeginBusyAfter(DescribeCompare(leftName, rightName));

        try
        {
            var built = await Task.Run(
                () => AttributeMapper.BuildForClasses(left, right, leftName, rightName, options))
                .ConfigureAwait(true);

            if (generation != _compareGeneration) return;

            Map = built;

            Advisories.Clear();
            foreach (var advisory in built.Advisories)
                Advisories.Add(advisory);

            ApplyFilter();

            StatusMessage = !built.ComparesBothSides
                ? $"{built.Rows.Count} attribute{Plural(built.Rows.Count)} listed — " +
                  $"pick a class on the other side to compare"
                : built.ActionableCount == 0
                    ? "Attribute data lines up on both sides"
                    : $"{built.ActionableCount} attribute{Plural(built.ActionableCount)} " +
                      $"need{(built.ActionableCount == 1 ? "s" : "")} remapping";
        }
        catch (Exception ex)
        {
            if (generation != _compareGeneration) return;

            // A comparison that cannot be built leaves the strip and the empty state usable rather
            // than taking the shell down; EmptyMessage explains what happened.
            Map = null;
            Rows.Clear();
            SelectedRow = null;
            RaiseRowState();
            _dialogs.ShowError("Attribute data",
                $"The attribute map could not be built for these two classes.\n\n{ex.Message}");
        }
    }

    /// <summary>What the overlay would say, on the rare occasion it appears.</summary>
    private string DescribeCompare(string? leftName, string? rightName) =>
        leftName is not null && rightName is not null
            ? $"Comparing {LeafOf(leftName)} against {LeafOf(rightName)}…"
            : $"Listing {LeafOf(leftName ?? rightName)}…";

    // ---- picker filtering ---------------------------------------------------------------------

    private bool PassesA(object item) => Passes(item, _selectedClassA, _classFilterA);

    private bool PassesB(object item) => Passes(item, _selectedClassB, _classFilterB);

    /// <summary>
    /// Whether one class survives what has been typed into its picker.
    /// </summary>
    /// <remarks>
    /// The current selection is <b>always</b> admitted. That single rule is what makes a filtered
    /// ComboBox usable: WPF's Selector drops <c>SelectedItem</c> the instant the selected item
    /// leaves the items collection, and the ComboBox then rewrites its editable text box from the
    /// now-null selection, deleting the word the user is halfway through typing.
    /// </remarks>
    private static bool Passes(object item, ObjectClassOption? selected, string filter)
    {
        if (item is not ObjectClassOption option) return false;

        return (selected is not null && ReferenceEquals(option, selected)) || option.Matches(filter);
    }

    // ---- row filtering --------------------------------------------------------------------

    private void ApplyFilter()
    {
        // The selection survives a filter change whenever the row is still visible.
        var selectedKey = SelectedRow?.QualifiedName;

        Rows.Clear();

        if (Map is { } map)
        {
            foreach (var row in map.Rows)
            {
                if (Matches(row)) Rows.Add(row);
            }
        }

        SelectedRow = selectedKey is null
            ? null
            : Rows.FirstOrDefault(r => string.Equals(r.QualifiedName, selectedKey, StringComparison.Ordinal));

        RaiseRowState();
    }

    private bool Matches(AttributeMapRow row)
    {
        // A rename is filtered out here rather than by its own toggle, because "needs attention" is
        // a statement about work and a renamed datatype is none: the bits on the wire are unchanged.
        //
        // Inert while only one class is chosen. "Needs attention" is a statement about a comparison,
        // and there has not been one — applying it would empty the grid of a class the user has just
        // asked to see and blame it on a filter.
        if (OnlyDifferences && Map is { ComparesBothSides: true } && !NeedsAttention(row)) return false;

        if (!IsKindVisible(row.Status)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();

        // Declared-in and Note are excluded on purpose: the search box is how a user finds one
        // attribute or one encoding, and matching the owning class of every inherited attribute would
        // return most of the class for a common ancestor name. The resolved encodings are searchable
        // so that a canonical form such as "uint:32" gathers every attribute carrying those bits,
        // whatever the two FOMs happen to call the type.
        return Contains(row.AttributeName, needle)
            || Contains(row.RightAttributeName, needle)
            || Contains(row.LeftDataType, needle)
            || Contains(row.RightDataType, needle)
            || Contains(row.LeftEncoding, needle)
            || Contains(row.RightEncoding, needle);
    }

    /// <summary>
    /// Whether a row is something the user has to look at. A match is not, and neither is a rename.
    /// A move stays in: nothing needs converting, but the attribute did change class, which is worth
    /// seeing — its own toggle is there to drop it.
    /// </summary>
    private static bool NeedsAttention(AttributeMapRow row) =>
        row.IsDifferent && row.Status != AttributeMapStatus.Renamed;

    /// <summary>
    /// Whether the chips admit a status. Unpaired has no chip and answers true through the default
    /// arm: it is the state of a half-made choice rather than a kind of finding.
    /// </summary>
    private bool IsKindVisible(AttributeMapStatus status) => status switch
    {
        AttributeMapStatus.Same => ShowSame,
        AttributeMapStatus.DataTypeChanged => ShowChanged,
        AttributeMapStatus.Renamed => ShowRenamed,
        AttributeMapStatus.Moved => ShowMoved,
        AttributeMapStatus.OnlyInLeft => ShowOnlyLeft,
        AttributeMapStatus.OnlyInRight => ShowOnlyRight,
        _ => true,
    };

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void RaiseRowState()
    {
        OnPropertyChanged(nameof(HasRows), nameof(EmptyMessage), nameof(IsAwaitingCompare),
            nameof(Summary), nameof(ComparesBothSides));
        ExportCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The segment after the last dot, for a headline that has no room for the path.</summary>
    private static string LeafOf(string? qualifiedName)
    {
        if (string.IsNullOrEmpty(qualifiedName)) return "";

        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < qualifiedName.Length - 1
            ? qualifiedName[(lastDot + 1)..]
            : qualifiedName;
    }

    // ---- the worksheet ----------------------------------------------------------------------

    /// <summary>
    /// Writes the visible rows as the leveled, side-by-side Excel worksheet.
    /// </summary>
    /// <remarks>
    /// The filtered view is written, not the whole map: this file is the remap worksheet somebody
    /// works through, so it holds exactly the rows they narrowed the screen down to. Each row is then
    /// unfolded through the structure of its datatype, which is the half of the answer the grid has
    /// no room for — the grid says an attribute re-encodes, the sheet says which field of it does.
    /// </remarks>
    private void ExportWorkbook()
    {
        if (Map is not { } map || Rows.Count == 0) return;
        if (_leftDocument is not { } left || _rightDocument is not { } right) return;

        // Named after the two classes rather than the two FOMs: the sheet is one class pair, and on a
        // run of exports from the same pair of FOMs the FOM names would make every file alike.
        var suggested = map.ComparesBothSides
            ? $"{LeafOf(map.LeftClassName)}-to-{LeafOf(map.RightClassName)}-attributes"
            : $"{LeafOf(map.LeftClassName ?? map.RightClassName)}-attributes";

        var path = _dialogs.SaveFile(
            "Export attribute worksheet",
            "Excel workbook|*.xlsx|All files|*.*",
            $"{Sanitize(suggested)}.xlsx",
            "xlsx");

        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var sheet = AttributePairExporter.Build(
                map, Rows.ToList(), left, right,
                new AttributePairSheetOptions { Comparison = Options.Clone() });

            AttributePairExporter.WriteXlsx(sheet, path);

            var expanded = sheet.Rows.Count - Rows.Count;

            StatusMessage = $"Attribute worksheet written to {Path.GetFileName(path)}";
            _dialogs.ShowInfo("Export complete",
                $"{Rows.Count} attribute{Plural(Rows.Count)}" +
                (expanded > 0 ? $", unfolded to {sheet.Rows.Count} rows," : "") +
                $" written to:\n\n{path}");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    /// <summary>
    /// The wording used for a status wherever it is shown to a person — badge, chip and export
    /// column alike, so a CSV reader and a screen reader see the same words.
    /// </summary>
    public static string StatusLabel(AttributeMapStatus status) => AttributeMapStatusText.Label(status);

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attribute-map" : cleaned;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
