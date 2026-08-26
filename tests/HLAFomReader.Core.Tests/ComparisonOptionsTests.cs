using System;
using System.Linq;
using System.Reflection;
using HLAFomReader.Core.Comparison;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// <see cref="ComparisonOptions.Matches"/>, which is how a screen tells a result it is still
/// showing from the settings it was produced under.
/// </summary>
public sealed class ComparisonOptionsTests
{
    [Fact]
    public void AFreshPairOfOptionsMatches()
    {
        Assert.True(new ComparisonOptions().Matches(new ComparisonOptions()));
    }

    [Fact]
    public void ACloneMatchesWhatItWasClonedFrom()
    {
        var options = new ComparisonOptions
        {
            Depth = ComparisonDepth.Full,
            IgnoreInexpressibleProperties = true,
            IgnoreCase = true,
        };

        Assert.True(options.Matches(options.Clone()));
    }

    [Fact]
    public void NothingMatchesAbsentOptions()
    {
        Assert.False(new ComparisonOptions().Matches(null));
    }

    /// <summary>
    /// Changing a setting and changing it back leaves the two matching again.
    /// </summary>
    /// <remarks>
    /// The reason this is a value comparison rather than a dirty flag. A comparison on a pair the
    /// size of RPR takes real time, so a mis-clicked radio button that is immediately corrected has
    /// to leave the result on screen valid rather than force a re-run to get back to where it was.
    /// </remarks>
    [Fact]
    public void ASettingChangedAndChangedBackMatchesOnceMore()
    {
        var ran = new ComparisonOptions().Clone();
        var live = new ComparisonOptions();

        live.Depth = ComparisonDepth.Full;
        Assert.False(live.Matches(ran));

        live.Depth = new ComparisonOptions().Depth;
        Assert.True(live.Matches(ran));
    }

    /// <summary>
    /// Every writable setting is compared, so adding one cannot quietly go unchecked.
    /// </summary>
    /// <remarks>
    /// A setting left out of <see cref="ComparisonOptions.Matches"/> would let a comparison run
    /// under different rules be reported as still current, which is precisely what the method exists
    /// to prevent — and it would fail silently, because the screen would simply never mention it.
    /// Walking the properties by reflection means the omission fails here instead, at the moment the
    /// setting is added.
    /// </remarks>
    [Fact]
    public void EverySettingIsCoveredByMatches()
    {
        var settings = typeof(ComparisonOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .ToList();

        Assert.NotEmpty(settings);

        foreach (var setting in settings)
        {
            var baseline = new ComparisonOptions();
            var changed = new ComparisonOptions();

            setting.SetValue(changed, Different(setting.GetValue(baseline), setting.PropertyType));

            Assert.False(
                changed.Matches(baseline),
                $"ComparisonOptions.Matches ignores {setting.Name}. Add it, or a comparison run "
                + "under a different value for it will be reported as still current.");
        }
    }

    /// <summary>Any value of the right type other than the one given.</summary>
    private static object Different(object? current, Type type)
    {
        if (type == typeof(bool)) return !(bool)current!;

        if (type.IsEnum)
        {
            return Enum.GetValues(type)
                .Cast<object>()
                .First(value => !value.Equals(current));
        }

        throw new NotSupportedException(
            $"ComparisonOptions has gained a {type.Name} setting. Teach this test how to vary one.");
    }
}
