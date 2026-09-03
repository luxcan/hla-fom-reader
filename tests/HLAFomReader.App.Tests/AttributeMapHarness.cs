using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HLAFomReader.App.ViewModels;
using Xunit;

namespace HLAFomReader.App.Tests;

/// <summary>
/// Picks a class pair on the Attribute data tab, for tests that care about the comparison rather
/// than about the choosing.
/// </summary>
/// <remarks>
/// The tab compares one class of FOM A against one class of FOM B, and neither is chosen for the
/// user: which two classes to lay against each other is the judgement the screen exists to support.
/// So every test that wants rows has to pick, and this is the one place that says how.
/// </remarks>
internal static class AttributeMapHarness
{
    /// <summary>
    /// Chooses the class both FOMs have that carries the most attributes, on both sides, and waits
    /// for the comparison.
    /// </summary>
    /// <param name="map">A tab whose pickers have already been filled by ActivateAsync.</param>
    /// <param name="leafName">
    /// A specific class to prefer, when the test needs one — the class carrying the attribute it is
    /// about. The richest shared class is used when it is null or not found on both sides.
    /// </param>
    internal static void PickSharedClass(AttributeMapViewModel map, string? leafName = null)
    {
        var shared = map.ClassOptionsA
            .Where(a => map.ClassOptionsB.Any(
                b => string.Equals(b.QualifiedName, a.QualifiedName, StringComparison.Ordinal)))
            .ToList();

        Assert.True(shared.Count > 0, "the two FOMs share no object class");

        var chosen =
            (leafName is null
                ? null
                : shared.FirstOrDefault(o => string.Equals(o.LeafName, leafName, StringComparison.Ordinal)))
            ?? shared.OrderByDescending(o => o.AttributeCount).First();

        map.SelectedClassA = chosen;
        map.SelectedClassB = map.ClassOptionsB.First(
            b => string.Equals(b.QualifiedName, chosen.QualifiedName, StringComparison.Ordinal));

        Pump(map.PendingWork);
    }

    /// <summary>
    /// Pumps the dispatcher until the work finishes. A test body runs inside a blocking
    /// <c>Dispatcher.Invoke</c>, so a continuation cannot run unless something drives it.
    /// </summary>
    internal static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted, "The comparison did not finish within 60 seconds.");
        task.GetAwaiter().GetResult();
    }
}
