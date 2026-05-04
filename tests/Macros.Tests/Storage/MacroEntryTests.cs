using System;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Pin behaviour-relevant aspects of <see cref="MacroEntry"/>: that the with-expression
/// produces an independent copy (used by tool-window list diffing). Auto-generated
/// record equality is the C# compiler's job and isn't re-tested per field here.
/// </summary>
public sealed class MacroEntryTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

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
