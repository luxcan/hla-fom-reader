using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HLAFomReader.Core.Comparison;
using HLAFomReader.Core.Model;

namespace HLAFomReader.App.Converters;

/// <summary>
/// The palette the diff tree paints with. Kept in code beside the converters (rather than in the
/// theme dictionary) because the brushes must be resolvable from a <c>{x:Static}</c> converter that
/// has no access to a resource lookup. The values match the Precision theme.
/// </summary>
internal static class DiffPalette
{
    /// <summary>Accent hover — additions.</summary>
    internal static readonly SolidColorBrush Added = Frozen("#FF4FC5E4");

    /// <summary>Validation error — removals.</summary>
    internal static readonly SolidColorBrush Removed = Frozen("#FFE56B5E");

    /// <summary>Amber — modifications.</summary>
    internal static readonly SolidColorBrush Modified = Frozen("#FFE0B341");

    /// <summary>Secondary text — unchanged entries recede.</summary>
    internal static readonly SolidColorBrush Unchanged = Frozen("#FF9AA7B5");

    /// <summary>Builds a frozen brush; frozen brushes are shareable across threads and cheap to render.</summary>
    private static SolidColorBrush Frozen(string argb)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb)!);
        brush.Freeze();
        return brush;
    }

    /// <summary>Maps a diff kind onto its brush, defaulting to the unchanged colour.</summary>
    internal static SolidColorBrush For(DiffKind kind) => kind switch
    {
        DiffKind.Added => Added,
        DiffKind.Removed => Removed,
        DiffKind.Modified => Modified,
        _ => Unchanged,
    };
}

/// <summary>
/// <see cref="DiffKind"/> to the brush used for its badge and rail. Also accepts a
/// <see cref="DiffNode"/> so a row can bind to the node itself.
/// </summary>
public sealed class DiffKindToBrushConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly DiffKindToBrushConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffKind kind => DiffPalette.For(kind),
        DiffNode node => DiffPalette.For(node.Kind),
        _ => DiffPalette.Unchanged,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(DiffKindToBrushConverter)} is one-way.");
}

/// <summary>
/// <see cref="DiffKind"/> to the single-character badge glyph: <c>+</c>, <c>−</c> (minus sign, not a
/// hyphen), <c>~</c>, and nothing at all for unchanged.
/// </summary>
public sealed class DiffKindToGlyphConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly DiffKindToGlyphConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "−",
        DiffKind.Modified => "~",
        DiffKind.Unchanged => "",
        DiffNode node => Convert(node.Kind, targetType, parameter, culture),
        _ => "",
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(DiffKindToGlyphConverter)} is one-way.");
}

/// <summary><see cref="DiffKind"/> to its display word.</summary>
public sealed class DiffKindToLabelConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly DiffKindToLabelConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffKind.Added => "Added",
        DiffKind.Removed => "Removed",
        DiffKind.Modified => "Modified",
        DiffKind.Unchanged => "Unchanged",
        DiffNode node => Convert(node.Kind, targetType, parameter, culture),
        _ => "",
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(DiffKindToLabelConverter)} is one-way.");
}

/// <summary>
/// <see cref="DiffCategory"/> to the OMT table name a user would recognise, for the muted caption
/// beside each tree row.
/// </summary>
public sealed class DiffCategoryToLabelConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly DiffCategoryToLabelConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffCategory category => Label(category),
        DiffNode node => Label(node.Category),
        _ => "",
    };

    /// <summary>The friendly name for one category.</summary>
    public static string Label(DiffCategory category) => category switch
    {
        DiffCategory.Root => "Federation",
        DiffCategory.Identification => "Model identification",
        DiffCategory.IdentificationField => "Identification field",
        DiffCategory.ObjectClass => "Object class",
        DiffCategory.Attribute => "Attribute",
        DiffCategory.InteractionClass => "Interaction class",
        DiffCategory.Parameter => "Parameter",
        DiffCategory.DataTypeGroup => "Datatype table",
        DiffCategory.DataType => "Datatype",
        DiffCategory.DataTypeMember => "Datatype member",
        DiffCategory.Dimension => "Dimension",
        DiffCategory.RoutingSpace => "Routing space",
        DiffCategory.Transportation => "Transportation",
        DiffCategory.Synchronization => "Synchronization",
        DiffCategory.UpdateRate => "Update rate",
        DiffCategory.Switch => "Switch",
        DiffCategory.Tag => "Tag",
        DiffCategory.Note => "Note",
        DiffCategory.Time => "Time representation",
        DiffCategory.Section => "Section",
        _ => "",
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(DiffCategoryToLabelConverter)} is one-way.");
}

/// <summary>
/// Highlights differing rows in the property grid: amber for a real difference, secondary text
/// otherwise. Binds either to <see cref="PropertyDiff.IsDifferent"/> or to the
/// <see cref="PropertyDiff"/> itself.
/// </summary>
public sealed class PropertyDiffToBrushConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly PropertyDiffToBrushConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var different = value switch
        {
            bool flag => flag,
            PropertyDiff diff => diff.IsDifferent,
            _ => false,
        };

        return different ? DiffPalette.Modified : DiffPalette.Unchanged;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PropertyDiffToBrushConverter)} is one-way.");
}

/// <summary>
/// <see cref="FomStandard"/> to the short badge shown on registry rows and FOM pickers — the long
/// form does not fit the chip.
/// </summary>
public sealed class FomStandardToBadgeConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly FomStandardToBadgeConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FomStandard standard => Badge(standard),
        _ => Badge(FomStandard.Unknown),
    };

    /// <summary>The badge text for one standard.</summary>
    public static string Badge(FomStandard standard) => standard switch
    {
        FomStandard.Hla13 => "HLA 1.3",
        FomStandard.Ieee1516_2000 => "1516-2000",
        FomStandard.Ieee1516_2010 => "Evolved",
        FomStandard.Ieee1516_2025 => "1516-2025",
        _ => "Unknown",
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(FomStandardToBadgeConverter)} is one-way.");
}
