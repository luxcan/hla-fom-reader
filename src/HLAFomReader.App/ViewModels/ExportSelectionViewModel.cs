using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Reporting;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// The export dialog: which classes, if any, the user wants written out in full alongside the two
/// class hierarchies.
/// </summary>
/// <remarks>
/// <para>
/// The hierarchies answer what shape the model is. They are always written and are not what this
/// screen is about — nothing here can switch them off, and the copy says so, because a dialog that
/// looks as though it governs the whole export while governing only part of it is a dialog people
/// learn to distrust.
/// </para>
/// <para>
/// Ticking nothing is a first-class answer, not an incomplete form. It gives the two-sheet workbook
/// this button produced before there was anything to tick, so the export button behaves as it
/// always did for anyone who does not want the new thing, and <b>Export</b> stays enabled at zero.
/// </para>
/// <para>
/// Both trees are built up front and never rebuilt. Search hides rows rather than replacing them,
/// which is what keeps the ticks: a user filtering to "Aircraft", ticking it, then clearing the
/// search to go and find something else must not come back to an empty selection, and a filter that
/// rebuilds the tree — the way the detail screen's does, where nothing is ticked and it costs
/// nothing — would do exactly that.
/// </para>
/// </remarks>
public sealed class ExportSelectionViewModel : ViewModelBase
{
    private readonly ExportClassNode[] _objectNodes;
    private readonly ExportClassNode[] _interactionNodes;

    private string _searchText = "";
    private int _selectedObjectCount;
    private int _selectedInteractionCount;

    /// <summary>
    /// Held up while a bulk operation runs, so ticking 200 classes retotals once rather than 200
    /// times. Each retotal is a walk of both trees plus six binding updates; at Select all on a real
    /// FOM the difference is visible.
    /// </summary>
    private bool _retotalSuspended;

    /// <summary>Builds the dialog's two trees from a parsed FOM.</summary>
    /// <param name="document">The FOM being exported.</param>
    /// <param name="fomName">Display name of the FOM, shown in the dialog's heading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public ExportSelectionViewModel(FomDocument document, string? fomName = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        FomName = string.IsNullOrWhiteSpace(fomName) ? "this FOM" : fomName!.Trim();

        foreach (var root in document.ObjectClasses)
            ObjectClasses.Add(BuildObjectNode(root, null));

        foreach (var root in document.InteractionClasses)
            InteractionClasses.Add(BuildInteractionNode(root, null));

        // Flattened once. Every count, every filter pass and every expand walks these, and the trees
        // cannot gain or lose a class while the dialog is open.
        _objectNodes = ObjectClasses.SelectMany(n => n.DescendantsAndSelf()).ToArray();
        _interactionNodes = InteractionClasses.SelectMany(n => n.DescendantsAndSelf()).ToArray();

        foreach (var node in AllNodes)
        {
            node.CheckedChanged += OnCheckedChanged;

            // Roots and their immediate children open; anything deeper waits to be asked for. A
            // 200-class FOM expanded whole is a scrollbar, not a tree.
            node.IsExpanded = node.Depth <= 1;
        }

        var any = _objectNodes.Length + _interactionNodes.Length > 0;

        SelectAllCommand = new RelayCommand(() => SetAll(true), () => any);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => SelectedCount > 0);
        ExpandAllCommand = new RelayCommand(() => SetExpansion(true), () => any);
        CollapseAllCommand = new RelayCommand(() => SetExpansion(false), () => any);
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => HasSearchText);
    }

    /// <summary>Display name of the FOM being exported.</summary>
    public string FomName { get; }

    /// <summary>The object class tree, with the roots the document declares.</summary>
    public ObservableCollection<ExportClassNode> ObjectClasses { get; } = new();

    /// <summary>The interaction class tree.</summary>
    public ObservableCollection<ExportClassNode> InteractionClasses { get; } = new();

    /// <summary>False when the FOM declares no object classes, so the pane can say so.</summary>
    public bool HasObjectClasses => ObjectClasses.Count > 0;

    /// <summary>False when the FOM declares no interaction classes.</summary>
    public bool HasInteractionClasses => InteractionClasses.Count > 0;

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Hides rows that match neither the text nor anything under them.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;

            ApplyFilter();
            OnPropertyChanged(nameof(HasSearchText), nameof(SelectAllCaption), nameof(SelectNoneCaption));
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

    /// <summary>Caption of the tick-everything button.</summary>
    /// <remarks>
    /// Changes with the search because the button's reach does. While the trees are filtered it
    /// ticks the matches and nothing else, and a button still reading "Select all" would be
    /// describing something it no longer does.
    /// </remarks>
    public string SelectAllCaption => HasSearchText ? "Select matches" : "Select all";

    /// <summary>Caption of the untick button, which is bounded the same way.</summary>
    public string SelectNoneCaption => HasSearchText ? "Clear matches" : "Clear";

    /// <summary>How many object classes are ticked.</summary>
    public int SelectedObjectCount => _selectedObjectCount;

    /// <summary>How many interaction classes are ticked.</summary>
    public int SelectedInteractionCount => _selectedInteractionCount;

    /// <summary>How many classes are ticked, of both kinds together.</summary>
    public int SelectedCount => _selectedObjectCount + _selectedInteractionCount;

    /// <summary>Caption above the object pane, e.g. "Object classes — 3 of 48 selected".</summary>
    public string ObjectHeading => Heading("Object classes", _selectedObjectCount, _objectNodes.Length);

    /// <summary>Caption above the interaction pane.</summary>
    public string InteractionHeading => Heading("Interaction classes", _selectedInteractionCount, _interactionNodes.Length);

    /// <summary>
    /// What the workbook will contain, said in full, so the user can check it before committing.
    /// </summary>
    /// <remarks>
    /// Names the hierarchy tabs even when nothing is ticked. The commonest way to misread this
    /// dialog is as a filter — "I ticked three classes, so I get three classes" — and a summary that
    /// went quiet at zero would leave that reading standing.
    /// </remarks>
    public string Summary
    {
        get
        {
            var tabs = new List<string> { "both class hierarchies" };

            if (SelectedObjectCount > 0)
                tabs.Add($"the attributes of {Plural(SelectedObjectCount, "object class", "object classes")}");

            if (SelectedInteractionCount > 0)
                tabs.Add($"the parameters of {Plural(SelectedInteractionCount, "interaction class", "interaction classes")}");

            var sheets = 2
                + (SelectedObjectCount > 0 ? 1 : 0)
                + (SelectedInteractionCount > 0 ? 1 : 0);

            return $"The workbook will hold {Join(tabs)} — {sheets} tabs.";
        }
    }

    /// <summary>The ticked classes, in the form the exporter takes.</summary>
    public ClassExportSelection ToSelection() =>
        new(Names(_objectNodes), Names(_interactionNodes));

    private IEnumerable<ExportClassNode> AllNodes => _objectNodes.Concat(_interactionNodes);

    private static IEnumerable<string> Names(IEnumerable<ExportClassNode> nodes) =>
        nodes.Where(n => n.IsSelected).Select(n => n.QualifiedName);

    private static string Heading(string kind, int selected, int total) =>
        total == 0 ? kind
        : selected == 0 ? $"{kind} — {total}"
        : $"{kind} — {selected} of {total} selected";

    private static string Plural(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count} {many}";

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0]
        : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private void OnCheckedChanged(object? sender, EventArgs e) => Retotal();

    private void Retotal()
    {
        if (_retotalSuspended) return;

        _selectedObjectCount = _objectNodes.Count(n => n.IsSelected);
        _selectedInteractionCount = _interactionNodes.Count(n => n.IsSelected);

        OnPropertyChanged(nameof(SelectedObjectCount), nameof(SelectedInteractionCount),
                          nameof(SelectedCount), nameof(ObjectHeading),
                          nameof(InteractionHeading), nameof(Summary));
    }

    /// <summary>Ticks or unticks everything the user can currently see.</summary>
    /// <remarks>
    /// <para>
    /// Bounded by the search, which is the whole point. Filtering a 214-class FOM down to the 18
    /// that mention "Aircraft" and pressing a button captioned <b>Select all</b> must not tick the
    /// other 196 — the user would have no way to see it had happened, and would find out from a
    /// workbook with 214 tabs' worth of rows in it. The captions change with the search so the
    /// button never claims more than it does.
    /// </para>
    /// <para>
    /// With no search on, every node is visible and this is the plain "everything" it reads as.
    /// </para>
    /// </remarks>
    private void SetAll(bool value)
    {
        _retotalSuspended = true;
        try
        {
            foreach (var node in AllNodes)
                if (node.IsMatch) node.SetSelectedAlone(value);

            foreach (var root in ObjectClasses.Concat(InteractionClasses))
                root.RefreshIndicators();
        }
        finally
        {
            _retotalSuspended = false;
        }

        Retotal();
    }

    private void SetExpansion(bool value)
    {
        foreach (var node in AllNodes)
            if (node.HasChildren) node.IsExpanded = value;
    }

    /// <summary>
    /// Shows the classes whose name matches, everything under a match, and every ancestor of one so
    /// the path down stays walkable.
    /// </summary>
    /// <remarks>
    /// A matching class brings its whole subtree, which mirrors the detail screen's search: having
    /// found <c>Aircraft</c>, a user wants to see what hangs off it, not only the row that matched.
    /// Matches are expanded so the hit is on screen rather than folded away inside a collapsed
    /// ancestor.
    /// </remarks>
    private void ApplyFilter()
    {
        var needle = _searchText.Trim();

        foreach (var root in ObjectClasses.Concat(InteractionClasses))
            Filter(root, needle, ancestorMatched: false);
    }

    private static bool Filter(ExportClassNode node, string needle, bool ancestorMatched)
    {
        var matched = needle.Length == 0 || ancestorMatched || node.Matches(needle);

        var childMatched = false;
        foreach (var child in node.Children)
            childMatched |= Filter(child, needle, matched);

        // A hit and everything under it are the result; an ancestor is only the way to reach it.
        // The two have to be told apart, because the bulk buttons act on the result — searching for
        // "Aircraft" and pressing Select matches must not tick ObjectRoot and BaseEntity, which are
        // on screen only as the path down.
        node.IsMatch = matched;
        node.IsVisible = matched || childMatched;

        if (needle.Length > 0 && childMatched) node.IsExpanded = true;

        return node.IsVisible;
    }

    private static ExportClassNode BuildObjectNode(FomObjectClass objectClass, ExportClassNode? parent)
    {
        var node = new ExportClassNode(
            objectClass.Name,
            Qualified(objectClass),
            $"{FomInheritance.EffectiveAttributeCount(objectClass)} attributes",
            parent);

        foreach (var child in objectClass.Children)
            node.Children.Add(BuildObjectNode(child, node));

        return node;
    }

    private static ExportClassNode BuildInteractionNode(FomInteractionClass interaction, ExportClassNode? parent)
    {
        var node = new ExportClassNode(
            interaction.Name,
            Qualified(interaction),
            $"{FomInheritance.EffectiveParameterCount(interaction)} parameters",
            parent);

        foreach (var child in interaction.Children)
            node.Children.Add(BuildInteractionNode(child, node));

        return node;
    }

    private static string Qualified(FomNode node) =>
        string.IsNullOrWhiteSpace(node.QualifiedName) ? node.Name : node.QualifiedName;
}

/// <summary>One class in the export dialog's tree, with its tick.</summary>
/// <remarks>
/// <para>
/// Every node here is a class somebody can ask for in its own right, which is what makes this
/// different from the usual tri-state tree. In the usual one a parent is a pure aggregate: its box
/// is derived from its children, and "all my children are ticked" is written as "I am ticked". Doing
/// that here would export classes nobody asked for. Tick <c>Aircraft</c> under a
/// <c>PhysicalEntity</c> that has no other subclass and the aggregate rule would tick
/// <c>PhysicalEntity</c> too, and its 40-odd attributes would appear on a tab the user believes
/// holds one class.
/// </para>
/// <para>
/// So a node's tick is its own, and only ever set by ticking that node. What cascades is the
/// gesture, not the state: ticking a class fills its whole branch, unticking it clears the branch,
/// because "give me this and everything under it" is the commonest thing anyone wants from a tree
/// this size. Afterwards each class in that branch stands alone — unticking one child leaves the
/// parent ticked, since the parent really was asked for.
/// </para>
/// <para>
/// The indeterminate bar is therefore an indicator and never a selection: it means "not this class,
/// but something below it". Without it a collapsed branch could hide the whole of a selection, and a
/// user would have no way to see what they had ticked. Clicking a node showing the bar selects it,
/// branch and all, which is the same gesture as clicking an empty one.
/// </para>
/// </remarks>
public sealed class ExportClassNode : ObservableObject
{
    private readonly ExportClassNode? _parent;

    private bool _isSelected;
    private bool _hasSelectedDescendant;
    private bool _isExpanded;
    private bool _isVisible = true;

    internal ExportClassNode(string name, string qualifiedName, string detail, ExportClassNode? parent)
    {
        Name = name;
        QualifiedName = qualifiedName;
        Detail = detail;
        _parent = parent;
    }

    /// <summary>Raised whenever this node's tick moves, so the dialog can retotal.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>Local class name, as written in the FOM.</summary>
    public string Name { get; }

    /// <summary>Dotted path, which is what the selection is recorded under.</summary>
    public string QualifiedName { get; }

    /// <summary>Trailing summary — how many members the class has, inherited ones included.</summary>
    public string Detail { get; }

    /// <summary>Subclasses, in declaration order.</summary>
    public ObservableCollection<ExportClassNode> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;

    /// <summary>Depth below the root, counting a root as 0.</summary>
    public int Depth => _parent is null ? 0 : _parent.Depth + 1;

    /// <summary>
    /// True when this class is ticked, null when it is not but something under it is, false when
    /// neither.
    /// </summary>
    /// <remarks>
    /// Bound two-way to a <see cref="System.Windows.Controls.CheckBox"/> whose <c>IsThreeState</c>
    /// is left off. The property still yields null from here, which is what paints the indeterminate
    /// bar; leaving <c>IsThreeState</c> off is what stops a click cycling <em>into</em> that state,
    /// since "something below me is ticked" is a fact the tree works out, never an answer a user
    /// gives. Anything a click can set means the same thing to the exporter as it does on screen:
    /// only <see cref="IsSelected"/> reaches the workbook.
    /// </remarks>
    public bool? IsChecked
    {
        get => _isSelected ? true : _hasSelectedDescendant ? null : false;
        set => Apply(value == true);
    }

    /// <summary>True when this class itself goes into the workbook.</summary>
    public bool IsSelected => _isSelected;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>False while the search text hides this row.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>
    /// True when this class is part of what the search found, rather than merely on the way to it.
    /// </summary>
    /// <remarks>
    /// A class matches on its own name, and so does everything beneath a match — having found
    /// <c>Aircraft</c>, its subclasses are part of the answer. Its <em>ancestors</em> are not: they
    /// are drawn so the path down stays walkable, and the bulk buttons must leave them alone. With
    /// no search on, every class is a match. Not bindable; the tree's own visibility uses
    /// <see cref="IsVisible"/>.
    /// </remarks>
    internal bool IsMatch { get; set; } = true;

    public IEnumerable<ExportClassNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }

    /// <summary>True when the search text appears in this class's name or qualified name.</summary>
    internal bool Matches(string needle) =>
        Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || QualifiedName.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ticks or unticks this class and everything under it, then repairs the indicators.</summary>
    private void Apply(bool value)
    {
        foreach (var node in DescendantsAndSelf())
            node.SetSelected(value);

        // The subtree first, bottom-up, then the line of ancestors. Both may now be showing — or
        // no longer showing — the bar.
        RefreshSubtree();

        for (var node = _parent; node is not null; node = node._parent)
            node.RefreshFromChildren();
    }

    private void SetSelected(bool value)
    {
        if (_isSelected == value) return;

        _isSelected = value;
        Announce();
    }

    /// <summary>
    /// Ticks this one class without touching its branch, for a caller ticking a set of its own
    /// choosing — the bulk buttons, which are bounded by the search rather than by the tree.
    /// </summary>
    internal void SetSelectedAlone(bool value) => SetSelected(value);

    /// <summary>Re-derives the indicators for this subtree after a bulk change.</summary>
    internal void RefreshIndicators() => RefreshSubtree();

    /// <summary>Re-derives the indicator for this whole subtree; returns whether anything in it is ticked.</summary>
    private bool RefreshSubtree()
    {
        var any = false;
        foreach (var child in Children)
            any |= child.RefreshSubtree();

        if (_hasSelectedDescendant != any)
        {
            _hasSelectedDescendant = any;
            Announce();
        }

        return any || _isSelected;
    }

    /// <summary>Re-derives this node's indicator from its children alone.</summary>
    private void RefreshFromChildren()
    {
        var any = Children.Any(c => c._isSelected || c._hasSelectedDescendant);
        if (_hasSelectedDescendant == any) return;

        _hasSelectedDescendant = any;
        Announce();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(IsChecked), nameof(IsSelected));
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
}
