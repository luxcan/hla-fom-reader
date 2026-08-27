using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using HLAFomReader.App.Infrastructure;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;
using HLAFomReader.Core.Registry;
using HLAFomReader.Core.Reporting;

namespace HLAFomReader.App.ViewModels;

/// <summary>
/// The FOM detail screen: one registered FOM, opened full width. The class tree runs down the left
/// and the selected element's members fill the right as a table with every column the OMT defines.
/// </summary>
/// <remarks>
/// This is a drill-down, not a replacement for the Registry side pane. The side pane answers "what
/// did the parser understand"; this screen answers "what exactly is in this class", which needs the
/// horizontal room a 392 DIP pane cannot give.
/// </remarks>
public sealed class FomDetailViewModel : ViewModelBase
{
    private readonly IFomRepository _repository;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<FomExplorerNode> _allNodes = Array.Empty<FomExplorerNode>();
    private FomDocument? _document;
    private FomExplorerNode? _selectedNode;
    private string _searchText = "";

    // Built once with the document, so opening a datatype is a dictionary hit rather than a reload.
    private DataTypeResolver? _resolver;

    /// <summary>Opens one registered FOM for inspection.</summary>
    /// <param name="repository">Store the document is read back from.</param>
    /// <param name="dialogs">Used to surface a failed read; the screen stays usable and empty.</param>
    /// <param name="entry">The registry row that was double-clicked.</param>
    public FomDetailViewModel(IFomRepository repository, IDialogService dialogs, FomRegistryEntry entry)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ExpandAllCommand = new RelayCommand(() => SetExpansion(true), () => Tree.Count > 0);
        CollapseAllCommand = new RelayCommand(() => SetExpansion(false), () => Tree.Count > 0);
        ExportHierarchyCommand = new RelayCommand(ExportHierarchy, () => Document is not null);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ShowDataTypeCommand = new RelayCommand<string?>(ShowDataType, CanShowDataType);

        Load();
    }

    /// <summary>
    /// Opens the datatype inspector on a name declared by this FOM — from the datatype tree, or from
    /// the DataType column of whichever class is selected.
    /// </summary>
    public RelayCommand<string?> ShowDataTypeCommand { get; }

    /// <summary>Raised by the Back button; the shell returns to the Registry screen.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The registry row this screen was opened from.</summary>
    public FomRegistryEntry Entry { get; }

    /// <summary>The rehydrated document, or null when it could not be read.</summary>
    public FomDocument? Document
    {
        get => _document;
        private set => SetProperty(ref _document, value);
    }

    /// <summary>Section, class, datatype and group nodes, filtered by <see cref="SearchText"/>.</summary>
    public ObservableCollection<FomExplorerNode> Tree { get; } = new();

    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }

    /// <summary>Writes the two class hierarchies to an Excel workbook.</summary>
    public RelayCommand ExportHierarchyCommand { get; }

    public RelayCommand CloseCommand { get; }

    public string Title => Entry.DisplayName;

    /// <summary>Standard, version, file name and the headline counts, joined with middle dots.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Entry.StandardDisplayName };

            if (!string.IsNullOrWhiteSpace(Entry.Version))
            {
                var version = Entry.Version!.Trim();
                parts.Add(version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}");
            }

            if (!string.IsNullOrWhiteSpace(Entry.FileName)) parts.Add(Entry.FileName);

            parts.Add($"{Entry.ObjectClassCount} classes");
            parts.Add($"{Entry.AttributeCount} attributes");
            parts.Add($"{Entry.InteractionClassCount} interactions");
            parts.Add($"{Entry.ParameterCount} parameters");
            parts.Add($"{Entry.DataTypeCount} datatypes");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The node whose members fill the table. Driven from the tree through
    /// <see cref="FomExplorerNode.IsSelected"/>, which is why the view needs no code-behind.
    /// </summary>
    public FomExplorerNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            var previous = _selectedNode;
            if (!SetProperty(ref _selectedNode, value)) return;

            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;

            OnPropertyChanged(nameof(Members), nameof(Properties), nameof(HasSelection),
                              nameof(HasMembers), nameof(MemberSummary));
        }
    }

    /// <summary>Attributes, parameters, fields, enumerators or alternatives of the selected node.</summary>
    public IReadOnlyList<FomMemberRow> Members => SelectedNode?.Members ?? Array.Empty<FomMemberRow>();

    /// <summary>The selected node's own properties, as name/value pairs.</summary>
    public IReadOnlyList<PropertyRow> Properties => SelectedNode?.Properties ?? Array.Empty<PropertyRow>();

    /// <summary>Free-text filter over node names and their members; rebuilds the tree.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;

            OnPropertyChanged(nameof(HasSearchText));
            RebuildTree();
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);
    public bool HasSelection => SelectedNode is not null;
    public bool HasMembers => Members.Count > 0;
    public bool IsEmpty => Tree.Count == 0;

    /// <summary>Trailing count on the detail header, e.g. "14 attributes". Blank with no members.</summary>
    public string MemberSummary
    {
        get
        {
            var members = Members;
            if (members.Count == 0) return "";

            var kind = members[0].Kind;
            var summary = $"{members.Count} {(members.Count == 1 ? kind : Plural(kind))}";

            // Say how the total splits, because a class that declares nothing can still inherit
            // dozens — and that is the number that matters when mapping data onto it.
            var inherited = members.Count(m => m.IsInherited);
            if (inherited == 0) return summary;

            var own = members.Count - inherited;
            return $"{summary} · {own} declared here, {inherited} inherited";
        }
    }

    /// <summary>Explains an empty tree — the reason differs and the user cannot be left guessing.</summary>
    public string EmptyMessage =>
        Document is null ? "This FOM could not be read from the registry database."
        : HasSearchText ? "Nothing matches the current search."
        : "This FOM holds no classes, interactions or datatypes.";

    // ---- loading ----------------------------------------------------------------------------

    /// <summary>
    /// Reads the document back and builds the explorer tree. The read is a handful of indexed
    /// SELECTs against a local SQLite file, so it runs synchronously inside the busy scope — the
    /// same reasoning as <see cref="StoredRowsViewModel"/>: a worker thread would buy a
    /// cancellation and re-entrancy story for work that finishes inside a frame, and the gesture
    /// that opened this screen is already blocking.
    /// </summary>
    private void Load()
    {
        using (BeginBusy($"Opening {Entry.DisplayName}…"))
        {
            try
            {
                var document = _repository.LoadDocument(Entry.Id);
                Document = document;
                _resolver = new DataTypeResolver(document);
                _allNodes = FomExplorerNode.Build(document);
            }
            catch (Exception ex)
            {
                // An unreadable entry leaves the screen empty rather than taking the shell down.
                Document = null;
                _resolver = null;
                _allNodes = Array.Empty<FomExplorerNode>();

                _dialogs.ShowError("Open FOM",
                    $"\"{Entry.DisplayName}\" could not be read from the registry database.\n\n{ex.Message}");
            }
        }

        RebuildTree();
    }

    /// <summary>Refills <see cref="Tree"/> from the built nodes, honouring the current search.</summary>
    private void RebuildTree()
    {
        var previous = SelectedNode;

        foreach (var node in Tree)
            Detach(node);

        Tree.Clear();

        // Selection lives on the nodes themselves, so clear it everywhere before restoring it —
        // otherwise a node left over from the previous filter still reads as selected.
        foreach (var node in _allNodes.SelectMany(n => n.DescendantsAndSelf()))
            node.IsSelected = false;

        var needle = SearchText.Trim();

        foreach (var root in _allNodes)
        {
            var node = needle.Length == 0 ? root : FomExplorerNode.Filter(root, needle);
            if (node is not null) Tree.Add(node);
        }

        foreach (var node in Tree)
            Attach(node);

        SelectedNode = previous is null ? null : FindEquivalent(previous);

        OnPropertyChanged(nameof(IsEmpty), nameof(EmptyMessage));
        ExpandAllCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Finds the node in the current tree standing for the same model object, so the selection
    /// survives a filter change. Section and group nodes have no model, so they match on name.
    /// </summary>
    private FomExplorerNode? FindEquivalent(FomExplorerNode node) =>
        Tree.SelectMany(n => n.DescendantsAndSelf())
            .FirstOrDefault(candidate => node.Model is not null
                ? ReferenceEquals(candidate.Model, node.Model)
                : candidate.Model is null
                  && string.Equals(candidate.Name, node.Name, StringComparison.Ordinal)
                  && string.Equals(candidate.Kind, node.Kind, StringComparison.Ordinal));

    private void Attach(FomExplorerNode node)
    {
        node.Selected += OnNodeSelected;
        foreach (var child in node.Children)
            Attach(child);
    }

    private void Detach(FomExplorerNode node)
    {
        node.Selected -= OnNodeSelected;
        foreach (var child in node.Children)
            Detach(child);
    }

    private void OnNodeSelected(object? sender, EventArgs e)
    {
        if (sender is FomExplorerNode node) SelectedNode = node;
    }

    private void SetExpansion(bool expanded)
    {
        foreach (var node in Tree.SelectMany(n => n.DescendantsAndSelf()))
            node.IsExpanded = expanded;
    }

    // ---- clipboard --------------------------------------------------------------------------

    // ---- the datatype inspector ---------------------------------------------------------

    /// <summary>True once the document is open and there is a name to look up.</summary>
    private bool CanShowDataType(string? dataTypeName) =>
        _resolver is not null && !string.IsNullOrWhiteSpace(dataTypeName);

    /// <summary>
    /// Reads one datatype back out of this FOM: what it is declared as, and what it can carry.
    /// </summary>
    /// <remarks>
    /// The same inspector the attribute map opens, on one document instead of a pair. The side label
    /// names the FOM rather than "FOM A", because on this screen there is no other side to be the
    /// other one.
    /// </remarks>
    private void ShowDataType(string? dataTypeName)
    {
        if (!CanShowDataType(dataTypeName)) return;

        try
        {
            var detail = _resolver!.Explain(dataTypeName);

            _dialogs.ShowDataTypeDetail(new DataTypeDetailViewModel(
                detail,
                sideLabel: Entry.DisplayName,
                fomLabel: Entry.StandardBadge,
                attributeName: SelectedNode?.Name ?? Entry.DisplayName));
        }
        catch (Exception ex)
        {
            // Explain is documented never to throw on content, so anything here is a bug rather than
            // a malformed FOM. Say so instead of taking the shell down.
            _dialogs.ShowError("Datatype", $"'{dataTypeName}' could not be read.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Writes this FOM's object and interaction class trees to an Excel workbook, one sheet each,
    /// plus a sheet of members for any class the user picked on the way through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaced a "copy the visible table" button. Copying answered a question nobody was
    /// asking — the member table is already on screen — whereas the shape of the class tree is the
    /// thing people take away and work through, and it is the one view a window cannot show whole.
    /// </para>
    /// <para>
    /// Two dialogs, picker first: what to export is the decision, where to put it is the
    /// confirmation. Cancelling either abandons the export and writes nothing, but only the picker
    /// can be cancelled without having chosen anything yet, which is why it comes first.
    /// </para>
    /// <para>
    /// A picker that returns an empty selection is not a cancel. It means the user looked at the
    /// classes on offer and wanted none of them, which is the two-sheet workbook this button
    /// produced before there was anything to pick — so it goes ahead.
    /// </para>
    /// </remarks>
    private void ExportHierarchy()
    {
        var document = Document;
        if (document is null) return;

        var selection = _dialogs.RequestExportSelection(new ExportSelectionViewModel(document, Entry.DisplayName));
        if (selection is null) return;

        var path = _dialogs.SaveFile(
            "Export class hierarchy",
            "Excel workbook|*.xlsx|All files|*.*",
            $"{Sanitize(Entry.DisplayName)}-class-hierarchy.xlsx",
            "xlsx");

        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // Painted in whichever theme is on screen, so the sheet and the app it came out of look
            // like the same piece of work.
            ClassHierarchyExporter.Export(document, path, selection, ExportPalette.Current());

            var objects = document.ObjectClasses.Sum(c => c.DescendantsAndSelf().Count());
            var interactions = document.InteractionClasses.Sum(c => c.DescendantsAndSelf().Count());

            StatusMessage = $"Class hierarchy written to {Path.GetFileName(path)}";
            _dialogs.ShowInfo("Export complete",
                $"{objects} object class{(objects == 1 ? "" : "es")} and {interactions} " +
                $"interaction class{(interactions == 1 ? "" : "es")} written to:\n\n{path}" +
                MembersExported(selection));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    /// <summary>
    /// The sentence the completion message adds when the user asked for member sheets as well.
    /// </summary>
    /// <remarks>
    /// Silent for an empty selection, so anyone who does not use the picker sees exactly the message
    /// this export has always shown.
    /// </remarks>
    private static string MembersExported(ClassExportSelection selection)
    {
        if (selection.IsEmpty) return "";

        var parts = new List<string>(2);

        if (selection.ObjectClasses.Count > 0)
            parts.Add($"{selection.ObjectClasses.Count} object class{(selection.ObjectClasses.Count == 1 ? "" : "es")}");

        if (selection.InteractionClasses.Count > 0)
            parts.Add($"{selection.InteractionClasses.Count} interaction class{(selection.InteractionClasses.Count == 1 ? "" : "es")}");

        return $"\n\nMembers of {string.Join(" and ", parts)} were written to their own tabs.";
    }

    /// <summary>Makes a FOM's display name safe to use as a file name.</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "fom" : cleaned;
    }

    private static string Plural(string kind) =>
        kind.EndsWith("s", StringComparison.Ordinal) ? kind : kind + "s";
}

/// <summary>
/// A node in the full-width explorer tree. Richer than <see cref="FomTreeNode"/>: it carries the
/// model object it stands for, the member rows that fill the table beside it, and its own
/// properties, so selecting a node is enough to draw the whole right-hand pane.
/// </summary>
public sealed class FomExplorerNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public FomExplorerNode(string name, string kind, string? detail = null, object? model = null)
    {
        Name = name;
        Kind = kind;
        Detail = detail;
        Model = model;
    }

    /// <summary>
    /// Raised when this node becomes the selected one. A <see cref="System.Windows.Controls.TreeView"/>
    /// has no settable SelectedItem, so the view binds each container's IsSelected two-way and the
    /// view model listens here — which keeps the selection out of the code-behind.
    /// </summary>
    public event EventHandler? Selected;

    public string Name { get; }

    /// <summary>Short category label shown as a muted chip, e.g. "class", "interaction", "record".</summary>
    public string Kind { get; }

    /// <summary>Trailing summary, e.g. the sharing and the attribute count.</summary>
    public string? Detail { get; }

    /// <summary>
    /// The model element this node stands for — a <see cref="FomObjectClass"/>,
    /// <see cref="FomInteractionClass"/>, datatype, dimension and so on. Null for the section and
    /// group headers, which stand for nothing in the document.
    /// </summary>
    public object? Model { get; }

    /// <summary>
    /// True when this node IS a declared datatype, so its name opens the inspector.
    /// </summary>
    /// <remarks>
    /// Keyed on the model the node was built from rather than on its <see cref="Kind"/> string. The
    /// six datatype families each carry their own kind — "basic", "simple", "enumerated" and so on —
    /// so matching on the string means six literals that a seventh family would silently not join,
    /// and that a renamed kind would silently break. The model is the fact; the kind is a label for
    /// the icon.
    /// <para>
    /// Deliberately false for a dimension or a tag, which merely REFERENCE a datatype. Their name is
    /// not a type name, so opening the inspector on it would answer "declared in no datatype table".
    /// </para>
    /// </remarks>
    public bool IsDataType =>
        Model is BasicDataType or SimpleDataType or EnumeratedDataType
              or ArrayDataType or FixedRecordDataType or VariantRecordDataType;

    public ObservableCollection<FomExplorerNode> Children { get; } = new();

    /// <summary>Attributes, parameters, fields, enumerators or alternatives. Empty for a header.</summary>
    public IReadOnlyList<FomMemberRow> Members { get; init; } = Array.Empty<FomMemberRow>();

    /// <summary>The node's own OMT properties, as name/value pairs.</summary>
    public IReadOnlyList<PropertyRow> Properties { get; init; } = Array.Empty<PropertyRow>();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            if (value) Selected?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasMembers => Members.Count > 0;

    public bool HasChildren => Children.Count > 0;

    public IEnumerable<FomExplorerNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }

    private FomExplorerNode Add(FomExplorerNode child)
    {
        Children.Add(child);
        return child;
    }

    // ---- filtering --------------------------------------------------------------------------

    /// <summary>
    /// Returns a copy of <paramref name="node"/> holding everything that matches, or null when
    /// nothing beneath it does. A node matches on its own name or on any of its members, and the
    /// ancestors of a match are kept so the path to it stays visible.
    /// </summary>
    internal static FomExplorerNode? Filter(FomExplorerNode node, string needle)
    {
        // A matching node brings its whole subtree: having found "Aircraft", a user wants to see
        // what hangs off it, not just the row that matched.
        if (node.Matches(needle)) return node.CloneSubtree();

        var kept = new List<FomExplorerNode>();
        foreach (var child in node.Children)
        {
            var match = Filter(child, needle);
            if (match is not null) kept.Add(match);
        }

        if (kept.Count == 0) return null;

        var clone = node.CloneShallow();
        foreach (var child in kept)
            clone.Add(child);

        clone.IsExpanded = true;
        return clone;
    }

    private bool Matches(string needle) =>
        Contains(Name, needle) ||
        Members.Any(m => Contains(m.Name, needle) || Contains(m.DataType, needle));

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private FomExplorerNode CloneShallow() =>
        new(Name, Kind, Detail, Model) { Members = Members, Properties = Properties };

    private FomExplorerNode CloneSubtree()
    {
        var clone = CloneShallow();
        clone.IsExpanded = IsExpanded;

        foreach (var child in Children)
            clone.Add(child.CloneSubtree());

        return clone;
    }

    // ---- building ---------------------------------------------------------------------------

    /// <summary>Builds the whole explorer tree for a parsed document.</summary>
    public static IReadOnlyList<FomExplorerNode> Build(FomDocument document)
    {
        var roots = new List<FomExplorerNode>();

        if (document.ObjectClasses.Count > 0)
        {
            var section = new FomExplorerNode("Object classes", "section",
                $"{document.ObjectClassCount} classes · {document.AttributeCount} attributes");

            foreach (var objectClass in document.ObjectClasses)
                section.Add(BuildObjectClass(objectClass));

            roots.Add(section);
        }

        if (document.InteractionClasses.Count > 0)
        {
            var section = new FomExplorerNode("Interaction classes", "section",
                $"{document.InteractionClassCount} classes · {document.ParameterCount} parameters");

            foreach (var interaction in document.InteractionClasses)
                section.Add(BuildInteractionClass(interaction));

            roots.Add(section);
        }

        if (!document.DataTypes.IsEmpty)
        {
            var section = new FomExplorerNode("Datatypes", "section", $"{document.DataTypeCount} total");
            var types = document.DataTypes;

            AddGroup(section, "Basic data representations",
                types.BasicDataRepresentations.Select(BuildBasicDataType));
            AddGroup(section, "Simple datatypes",
                types.SimpleDataTypes.Select(BuildSimpleDataType));
            AddGroup(section, "Enumerated datatypes",
                types.EnumeratedDataTypes.Select(BuildEnumeratedDataType));
            AddGroup(section, "Array datatypes",
                types.ArrayDataTypes.Select(BuildArrayDataType));
            AddGroup(section, "Fixed record datatypes",
                types.FixedRecordDataTypes.Select(BuildFixedRecordDataType));
            AddGroup(section, "Variant record datatypes",
                types.VariantRecordDataTypes.Select(BuildVariantRecordDataType));

            roots.Add(section);
        }

        AddFlatSection(roots, "Dimensions", document.Dimensions.Select(d =>
            new FomExplorerNode(d.Name, "dimension", Describe(d.DataType, d.UpperBound), d)
            {
                Properties = Rows(
                    ("Data type", d.DataType),
                    ("Upper bound", d.UpperBound),
                    ("Normalization", d.Normalization),
                    ("Value", d.Value),
                    ("Semantics", d.Semantics),
                    ("Notes", d.Notes)),
            }));

        AddFlatSection(roots, "Routing spaces", document.RoutingSpaces.Select(s =>
            new FomExplorerNode(s.Name, "space", Join(s.Dimensions), s)
            {
                Properties = Rows(
                    ("Dimensions", Join(s.Dimensions)),
                    ("Semantics", s.Semantics),
                    ("Notes", s.Notes)),
            }));

        AddFlatSection(roots, "Transportations", document.Transportations.Select(t =>
            new FomExplorerNode(t.Name, "transportation", Describe(t.Reliable, null), t)
            {
                Properties = Rows(
                    ("Reliable", t.Reliable),
                    ("Semantics", t.Semantics),
                    ("Notes", t.Notes)),
            }));

        AddFlatSection(roots, "Synchronizations", document.Synchronizations.Select(s =>
            new FomExplorerNode(s.Name, "synchronization", Describe(s.Capability, s.DataType), s)
            {
                Properties = Rows(
                    ("Capability", s.Capability),
                    ("Data type", s.DataType),
                    ("Semantics", s.Semantics),
                    ("Notes", s.Notes)),
            }));

        AddFlatSection(roots, "Update rates", document.UpdateRates.Select(u =>
            new FomExplorerNode(u.Name, "updateRate", Describe(u.Rate, null), u)
            {
                Properties = Rows(
                    ("Rate", u.Rate),
                    ("Semantics", u.Semantics),
                    ("Notes", u.Notes)),
            }));

        AddFlatSection(roots, "Switches", document.Switches.Select(s =>
            new FomExplorerNode(s.Name, "switch", Describe(s.IsEnabled, s.ResignSwitch), s)
            {
                Properties = Rows(
                    ("Enabled", s.IsEnabled),
                    ("Resign switch", s.ResignSwitch),
                    ("Semantics", s.Semantics),
                    ("Notes", s.Notes)),
            }));

        AddFlatSection(roots, "Tags", document.Tags.Select(t =>
            new FomExplorerNode(t.Name, "tag", Describe(t.DataType, null), t)
            {
                Properties = Rows(
                    ("Data type", t.DataType),
                    ("Semantics", t.Semantics),
                    ("Notes", t.Notes)),
            }));

        AddFlatSection(roots, "Notes", document.Notes.Select(n =>
            new FomExplorerNode(n.Name, "note", Describe(n.Text, null), n)
            {
                Properties = Rows(
                    ("Label", n.Label),
                    ("Text", n.Text),
                    ("Semantics", n.Semantics)),
            }));

        if (!document.Time.IsEmpty)
        {
            var time = document.Time;
            var section = new FomExplorerNode("Time representation", "section");

            section.Add(new FomExplorerNode("timeStamp", "time", time.TimeStampDataType, time)
            {
                Properties = Rows(
                    ("Data type", time.TimeStampDataType),
                    ("Semantics", time.TimeStampSemantics)),
            });

            section.Add(new FomExplorerNode("lookahead", "time", time.LookaheadDataType, time)
            {
                Properties = Rows(
                    ("Data type", time.LookaheadDataType),
                    ("Semantics", time.LookaheadSemantics)),
            });

            roots.Add(section);
        }

        SetInitialExpansion(roots, 0);
        return roots;
    }

    private static FomExplorerNode BuildObjectClass(FomObjectClass objectClass)
    {
        var members = EffectiveAttributes(objectClass);
        var declared = objectClass.Attributes.Count;

        // The tree summary quotes the effective count, because that is what a federate sees.
        var count = declared == members.Count
            ? $"{members.Count} attributes"
            : $"{members.Count} attributes ({declared} own)";

        var detail = Describe(objectClass.Sharing, members.Count > 0 ? count : null);

        var node = new FomExplorerNode(objectClass.Name, "class", detail, objectClass)
        {
            Members = members,
            Properties = Rows(
                ("Sharing", objectClass.Sharing),
                ("Semantics", objectClass.Semantics),
                ("Notes", objectClass.Notes),
                ("Qualified name", objectClass.QualifiedName)),
        };

        // Attributes live in the table beside the tree, so only real subclasses hang off a class.
        foreach (var child in objectClass.Children)
            node.Add(BuildObjectClass(child));

        return node;
    }

    private static FomExplorerNode BuildInteractionClass(FomInteractionClass interaction)
    {
        var members = EffectiveParameters(interaction);
        var declared = interaction.Parameters.Count;

        var count = declared == members.Count
            ? $"{members.Count} parameters"
            : $"{members.Count} parameters ({declared} own)";

        var detail = Describe(
            Describe(interaction.Sharing, interaction.Transportation),
            members.Count > 0 ? count : null);

        var node = new FomExplorerNode(interaction.Name, "interaction", detail, interaction)
        {
            Members = members,
            Properties = Rows(
                ("Sharing", interaction.Sharing),
                ("Transportation", interaction.Transportation),
                ("Order", interaction.Order),
                ("Dimensions", Join(interaction.Dimensions)),
                ("Routing space", interaction.RoutingSpace),
                ("Semantics", interaction.Semantics),
                ("Notes", interaction.Notes),
                ("Qualified name", interaction.QualifiedName)),
        };

        foreach (var child in interaction.Children)
            node.Add(BuildInteractionClass(child));

        return node;
    }

    /// <summary>
    /// Every attribute the class actually has: those declared on it, preceded by everything inherited
    /// from its ancestors, walking down from the root so the list reads the way the FOM does.
    /// </summary>
    /// <remarks>
    /// An attribute redeclared on a subclass overrides the inherited one rather than appearing twice.
    /// </remarks>
    private static List<FomMemberRow> EffectiveAttributes(FomObjectClass objectClass) =>
        FomInheritance.EffectiveAttributes(objectClass)
            .Select(e => BuildAttribute(e.Attribute, e.Owner.Name, !ReferenceEquals(e.Owner, objectClass)))
            .ToList();

    /// <summary>The interaction equivalent of <see cref="EffectiveAttributes"/>.</summary>
    private static List<FomMemberRow> EffectiveParameters(FomInteractionClass interaction) =>
        FomInheritance.EffectiveParameters(interaction)
            .Select(e => BuildParameter(e.Parameter, e.Owner.Name, !ReferenceEquals(e.Owner, interaction)))
            .ToList();

    private static FomMemberRow BuildAttribute(FomAttribute attribute, string declaredIn, bool inherited) =>
        new(attribute.Name, "attribute")
        {
            DeclaredIn = declaredIn,
            IsInherited = inherited,
            DataType = attribute.DataType,
            Cardinality = attribute.Cardinality,
            Units = attribute.Units,
            Resolution = attribute.Resolution,
            Accuracy = attribute.Accuracy,
            AccuracyCondition = attribute.AccuracyCondition,
            Transportation = attribute.Transportation,
            Order = attribute.Order,
            Sharing = attribute.Sharing,
            Ownership = attribute.Ownership,
            UpdateType = attribute.UpdateType,
            UpdateCondition = attribute.UpdateCondition,
            Dimensions = Join(attribute.Dimensions),
            RoutingSpace = attribute.RoutingSpace,
            Semantics = attribute.Semantics,
        };

    private static FomMemberRow BuildParameter(FomParameter parameter, string declaredIn, bool inherited) =>
        new(parameter.Name, "parameter")
        {
            DeclaredIn = declaredIn,
            IsInherited = inherited,
            DataType = parameter.DataType,
            Cardinality = parameter.Cardinality,
            Units = parameter.Units,
            Resolution = parameter.Resolution,
            Accuracy = parameter.Accuracy,
            AccuracyCondition = parameter.AccuracyCondition,
            Semantics = parameter.Semantics,
        };

    private static FomExplorerNode BuildBasicDataType(BasicDataType type) =>
        new(type.Name, "basic", Describe(type.Size, type.Interpretation), type)
        {
            Properties = Rows(
                ("Size", type.Size),
                ("Interpretation", type.Interpretation),
                ("Endian", type.Endian),
                ("Encoding", type.Encoding),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static FomExplorerNode BuildSimpleDataType(SimpleDataType type) =>
        new(type.Name, "simple", Describe(type.Representation, type.Units), type)
        {
            Properties = Rows(
                ("Representation", type.Representation),
                ("Units", type.Units),
                ("Resolution", type.Resolution),
                ("Accuracy", type.Accuracy),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static FomExplorerNode BuildEnumeratedDataType(EnumeratedDataType type) =>
        new(type.Name, "enumerated",
            Describe(type.Representation, $"{type.Enumerators.Count} enumerators"), type)
        {
            Members = type.Enumerators.Select(e => new FomMemberRow(e.Name, "enumerator")
            {
                Values = e.Values,
                Semantics = e.Semantics,
            }).ToList(),
            Properties = Rows(
                ("Representation", type.Representation),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static FomExplorerNode BuildArrayDataType(ArrayDataType type) =>
        new(type.Name, "array", Describe(type.DataType, type.Cardinality), type)
        {
            Properties = Rows(
                ("Data type", type.DataType),
                ("Cardinality", type.Cardinality),
                ("Encoding", type.Encoding),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static FomExplorerNode BuildFixedRecordDataType(FixedRecordDataType type) =>
        new(type.Name, "record", Describe($"{type.Fields.Count} fields", type.Encoding), type)
        {
            Members = type.Fields.Select(f => new FomMemberRow(f.Name, "field")
            {
                DataType = f.DataType,
                Semantics = f.Semantics,
            }).ToList(),
            Properties = Rows(
                ("Encoding", type.Encoding),
                ("Include", type.Include),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static FomExplorerNode BuildVariantRecordDataType(VariantRecordDataType type) =>
        new(type.Name, "variant",
            Describe(type.Discriminant, $"{type.Alternatives.Count} alternatives"), type)
        {
            Members = type.Alternatives.Select(a => new FomMemberRow(a.Name, "alternative")
            {
                DataType = a.DataType,
                Values = a.Enumerator,
                Semantics = a.Semantics,
            }).ToList(),
            Properties = Rows(
                ("Discriminant", type.Discriminant),
                ("Data type", type.DataType),
                ("Encoding", type.Encoding),
                ("Semantics", type.Semantics),
                ("Notes", type.Notes)),
        };

    private static void AddGroup(FomExplorerNode parent, string title, IEnumerable<FomExplorerNode> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var group = parent.Add(new FomExplorerNode(title, "group",
            list.Count.ToString(CultureInfo.InvariantCulture)));

        foreach (var item in list)
            group.Add(item);
    }

    private static void AddFlatSection(List<FomExplorerNode> roots, string title,
        IEnumerable<FomExplorerNode> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var section = new FomExplorerNode(title, "section",
            list.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var item in list)
            section.Add(item);

        roots.Add(section);
    }

    /// <summary>
    /// Sections and the level below them open; anything deeper starts closed. A FOM's class tree is
    /// several hundred nodes, and opening all of it hides the shape rather than showing it.
    /// </summary>
    private static void SetInitialExpansion(IEnumerable<FomExplorerNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = depth < 2;
            SetInitialExpansion(node.Children, depth + 1);
        }
    }

    /// <summary>Builds a property table, dropping the pairs this element does not carry.</summary>
    private static IReadOnlyList<PropertyRow> Rows(params (string Property, string? Value)[] pairs)
    {
        var rows = new List<PropertyRow>(pairs.Length);

        foreach (var (property, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new PropertyRow(property, value));
        }

        return rows;
    }

    private static string? Join(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? null : string.Join(", ", values);

    private static string? Describe(string? first, string? second)
    {
        var parts = new[] { first, second }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>
/// One row of the member table: an attribute, parameter, record field, enumerator or variant
/// alternative. Every OMT column the source element can carry has a slot; the rest stay null and
/// read as an em dash.
/// </summary>
public sealed class FomMemberRow
{
    public FomMemberRow(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }

    /// <summary>What this row is: "attribute", "parameter", "field", "enumerator", "alternative".</summary>
    public string Kind { get; }

    public string? DataType { get; init; }
    public string? Cardinality { get; init; }
    public string? Units { get; init; }
    public string? Resolution { get; init; }
    public string? Accuracy { get; init; }
    public string? AccuracyCondition { get; init; }
    public string? Transportation { get; init; }
    public string? Order { get; init; }
    public string? Sharing { get; init; }
    public string? Ownership { get; init; }
    public string? UpdateType { get; init; }
    public string? UpdateCondition { get; init; }

    /// <summary>Associated dimension names, joined with ", ".</summary>
    public string? Dimensions { get; init; }

    /// <summary>HLA 1.3 routing space, when the FED bound one to this element.</summary>
    public string? RoutingSpace { get; init; }

    /// <summary>An enumerator's literal value, or a variant alternative's enumerator.</summary>
    public string? Values { get; init; }

    public string? Semantics { get; init; }

    /// <summary>
    /// The class or interaction that declares this member. Equal to the selected element for its own
    /// members, and the ancestor's name for an inherited one.
    /// </summary>
    public string? DeclaredIn { get; init; }

    /// <summary>
    /// True when this member comes from a superclass rather than being declared here.
    /// </summary>
    /// <remarks>
    /// HLA classes inherit every attribute of their ancestors, so a subclass that declares nothing —
    /// RPR's Aircraft declares none of its 45 — still publishes the full inherited set. Showing only
    /// declared attributes is technically true and useless in practice.
    /// </remarks>
    public bool IsInherited { get; init; }

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>One name/value pair in the selected node's own property table.</summary>
public sealed class PropertyRow
{
    public PropertyRow(string property, string? value)
    {
        Property = property;
        Value = value;
    }

    public string Property { get; }

    public string? Value { get; }

    public override string ToString() => $"{Property}: {Value}";
}
