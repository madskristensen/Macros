using System;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Verifies <see cref="MacroEntry"/> behaves as a value-equal positional record across
/// every field — including the <see cref="MacroEntry.Triggers"/> collection — so it can
/// safely be compared in change-detection paths (tool-window list diffing, snapshot tests).
/// </summary>
public sealed class MacroEntryTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoEntries_WithSameValues_AreEqual()
    {
        var a = new MacroEntry("Foo", MacroScope.Global, @"C:\m\Foo.csx", 5, Stamp, 100,
            Array.Empty<TriggerBinding>());
        var b = new MacroEntry("Foo", MacroScope.Global, @"C:\m\Foo.csx", 5, Stamp, 100,
            Array.Empty<TriggerBinding>());

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("Foo", "Bar")]
    public void DifferingName_MakesEntriesUnequal(string n1, string n2)
    {
        var a = new MacroEntry(n1, MacroScope.Global, @"C:\m\X.csx", 0, Stamp, 1, Array.Empty<TriggerBinding>());
        var b = new MacroEntry(n2, MacroScope.Global, @"C:\m\X.csx", 0, Stamp, 1, Array.Empty<TriggerBinding>());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferingStepCount_MakesEntriesUnequal()
    {
        var a = new MacroEntry("X", MacroScope.Global, "p", 5, Stamp, 1, Array.Empty<TriggerBinding>());
        var b = new MacroEntry("X", MacroScope.Global, "p", 6, Stamp, 1, Array.Empty<TriggerBinding>());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferingTriggers_MakesEntriesUnequal()
    {
        var a = new MacroEntry("X", MacroScope.Global, "p", 0, Stamp, 1,
            new[] { TriggerBinding.Manual });
        var b = new MacroEntry("X", MacroScope.Global, "p", 0, Stamp, 1,
            Array.Empty<TriggerBinding>());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferingModified_MakesEntriesUnequal()
    {
        var a = new MacroEntry("X", MacroScope.Global, "p", 0, Stamp, 1, Array.Empty<TriggerBinding>());
        var b = new MacroEntry("X", MacroScope.Global, "p", 0, Stamp.AddSeconds(1), 1, Array.Empty<TriggerBinding>());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void With_Expression_ProducesIndependentValue()
    {
        var a = new MacroEntry("X", MacroScope.Global, "p", 1, Stamp, 1, Array.Empty<TriggerBinding>());
        var b = a with { StepCount = 7 };

        Assert.Equal(1, a.StepCount);
        Assert.Equal(7, b.StepCount);
        Assert.NotEqual(a, b);
    }
}
