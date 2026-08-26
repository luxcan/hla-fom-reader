using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Registry;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// One entry in the object-class picker: a class the map carries rows for, or the "All classes"
/// sentinel that turns the scope off.
/// </summary>
/// <remarks>
/// The leaf name is held apart from the path it sits under because the picker is used by typing.
/// Every class in a FOM is named <c>ObjectRoot.something.something.Aircraft</c>, so a list showing
/// qualified names is a list of identical prefixes: type-to-select would match "ObjectRoot" on the
/// first keystroke and never reach the name the user has in mind. The leaf leads, and the path
/// follows it as quiet context for the two classes that share a leaf name.
/// </remarks>
public sealed class ObjectClassOption
{
    /// <summary>Wording for the sentinel, used in the list and by the clear command.</summary>
    public const string AllClassesLabel = "All classes";

    private ObjectClassOption(string qualifiedName, string leafName, string? path, int rowCount, bool isAll)
    {
        QualifiedName = qualifiedName;
        LeafName = leafName;
        Path = path;
        RowCount = rowCount;
        IsAll = isAll;
    }

    /// <summary>The fully qualified dotted name, or <c>""</c> for the "all classes" sentinel.</summary>
    public string QualifiedName { get; }

    /// <summary>The segment after the last dot — "Aircraft" — or the sentinel's wording.</summary>
    public string LeafName { get; }

    /// <summary>Everything before the last dot, or null when there is nothing above the leaf.</summary>
    public string? Path { get; }

    /// <summary>How many attribute rows this class contributes; the whole map, for the sentinel.</summary>
    public int RowCount { get; }

    /// <summary>True for the sentinel that scopes to nothing.</summary>
    public bool IsAll { get; }

    /// <summary>The sentinel, carrying the total row count so the list reads as a breakdown.</summary>
    public static ObjectClassOption All(int totalRows) =>
        new("", AllClassesLabel, null, totalRows, isAll: true);

    /// <summary>Splits a qualified class name into the leaf the user types and the path behind it.</summary>
    public static ObjectClassOption ForClass(string qualifiedName, int rowCount)
    {
        var name = qualifiedName;
        var lastDot = name.LastIndexOf('.');

        // A trailing dot or a bare root name leaves nothing to split, and a leaf is required — a
        // blank entry would be unreachable by typing and unreadable in the list.
        var leaf = lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
        var path = lastDot > 0 && lastDot < name.Length - 1 ? name[..lastDot] : null;

        return new ObjectClassOption(name, leaf, path, rowCount, isAll: false);
    }

    /// <summary>Type-to-select and the collapsed field both fall back to this.</summary>
    public override string ToString() => LeafName;
}

/// <summary>
/// The "Attribute data" tab of the Compare screen: one flat row per attribute a federate could
/// publish or reflect, with the datatype on each side beside it.
/// </summary>
/// <remarks>
/// <para>
/// This screen answers a narrower question than the difference tree — "what data changed, and how do
/// I remap it?" — so it shows only the two things that exist on the wire: which attributes a class
/// carries, and what each one is typed as. Sharing, ownership, update type, semantics and qualified
/// names are properties of the model rather than of the data, and are deliberately absent.
/// </para>
/// <para>
/// The rows come from <see cref="AttributeMapper"/>, which resolves each class to its
/// <b>effective</b> attribute set — declared plus everything inherited from its ancestors. A class
/// that declares nothing still publishes its ancestors' attributes, so the declared set would report
/// most of a deep FOM as empty.
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

    private AttributeDataMap? _map;
    private AttributeMapRow? _selectedRow;

    // Kept from the last rebuild so clicking an encoding cell answers instantly. A resolver holds
    // only the document's datatype tables, not its class tree, so this is a fraction of the cost of
    // reloading the document — and reloading two documents on a click would stall the UI thread for
    // exactly as long as the IsActive gate exists to avoid. See ShowEncoding.
    private DataTypeResolver? _leftResolver;
    private DataTypeResolver? _rightResolver;
    private ObjectClassOption? _selectedObjectClass;

    // Defaults OFF: the map is read as a whole worksheet — you look up a class and see
    // everything it carries, changed or not — rather than as a filtered to-do list.
    private bool _onlyDifferences;
    private string _searchText = "";
    private bool _showSame;
    private bool _showChanged = true;
    private bool _showRenamed = true;
    private bool _showMoved = true;
    private bool _showOnlyLeft = true;
    private bool _showOnlyRight = true;

    private bool _isActive;
    private bool _isStale = true;

    // Bumped by each rebuild and re-checked when that rebuild's worker returns. See RebuildAsync.
    private int _generation;

    /// <summary>Creates the screen. Nothing is read until a pair is set and the tab is shown.</summary>
    /// <param name="repository">Store both FOM documents are rebuilt from.</param>
    /// <param name="dialogs">Used for the save dialog and for surfacing repository failures.</param>
    public AttributeMapViewModel(IFomRepository repository, IDialogService dialogs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        RefreshCommand = new RelayCommand(Refresh);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ClearClassScopeCommand = new RelayCommand(ClearClassScope, () => IsClassScoped);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => Map is not null && Rows.Count > 0);

        ShowLeftDataTypeCommand = new RelayCommand<AttributeMapRow>(
            row => ShowDataType(row, left: true), row => CanShowDataType(row, left: true));

        ShowRightDataTypeCommand = new RelayCommand<AttributeMapRow>(
            row => ShowDataType(row, left: false), row => CanShowDataType(row, left: false));
    }

    /// <summary>The rows that survive the current filters, in the order the mapper produced them.</summary>
    public ObservableCollection<AttributeMapRow> Rows { get; } = new();

    /// <summary>
    /// The picker's contents: the "All classes" sentinel, then every class the map carries rows for.
    /// </summary>
    /// <remarks>
    /// Deliberately left in the mapper's own order rather than sorted. The mapper emits the left
    /// document's class tree, root first, which is the order the FOM itself is written in and the
    /// order somebody who knows the FOM expects to scroll through. Sorting alphabetically would put
    /// <c>Aircraft</c> beside <c>AmphibiousVehicle</c> and tear the hierarchy apart; typing into the
    /// picker is what finds one class quickly, not the ordering.
    /// </remarks>
    public ObservableCollection<ObjectClassOption> ObjectClasses { get; } = new();

    /// <summary>Fidelity notes from the mapper, e.g. one side being a FED with no datatype table.</summary>
    public ObservableCollection<string> Advisories { get; } = new();

    /// <summary>
    /// How the map is built. A separate instance from the difference tree's options on purpose: the
    /// tree's depth and property switches say nothing about attribute data, and a user narrowing the
    /// tree should not silently change the remap worksheet under it.
    /// </summary>
    public ComparisonOptions Options { get; } = new();

    /// <summary>Rebuilds the map from the two documents currently selected.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Empties <see cref="SearchText"/>.</summary>
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Widens the picker back to "All classes". Disabled while nothing is scoped.</summary>
    public RelayCommand ClearClassScopeCommand { get; }

    /// <summary>Writes the visible rows out as the remap worksheet.</summary>
    public RelayCommand ExportCsvCommand { get; }

    /// <summary>Opens the datatype inspector for a row's FOM A datatype.</summary>
    public RelayCommand<AttributeMapRow> ShowLeftDataTypeCommand { get; }

    /// <summary>Opens the datatype inspector for a row's FOM B datatype.</summary>
    public RelayCommand<AttributeMapRow> ShowRightDataTypeCommand { get; }

    /// <summary>
    /// The map rebuild currently in flight, or a completed task when the screen is idle.
    /// </summary>
    /// <remarks>
    /// The entry points below are property setters and void callbacks, so they cannot hand their
    /// task back to whoever triggered them. Parking it here costs nothing and means the work is
    /// observable rather than merely discarded: a caller that must not race it can wait on this
    /// instead of guessing at a delay.
    /// </remarks>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    /// <summary>The whole map, unfiltered; null until a pair has been read successfully.</summary>
    public AttributeDataMap? Map
    {
        get => _map;
        private set
        {
            if (!SetProperty(ref _map, value)) return;
            OnPropertyChanged(nameof(Summary), nameof(SameCount), nameof(ChangedCount),
                nameof(RenamedCount), nameof(SameOrRenamedCount), nameof(MovedCount), nameof(OnlyLeftCount),
                nameof(OnlyRightCount));
            ExportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The highlighted row. Kept across a filter change whenever it is still visible.</summary>
    public AttributeMapRow? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    /// <summary>
    /// The class the grid is scoped to, or the "All classes" sentinel. Null only before a map exists.
    /// </summary>
    /// <remarks>
    /// This is the screen's primary control. On the user's real pair the map is 1690 rows across a
    /// couple of hundred classes, and the question being asked is almost always about one of them —
    /// "what changed for Aircraft?" — which the free-text box can only approximate: searching
    /// "Aircraft" also matches every class whose name contains it and every attribute that mentions
    /// it. Picking the class answers exactly, and the list doubles as the inventory of what the two
    /// FOMs contain.
    /// </remarks>
    public ObjectClassOption? SelectedObjectClass
    {
        get => _selectedObjectClass;
        set
        {
            if (!SetProperty(ref _selectedObjectClass, value)) return;

            OnPropertyChanged(nameof(IsClassScoped), nameof(Summary));
            ClearClassScopeCommand.RaiseCanExecuteChanged();
            ApplyFilter();
        }
    }

    /// <summary>True while the grid is narrowed to a single class.</summary>
    public bool IsClassScoped => _selectedObjectClass is { IsAll: false };

    /// <summary>
    /// Hides every row that needs no decision — attributes that line up with the same datatype, and
    /// attributes whose datatype was only renamed. Defaults to true: on a real FOM pair the
    /// overwhelming majority of rows are untouched, and the point of this screen is the handful that
    /// need a decision.
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

            // This checkbox and the "Same" chip are two controls over one idea, so the field is set
            // directly rather than through the sibling property — that notifies without re-filtering
            // twice or bouncing back into this setter.
            SetProperty(ref _showSame, !value, nameof(ShowSame));
            ApplyFilter();
        }
    }

    /// <summary>Free-text filter over the class name, the attribute name and either datatype.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            ApplyFilter();
        }
    }

    /// <summary>Show attributes that match on both sides. Mirrors <see cref="OnlyDifferences"/>.</summary>
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

    /// <summary>Show attributes that only moved to another class in the hierarchy.</summary>
    public bool ShowMoved
    {
        get => _showMoved;
        set
        {
            if (!SetProperty(ref _showMoved, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Show attributes that exist in FOM A only.</summary>
    public bool ShowOnlyLeft
    {
        get => _showOnlyLeft;
        set
        {
            if (!SetProperty(ref _showOnlyLeft, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Show attributes that exist in FOM B only.</summary>
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

    /// <summary>Attributes only FOM A has.</summary>
    public int OnlyLeftCount => Map?.OnlyInLeftCount ?? 0;

    /// <summary>Attributes only FOM B has.</summary>
    public int OnlyRightCount => Map?.OnlyInRightCount ?? 0;

    /// <summary>True once the parent has handed over both FOMs.</summary>
    public bool HasPair => _leftId is not null && _rightId is not null;

    /// <summary>True when the grid has at least one row after filtering.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True before this pair has been mapped — the tab is empty because nothing ran yet.</summary>
    public bool IsAwaitingCompare => _isStale;

    /// <summary>
    /// Headline on the right of the filter strip, e.g.
    /// "1690 attributes · 47 re-encode · 567 renamed only · 313 only in A · 505 only in B". Blank
    /// until a map exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Re-encode" leads because it is the number somebody has to act on. Renames are reported
    /// immediately after it and worded as "renamed only" so the two are never read as one figure:
    /// counting a rename as work is what made the old headline useless on a large migration.
    /// </para>
    /// <para>
    /// While a class is picked the line describes that class instead — "Aircraft · 45 attributes ·
    /// 3 re-encode · 12 renamed only · 1 only in A" — because it is then the answer to the question
    /// the user actually asked. A headline still quoting the whole FOM's totals beside a grid showing
    /// one class would be read as that class's figures and be wrong by two orders of magnitude.
    /// Zero segments are dropped there: most classes change in one way or none, and a line of zeroes
    /// hides the one number that is not.
    /// </para>
    /// </remarks>
    public string Summary
    {
        get
        {
            if (Map is not { } map) return "";
            if (SelectedObjectClass is { IsAll: false } scope) return ScopedSummary(map, scope);

            var total = map.Rows.Count;
            var text = $"{total} attribute{Plural(total)} · " +
                       $"{map.DataTypeChangedCount} re-encode · " +
                       $"{map.RenamedCount} renamed only · " +
                       $"{map.OnlyInLeftCount} only in A · {map.OnlyInRightCount} only in B";

            // A moved attribute is still available on the class, so it is informational and only
            // earns a place in the headline when there is actually one to report.
            return map.MovedCount > 0 ? $"{text} · {map.MovedCount} moved" : text;
        }
    }

    /// <summary>
    /// The headline for one class: its name, its attribute count, and only those status counts that
    /// are not zero, in the same order and wording the overall line uses.
    /// </summary>
    private static string ScopedSummary(AttributeDataMap map, ObjectClassOption scope)
    {
        var counts = new int[6];
        var total = 0;

        foreach (var row in map.Rows)
        {
            if (!string.Equals(row.ClassName, scope.QualifiedName, StringComparison.Ordinal)) continue;

            total++;
            var index = (int)row.Status;
            if (index >= 0 && index < counts.Length) counts[index]++;
        }

        // The leaf name alone: the path is on screen in the picker and in every row of the Class
        // column, and repeating "ObjectRoot.BaseEntity.PhysicalEntity.Platform." here would push the
        // counts — the part being read — off the end of the strip.
        var parts = new List<string> { scope.LeafName, $"{total} attribute{Plural(total)}" };

        Add(counts[(int)AttributeMapStatus.DataTypeChanged], "re-encode");
        Add(counts[(int)AttributeMapStatus.Renamed], "renamed only");
        Add(counts[(int)AttributeMapStatus.OnlyInLeft], "only in A");
        Add(counts[(int)AttributeMapStatus.OnlyInRight], "only in B");
        Add(counts[(int)AttributeMapStatus.Moved], "moved");

        return string.Join(" · ", parts);

        void Add(int count, string label)
        {
            if (count > 0) parts.Add($"{count} {label}");
        }
    }

    /// <summary>Explains an empty grid — the reason differs and the user cannot be left guessing.</summary>
    public string EmptyMessage =>
        !HasPair ? "Choose FOM A and FOM B above to see which attributes carry data on each side."
        : IsAwaitingCompare ? "Press Compare to map the attribute data for these two FOMs."
        : Map is null ? "The attribute map could not be built from these two FOMs."
        : Map.Rows.Count == 0 ? "Neither FOM declares or inherits a single object-class attribute."
        : "No attributes match the current filters.";

    /// <summary>
    /// True while the "Attribute data" tab is the visible one. Bound from the TabItem.
    /// </summary>
    /// <remarks>
    /// Selection only — showing this tab does not build anything. The Compare screen fills all three
    /// of its tabs in one pass, so that a single overlay covers the whole wait and every tab has its
    /// data by the time it lifts. A tab that quietly built itself on arrival made a partial load look
    /// like a complete one: the attribute map filled in, the class list stayed empty, and nothing on
    /// screen distinguished "not loaded" from "nothing to show". See CompareViewModel.CompareAsync.
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

        ClearMap();

        // Marked stale and left that way. Reading it costs two full document loads, and rebuilding
        // here would run once per picker — twice for a user setting both — for a result the next
        // Compare throws away. Compare is what fills this tab. See IsActive.
        _isStale = true;

        OnPropertyChanged(nameof(HasPair));
        RaiseRowState();
    }

    // ---- the datatype inspector ---------------------------------------------------------

    /// <summary>
    /// True when there is a datatype on that side to open. A row that is only in one FOM has nothing
    /// on the other, and a rebuild that failed has no resolver, so the cell stays plain text.
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

    // ---- building ---------------------------------------------------------------------------

    /// <summary>
    /// Selects this tab and builds its map, completing only once the rows are on screen.
    /// </summary>
    /// <param name="showBusy">
    /// False when the caller already owns an overlay covering this view. The Compare screen's scrim
    /// spans the whole tab strip, so raising this one underneath it would stack two scrims — twice
    /// the dimming, with the inner progress bar showing faintly through the outer one.
    /// </param>
    /// <remarks>
    /// This is what the Compare button uses. Setting <see cref="IsActive"/> starts the same rebuild,
    /// but as a task nobody holds, so the button's overlay would lift while the map was still being
    /// built and leave the user watching empty columns fill themselves in.
    /// </remarks>
    public async Task ActivateAsync(bool showBusy = true)
    {
        if (!_isActive)
        {
            // Written to the field rather than through the property: the setter would kick off the
            // very fire-and-forget rebuild this method exists to await instead.
            _isActive = true;
            OnPropertyChanged(nameof(IsActive));
        }

        if (!_isStale)
        {
            // Not stale does not mean not building. Changing a picker marks the map stale and, if
            // this tab is already showing, starts the rebuild there and then — so a user who edits a
            // picker and reaches straight for Compare arrives here with that rebuild still in flight.
            // Returning now would lift the caller's overlay off a grid that is still empty, which is
            // the one thing it is there to prevent. Waiting on a finished task costs nothing.
            await PendingWork.ConfigureAwait(true);
            return;
        }

        _isStale = false;
        RaiseRowState();

        var rebuild = RebuildAsync(showBusy);
        PendingWork = rebuild;
        await rebuild.ConfigureAwait(true);
    }

    private void Refresh()
    {
        _isStale = false;
        PendingWork = RebuildAsync(showBusy: true);
    }

    /// <summary>
    /// Loads both documents and maps them, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load is two whole <c>FomDocument</c>s rebuilt out of SQLite — every class, attribute and
    /// datatype row for both FOMs — and then the mapper walking them. Run inline it blocks the
    /// dispatcher, which means the busy overlay it raises can never be painted: the flag goes up and
    /// back down inside a single dispatcher turn, so WPF is never given a frame to draw it in. The
    /// screen simply freezes, which on an RPR-sized pair lasts long enough to read as a hang. On a
    /// worker thread the overlay paints, its bar keeps animating, and the freeze is gone.
    /// </para>
    /// <para>
    /// Only the read and the map go off-thread. The collections are filled back on the dispatcher in
    /// one pass — required, since <see cref="ObservableCollection{T}"/> may not be touched from
    /// anywhere else, and also what the user should see: the grid goes from empty to complete with no
    /// half-populated state in between.
    /// </para>
    /// </remarks>
    private async Task RebuildAsync(bool showBusy)
    {
        // Read before ClearMap empties the picker: a Refresh, or a rebuild after the options change,
        // should leave the user looking at the same class rather than snapping back to the whole FOM.
        var previousClass = SelectedObjectClass?.QualifiedName;

        ClearMap();

        if (_leftId is not { } leftId || _rightId is not { } rightId)
        {
            RaiseRowState();
            return;
        }

        // Stamped before the await, checked after it. Changing a picker mid-build starts a second
        // rebuild, and without this the slower of the two wins: the screen ends up showing a full,
        // plausible-looking map of a pair the user has already moved off, with nothing saying so.
        var generation = ++_generation;

        // Cloned for the reason the Compare screen clones its own: the options are bound to live
        // controls, and the worker has to map against the settings in force when it started.
        var options = Options.Clone();

        var busy = showBusy ? BeginBusy("Mapping attribute data…") : null;
        try
        {
            var built = await Task.Run(() =>
            {
                var left = _repository.LoadDocument(leftId);
                var right = _repository.LoadDocument(rightId);

                // The resolvers are built here rather than on demand: both documents are already in
                // hand, and this is the only point at which they are.
                return (Map: AttributeMapper.Build(left, right, options),
                        LeftResolver: new DataTypeResolver(left),
                        RightResolver: new DataTypeResolver(right));
            }).ConfigureAwait(true);

            if (generation != _generation) return;

            var map = built.Map;
            Map = map;
            _leftResolver = built.LeftResolver;
            _rightResolver = built.RightResolver;

            foreach (var advisory in map.Advisories)
                Advisories.Add(advisory);

            RebuildObjectClasses(previousClass);
            ApplyFilter();

            StatusMessage = map.ActionableCount == 0
                ? "Attribute data lines up in both FOMs"
                : $"{map.ActionableCount} attribute{Plural(map.ActionableCount)} " +
                  $"need{(map.ActionableCount == 1 ? "s" : "")} remapping";
        }
        catch (Exception ex)
        {
            // A superseded rebuild fails quietly. Its pair is no longer on screen, so a dialog about
            // it would name two FOMs the user has already moved off.
            if (generation != _generation) return;

            // A map that cannot be built leaves the strip and the empty state usable rather than
            // taking the shell down; EmptyMessage explains what happened.
            Map = null;
            RebuildObjectClasses(null);
            RaiseRowState();
            _dialogs.ShowError("Attribute data",
                $"The attribute map could not be built for these two FOMs.\n\n{ex.Message}");
        }
        finally
        {
            busy?.Dispose();
        }
    }

    private void ClearMap()
    {
        Map = null;

        // Dropped with the map they describe: a resolver kept past its pair would answer for the
        // wrong FOM, which is worse than answering not at all.
        _leftResolver = null;
        _rightResolver = null;

        Rows.Clear();
        Advisories.Clear();
        SelectedRow = null;
        RebuildObjectClasses(null);
    }

    /// <summary>
    /// Repopulates the picker from the current map, keeping <paramref name="preferredClass"/>
    /// selected when that class still exists and falling back to "All classes" when it does not.
    /// </summary>
    /// <remarks>
    /// The selection is written to the field rather than through the property. The picker is being
    /// emptied and refilled either side of this, and routing through the setter would re-filter the
    /// grid against a half-built list — once for the clear and once for the restore — in the middle
    /// of a rebuild that calls <see cref="ApplyFilter"/> itself immediately afterwards.
    /// </remarks>
    private void RebuildObjectClasses(string? preferredClass)
    {
        ObjectClasses.Clear();

        if (Map is { } map)
        {
            // Counted in one pass, in first-appearance order: that is the mapper's left-document tree
            // order, and a Dictionary alone would not preserve it.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var row in map.Rows)
            {
                if (counts.TryGetValue(row.ClassName, out var seen))
                {
                    counts[row.ClassName] = seen + 1;
                }
                else
                {
                    counts[row.ClassName] = 1;
                    order.Add(row.ClassName);
                }
            }

            ObjectClasses.Add(ObjectClassOption.All(map.Rows.Count));

            foreach (var className in order)
                ObjectClasses.Add(ObjectClassOption.ForClass(className, counts[className]));
        }

        var restored = preferredClass is null
            ? null
            : ObjectClasses.FirstOrDefault(
                option => !option.IsAll &&
                          string.Equals(option.QualifiedName, preferredClass, StringComparison.Ordinal));

        // FirstOrDefault is the sentinel when the list has one, and null when there is no map at all.
        _selectedObjectClass = restored ?? ObjectClasses.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedObjectClass), nameof(IsClassScoped), nameof(Summary));
        ClearClassScopeCommand.RaiseCanExecuteChanged();
    }

    private void ClearClassScope()
    {
        var all = ObjectClasses.FirstOrDefault(option => option.IsAll);
        if (all is not null) SelectedObjectClass = all;
    }

    // ---- filtering --------------------------------------------------------------------------

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
        // The class scope narrows before anything else and composes with the rest: picking Aircraft
        // and then unticking "Renamed" asks for Aircraft's attributes minus the renames, not for a
        // fresh start. Ordinal, because these names came from the map itself — nothing to fold.
        if (SelectedObjectClass is { IsAll: false } scope &&
            !string.Equals(row.ClassName, scope.QualifiedName, StringComparison.Ordinal)) return false;

        // A rename is filtered out here rather than by its own toggle, because "needs attention" is
        // a statement about work and a renamed datatype is none: the bits on the wire are unchanged.
        if (OnlyDifferences && !NeedsAttention(row)) return false;
        if (!IsKindVisible(row.Status)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var needle = SearchText.Trim();

        // Declared-in and Note are excluded on purpose: the search box is how a user finds one
        // attribute or one encoding, and matching the owning class of every inherited attribute would
        // return most of the FOM for a common ancestor name. The resolved encodings are searchable so
        // that a canonical form such as "uint:32" gathers every attribute carrying those bits,
        // whatever the two FOMs happen to call the type.
        return Contains(row.ClassName, needle)
            || Contains(row.AttributeName, needle)
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
        OnPropertyChanged(nameof(HasRows), nameof(EmptyMessage), nameof(IsAwaitingCompare));
        ExportCsvCommand.RaiseCanExecuteChanged();
    }

    // ---- CSV export -------------------------------------------------------------------------

    private void ExportCsv()
    {
        if (Map is null || Rows.Count == 0) return;

        var path = _dialogs.SaveFile(
            "Export attribute map",
            "CSV files|*.csv|All files|*.*",
            $"{Sanitize($"{LeftLabel}-to-{RightLabel}-attribute-map")}.csv",
            "csv");

        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // The filtered view is written, not the whole map: this file is the remap worksheet
            // somebody works through, so it should hold exactly the rows they narrowed the screen
            // down to. The Status column keeps it readable without the filter settings.
            File.WriteAllText(path, BuildCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            StatusMessage = $"Attribute map written to {Path.GetFileName(path)}";
            _dialogs.ShowInfo("Export complete",
                $"{Rows.Count} attribute{Plural(Rows.Count)} written to:\n\n{path}");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    /// <summary>
    /// Renders the visible rows as RFC 4180 CSV — CRLF line endings, quoting only where the format
    /// requires it — so the worksheet opens in a spreadsheet without a parsing step.
    /// </summary>
    private string BuildCsv()
    {
        var builder = new StringBuilder();

        // Each encoding sits beside the datatype name it resolves to, so a reader sorting or
        // filtering the sheet can tell a rename from a re-encode without trusting the Status column.
        builder.Append("Class,Attribute,Status,DeclaredInA,DataTypeA,EncodingA," +
                       "DeclaredInB,DataTypeB,EncodingB,Note\r\n");

        foreach (var row in Rows)
        {
            builder.Append(Quote(row.ClassName)).Append(',')
                   .Append(Quote(row.AttributeName)).Append(',')
                   .Append(Quote(StatusLabel(row.Status))).Append(',')
                   .Append(Quote(row.LeftDeclaredIn)).Append(',')
                   .Append(Quote(row.LeftDataType)).Append(',')
                   .Append(Quote(row.LeftEncoding)).Append(',')
                   .Append(Quote(row.RightDeclaredIn)).Append(',')
                   .Append(Quote(row.RightDataType)).Append(',')
                   .Append(Quote(row.RightEncoding)).Append(',')
                   .Append(Quote(row.Note)).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The wording used for a status wherever it is shown to a person — badge, chip and export
    /// column alike, so a CSV reader and a screen reader see the same words.
    /// </summary>
    public static string StatusLabel(AttributeMapStatus status) => status switch
    {
        AttributeMapStatus.Same => "Same",
        AttributeMapStatus.DataTypeChanged => "Changed",
        // A rename needs no conversion, so it reads as Same everywhere the user sees it —
        // badge, chip and CSV alike. The datatype columns still show the two names.
        AttributeMapStatus.Renamed => "Same",
        AttributeMapStatus.Moved => "Moved",
        AttributeMapStatus.OnlyInLeft => "Only in A",
        AttributeMapStatus.OnlyInRight => "Only in B",
        _ => "",
    };

    /// <summary>Quotes a field only when RFC 4180 requires it, doubling any embedded quote.</summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attribute-map" : cleaned;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
