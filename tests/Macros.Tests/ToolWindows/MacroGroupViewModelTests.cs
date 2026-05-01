using System;
using System.Linq;
using Macros.Engine.Storage;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies the <see cref="MacroGroupViewModel"/> filter projection and visibility rules.
/// </summary>
public sealed class MacroGroupViewModelTests
{
    [Fact]
    public void Constructor_NullOrWhitespaceHeader_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MacroGroupViewModel("", MacroScope.Global));
        Assert.Throws<ArgumentException>(() => new MacroGroupViewModel("   ", MacroScope.Global));
    }

    [Fact]
    public void DefaultState_IsExpanded_IsAvailable_VisibleItemsEmpty()
    {
        var group = new MacroGroupViewModel("Global", MacroScope.Global);

        Assert.True(group.IsExpanded);
        Assert.True(group.IsAvailable);
        Assert.Empty(group.VisibleItems);
        Assert.False(group.IsVisible); // Available but no items → hidden
    }

    [Fact]
    public void AddingItems_PopulatesVisibleItemsByDefault()
    {
        var group = new MacroGroupViewModel("Global", MacroScope.Global);
        var item = MakeItem("Greeting");

        group.Items.Add(item);

        Assert.Single(group.VisibleItems);
        Assert.True(group.IsVisible);
    }

    [Fact]
    public void ApplyFilter_FiltersVisibleItems_LeavesItemsUntouched()
    {
        var group = new MacroGroupViewModel("Global", MacroScope.Global);
        group.Items.Add(MakeItem("alpha"));
        group.Items.Add(MakeItem("beta"));
        group.Items.Add(MakeItem("alphabet"));

        group.ApplyFilter(item => item.Name.StartsWith("alpha", StringComparison.Ordinal));

        Assert.Equal(new[] { "alpha", "alphabet" }, group.VisibleItems.Select(i => i.Name));
        Assert.Equal(3, group.Items.Count);
    }

    [Fact]
    public void ApplyFilter_NullPredicate_ResetsToShowAll()
    {
        var group = new MacroGroupViewModel("Global", MacroScope.Global);
        group.Items.Add(MakeItem("a"));
        group.Items.Add(MakeItem("b"));

        group.ApplyFilter(_ => false);
        Assert.Empty(group.VisibleItems);

        group.ApplyFilter(null);
        Assert.Equal(2, group.VisibleItems.Count);
    }

    [Fact]
    public void IsAvailable_False_HidesGroupRegardlessOfItems()
    {
        var group = new MacroGroupViewModel("Repo", MacroScope.Repo);
        group.Items.Add(MakeItem("x"));
        Assert.True(group.IsVisible);

        group.IsAvailable = false;

        Assert.False(group.IsVisible);
    }

    [Fact]
    public void IsExpanded_PropertyChange_FiresPropertyChanged()
    {
        var group = new MacroGroupViewModel("Global", MacroScope.Global);
        bool fired = false;
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MacroGroupViewModel.IsExpanded))
            {
                fired = true;
            }
        };

        group.IsExpanded = false;

        Assert.True(fired);
    }

    private static MacroItemViewModel MakeItem(string name)
    {
        var descriptor = new MacroDescriptor(name, MacroScope.Global, $"X:\\fake\\{name}.csx", DateTime.UtcNow, 1);
        return new MacroItemViewModel(descriptor, service: null);
    }
}
