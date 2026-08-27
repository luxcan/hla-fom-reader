using System;
using System.Linq;
using HLAFomReader.Core.Model;
using Xunit;

namespace HLAFomReader.Core.Tests;

/// <summary>
/// The one statement of what a class actually has, which the detail screen, the exported hierarchy
/// counts, the exported member rows and the export dialog all read from.
/// </summary>
/// <remarks>
/// Worth its own tests because it is shared. A rule with four callers that disagree is four bugs;
/// a rule with four callers that agree is one place to fix.
/// </remarks>
public sealed class FomInheritanceTests
{
    /// <summary>A class has its own members and every ancestor's, ancestors first.</summary>
    [Fact]
    public void InheritedMembersComeFirstAndInDeclarationOrder()
    {
        var (_, _, soup) = Chain();

        Assert.Equal(
            new[] { "privilegeToDelete", "Price", "Temperature" },
            FomInheritance.EffectiveAttributes(soup).Select(e => e.Attribute.Name).ToArray());

        Assert.Equal(
            new[] { "ObjectRoot", "Meal", "Soup" },
            FomInheritance.EffectiveAttributes(soup).Select(e => e.Owner.Name).ToArray());
    }

    /// <summary>A member redeclared on a subclass stays with the ancestor that introduced it.</summary>
    /// <remarks>
    /// Inheritance in the OMT is by name, so a subclass redeclaring an attribute has not gained one.
    /// Counting it twice would put the exported totals out of step with the exported rows.
    /// </remarks>
    [Fact]
    public void ARedeclaredMemberIsCountedOnceAgainstItsOriginalOwner()
    {
        var (_, meal, soup) = Chain();
        soup.Attributes.Add(new FomAttribute { Name = "Price" });

        var effective = FomInheritance.EffectiveAttributes(soup);

        Assert.Equal(3, effective.Count);
        Assert.Same(meal, Assert.Single(effective, e => e.Attribute.Name == "Price").Owner);
        Assert.Equal(3, FomInheritance.EffectiveAttributeCount(soup));
    }

    /// <summary>
    /// A class that is somehow its own ancestor terminates instead of hanging the app.
    /// </summary>
    /// <remarks>
    /// The class trees are trees, and a document from the parser will be one. A hand-assembled or
    /// malformed one need not be, and this walk runs while a screen is being built — an unguarded
    /// climb up <c>Parent</c> would take the window with it rather than showing a wrong number.
    /// </remarks>
    [Fact]
    public void ACycleInTheParentChainTerminates()
    {
        var (root, meal, soup) = Chain();

        // The malformed part: the root's parent is its own grandchild.
        root.Parent = soup;

        var names = FomInheritance.EffectiveAttributes(soup).Select(e => e.Attribute.Name).ToList();

        Assert.Contains("Temperature", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(meal);
    }

    /// <summary>An interaction with no parameters anywhere in its chain has none.</summary>
    [Fact]
    public void AnInteractionWithNoParametersHasNone()
    {
        var root = new FomInteractionClass { Name = "InteractionRoot", QualifiedName = "InteractionRoot" };
        var order = new FomInteractionClass { Name = "Order", QualifiedName = "InteractionRoot.Order", Parent = root };
        root.Children.Add(order);

        Assert.Empty(FomInheritance.EffectiveParameters(order));
        Assert.Equal(0, FomInheritance.EffectiveParameterCount(order));
    }

    /// <summary>Parameters are inherited the same way attributes are.</summary>
    [Fact]
    public void ParametersAreInheritedTheSameWay()
    {
        var root = new FomInteractionClass { Name = "InteractionRoot", QualifiedName = "InteractionRoot" };
        root.Parameters.Add(new FomParameter { Name = "Sender" });

        var order = new FomInteractionClass { Name = "Order", QualifiedName = "InteractionRoot.Order", Parent = root };
        order.Parameters.Add(new FomParameter { Name = "Table" });
        root.Children.Add(order);

        Assert.Equal(
            new[] { "Sender", "Table" },
            FomInheritance.EffectiveParameters(order).Select(e => e.Parameter.Name).ToArray());
    }

    /// <summary>A three-deep chain with one attribute declared at each level.</summary>
    private static (FomObjectClass Root, FomObjectClass Meal, FomObjectClass Soup) Chain()
    {
        var root = new FomObjectClass { Name = "ObjectRoot", QualifiedName = "ObjectRoot" };
        root.Attributes.Add(new FomAttribute { Name = "privilegeToDelete" });

        var meal = new FomObjectClass { Name = "Meal", QualifiedName = "ObjectRoot.Meal", Parent = root };
        meal.Attributes.Add(new FomAttribute { Name = "Price" });

        var soup = new FomObjectClass { Name = "Soup", QualifiedName = "ObjectRoot.Meal.Soup", Parent = meal };
        soup.Attributes.Add(new FomAttribute { Name = "Temperature" });

        root.Children.Add(meal);
        meal.Children.Add(soup);

        return (root, meal, soup);
    }
}
