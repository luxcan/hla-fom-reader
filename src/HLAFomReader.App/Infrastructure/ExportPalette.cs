using System.Windows;
using System.Windows.Media;
using HLAFomReader.Core.Reporting;

namespace HLAFomReader.App.Infrastructure;

/// <summary>
/// Turns the theme the app is currently wearing into the colours an exported workbook is painted
/// with.
/// </summary>
/// <remarks>
/// <para>
/// Read from the live resource dictionary rather than switched on <see cref="ThemeManager.Current"/>
/// against two hard-coded palettes. The theme files are the one place these colours are decided, and
/// a second copy over here would be a copy that goes stale the first time somebody adjusts a shade
/// and has no reason to think an Excel exporter cares.
/// </para>
/// <para>
/// The keys are the raw <c>Color</c> resources rather than the <c>SolidColorBrush</c> ones, because
/// a brush would have to be unwrapped and might not be solid. Both dictionaries declare the same
/// set — <c>AppBackgroundColor</c> is the marker key <see cref="ThemeManager"/> recognises a theme
/// by, so its presence is already load-bearing.
/// </para>
/// </remarks>
public static class ExportPalette
{
    /// <summary>The palette for the theme in force right now.</summary>
    /// <remarks>
    /// Not the table-header colours the app paints its own grids with. Those are
    /// <c>SurfaceAltBackground</c> against <c>SurfaceBackground</c> — a three-percent step that
    /// reads as a header on screen only because the app draws a rule under it. On a spreadsheet's
    /// white it would be invisible, and a header the user cannot see is not a header they asked
    /// for. The window chrome is the theme's most recognisable colour and is the one that survives
    /// the trip.
    /// </remarks>
    public static XlsxPalette Current() => new(
        HeaderFill: Hex("AppBackgroundColor", XlsxPalette.Default.HeaderFill),
        HeaderText: Hex("TextPrimaryColor", XlsxPalette.Default.HeaderText),
        GridLine: Hex("ControlBorderColor", XlsxPalette.Default.GridLine));

    /// <summary>
    /// One theme colour as <c>AARRGGBB</c>, or <paramref name="fallback"/> when there is no
    /// application to ask — which is every test that touches this without building a shell.
    /// </summary>
    private static string Hex(string key, string fallback) =>
        Application.Current?.TryFindResource(key) is Color color
            ? $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : fallback;
}
