using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HLAFomReader.App.Converters;

/// <summary>
/// Shared helpers for the converters in this assembly. Every converter is a singleton exposed as
/// <c>Instance</c> so XAML can bind it with <c>{x:Static conv:XConverter.Instance}</c> — no
/// per-view resource dictionary entries, no allocation per binding.
/// </summary>
internal static class ConverterHelpers
{
    /// <summary>True when the converter parameter asks for the inverted result.</summary>
    internal static bool IsInverted(object? parameter) =>
        parameter is string text &&
        (text.Equals("invert", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("inverse", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("not", StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a logical "show" decision onto <see cref="Visibility"/>, honouring inversion.</summary>
    internal static Visibility ToVisibility(bool visible, object? parameter) =>
        (IsInverted(parameter) ? !visible : visible) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// <c>bool</c> to <see cref="Visibility"/>. Pass <c>ConverterParameter=invert</c> to show on false.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly BoolToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // A null or non-boolean source (an unresolved binding, say) counts as false rather than
        // throwing, so a transient DataContext never brings a view down.
        var flag = value is bool b && b;
        return ConverterHelpers.ToVisibility(flag, parameter);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility visibility && visibility == Visibility.Visible;
        return ConverterHelpers.IsInverted(parameter) ? !visible : visible;
    }
}

/// <summary>Non-null values are visible; <c>null</c> collapses. <c>invert</c> flips the test.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly NullToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConverterHelpers.ToVisibility(value is not null, parameter);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(NullToVisibilityConverter)} is one-way.");
}

/// <summary>Non-blank strings are visible; <c>null</c>, empty and whitespace collapse.</summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly StringEmptyToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = value is string text
            ? !string.IsNullOrWhiteSpace(text)
            : value is not null && !string.IsNullOrWhiteSpace(value.ToString());

        return ConverterHelpers.ToVisibility(hasText, parameter);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(StringEmptyToVisibilityConverter)} is one-way.");
}

/// <summary>Negates a boolean. Round-trips, so it is safe on two-way bindings.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly InverseBoolConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>
/// An item count to <see cref="Visibility"/>: zero (or an unusable value) collapses. Also accepts a
/// collection directly. <c>invert</c> shows the "empty state" instead.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly CountToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => l > int.MaxValue ? int.MaxValue : (int)l,
            short s => s,
            ICollection collection => collection.Count,
            _ => 0,
        };

        return ConverterHelpers.ToVisibility(count > 0, parameter);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(CountToVisibilityConverter)} is one-way.");
}

/// <summary>
/// A byte count to a compact, invariant size string — <c>"812 B"</c>, <c>"12.4 KB"</c>,
/// <c>"1.2 MB"</c>. Binary units (1 KB = 1024 B), one decimal above a kilobyte.
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly ByteSizeConverter Instance = new();

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!TryGetBytes(value, out var bytes)) return DependencyProperty.UnsetValue;

        return Format(bytes);
    }

    /// <summary>Formats a byte count. Negative inputs are clamped to zero.</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");

        double size = bytes;
        var unit = 0;

        while (size >= 1024d && unit < Units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.0} {Units[unit]}");
    }

    private static bool TryGetBytes(object? value, out long bytes)
    {
        switch (value)
        {
            case long l: bytes = l; return true;
            case int i: bytes = i; return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d): bytes = (long)d; return true;
            case decimal m: bytes = (long)m; return true;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                bytes = parsed;
                return true;
            default:
                bytes = 0;
                return false;
        }
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(ByteSizeConverter)} is one-way.");
}

/// <summary>
/// A UTC timestamp to local time, rendered <c>yyyy-MM-dd HH:mm</c>. Everything the registry stores
/// is UTC, so a value that arrives <see cref="DateTimeKind.Unspecified"/> is treated as UTC too.
/// </summary>
public sealed class LocalTimeConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly LocalTimeConverter Instance = new();

    /// <summary>Placeholder shown when there is no timestamp.</summary>
    public const string EmptyText = "—";

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return EmptyText;

            case DateTime dateTime:
                return Format(dateTime);

            case DateTimeOffset offset:
                return offset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            case string text when DateTime.TryParse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed):
                return Format(parsed);

            default:
                return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>Converts a stored UTC value to the local-time display string.</summary>
    public static string Format(DateTime utc)
    {
        if (utc == default) return EmptyText;

        var asUtc = utc.Kind switch
        {
            DateTimeKind.Utc => utc,
            DateTimeKind.Local => utc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utc, DateTimeKind.Utc),
        };

        return asUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(LocalTimeConverter)} is one-way.");
}

/// <summary>
/// Renders <c>null</c>, empty and whitespace as an em dash so detail panes stay aligned instead of
/// showing gaps. A non-empty converter parameter overrides the placeholder.
/// </summary>
public sealed class NullOrEmptyToDashConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly NullOrEmptyToDashConverter Instance = new();

    /// <summary>Default placeholder.</summary>
    public const string Dash = "—";

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var placeholder = parameter as string;
        if (string.IsNullOrEmpty(placeholder)) placeholder = Dash;

        var text = value as string ?? value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? placeholder : text;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Two-way editing of a placeholder field: the dash means "still empty".
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) || text == Dash ? null : text;
    }
}

/// <summary>
/// Binds a radio-button group to a single enum property: <c>IsChecked</c> is true when the bound
/// value equals the enum member named by <c>ConverterParameter</c>, and checking a button writes
/// that member back.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    /// <summary>Shared instance for <c>{x:Static}</c> bindings.</summary>
    public static readonly EnumToBoolConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;

        // The parameter is usually the member name as a XAML string, but {x:Static} supplies the
        // enum value itself — accept both.
        if (parameter is string name)
            return value.GetType().IsEnum && string.Equals(value.ToString(), name.Trim(), StringComparison.Ordinal);

        return Equals(value, parameter);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the button being checked writes back; unchecking is the other button's business.
        if (value is not bool isChecked || !isChecked || parameter is null)
            return Binding.DoNothing;

        if (parameter is string name)
        {
            var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (enumType.IsEnum && Enum.TryParse(enumType, name.Trim(), ignoreCase: false, out var parsed))
                return parsed!;

            return DependencyProperty.UnsetValue;
        }

        return parameter;
    }
}
