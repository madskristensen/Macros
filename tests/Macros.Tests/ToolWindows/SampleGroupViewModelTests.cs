using System;
using System.Linq;
using System.Threading.Tasks;
using Macros.Samples;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

public sealed class SampleGroupViewModelTests
{
    [Fact]
    public void Constructor_NullOrWhitespaceHeader_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SampleGroupViewModel(""));
        Assert.Throws<ArgumentException>(() => new SampleGroupViewModel("   "));
    }

    [Fact]
    public void DefaultState_IsCollapsed_IsAvailable_VisibleItemsEmpty()
    {
        var group = new SampleGroupViewModel("Samples");

        Assert.False(group.IsExpanded);
        Assert.True(group.IsAvailable);
        Assert.Empty(group.VisibleItems);
        Assert.False(group.IsVisible);
    }

    [Fact]
    public void ApplyFilter_FiltersVisibleItems_LeavesItemsUntouched()
    {
        var group = new SampleGroupViewModel("Samples");
        group.Items.Add(MakeItem("Insert file header", "Adds a header to the current file."));
        group.Items.Add(MakeItem("Format document", "Formats the active document."));
        group.Items.Add(MakeItem("Format selection", "Formats the current selection."));

        group.ApplyFilter(item => item.Name.Contains("Format", StringComparison.Ordinal));

        Assert.Equal(new[] { "Format document", "Format selection" }, group.VisibleItems.Select(i => i.Name));
        Assert.Equal(3, group.Items.Count);
    }

    private static SampleTemplateItemViewModel MakeItem(string name, string description)
    {
        var template = new SampleTemplate(name, description, "// sample");
        return new SampleTemplateItemViewModel(template, (sample, _) => Task.FromResult($"X:\\fake\\{sample.Name}.csx"));
    }
}
