using System;
using Macros;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Unit tests for <see cref="AccessibilityHelpers"/>. The helpers are pure strings so the
/// XAML accessibility properties can be validated without a UI Automation host.
/// </summary>
public sealed class AccessibilityHelpersTests
{
    private static MacroEntry MakeEntry(string name = "Greeting") =>
        new(name, MacroScope.Global, $"X:\\fake\\{name}.csx", 3, DateTimeOffset.UtcNow, 256, Array.Empty<TriggerBinding>());

    [Fact]
    public void FormatMacroAutomationName_NullEntry_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => AccessibilityHelpers.FormatMacroAutomationName(null!, "Manual", "5m"));
    }

    [Fact]
    public void FormatMacroAutomationName_HappyPath_ReturnsCompoundLabel()
    {
        var result = AccessibilityHelpers.FormatMacroAutomationName(MakeEntry("Format-On-Save"), "Manual", "5m");

        Assert.Equal("Macro Format-On-Save, Manual, modified 5m", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatMacroAutomationName_BlankTriggers_FallsBackToReadableLiteral(string? triggers)
    {
        var result = AccessibilityHelpers.FormatMacroAutomationName(MakeEntry(), triggers, "yesterday");

        Assert.Equal("Macro Greeting, no triggers, modified yesterday", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void FormatMacroAutomationName_BlankModified_FallsBackToReadableLiteral(string? modified)
    {
        var result = AccessibilityHelpers.FormatMacroAutomationName(MakeEntry(), "Manual", modified);

        Assert.Equal("Macro Greeting, Manual, modified unknown date", result);
    }

    [Fact]
    public void MacroAutomationNameFormat_HasThreeOrderedPlaceholders()
    {
        Assert.Equal("Macro {0}, {1}, modified {2}", AccessibilityHelpers.MacroAutomationNameFormat);
    }
}
