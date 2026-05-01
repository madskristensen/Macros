using System;
using Macros.Commands.Context;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Tests for the static <see cref="MacroSelectionContext"/> bridge that hands the
/// right-clicked <see cref="MacroEntry"/> from the tool window to the VSCT context-menu
/// command handlers.
/// </summary>
public sealed class MacroSelectionContextTests : IDisposable
{
    public MacroSelectionContextTests()
    {
        // Ensure each test starts from a known state — the property is process-static.
        MacroSelectionContext.Current = null;
    }

    public void Dispose()
    {
        MacroSelectionContext.Current = null;
    }

    [Fact]
    public void Current_DefaultsToNull()
    {
        Assert.Null(MacroSelectionContext.Current);
    }

    [Fact]
    public void Current_RoundTripsAssignment()
    {
        var descriptor = new MacroEntry(
            Name: "MyMacro",
            Scope: MacroScope.Global,
            Path: @"C:\some\path\MyMacro.csx",
            StepCount: 0,
            Modified: new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            SizeBytes: 42,
            Triggers: System.Array.Empty<Macros.Engine.Triggers.TriggerBinding>());

        MacroSelectionContext.Current = descriptor;

        Assert.Same(descriptor, MacroSelectionContext.Current);
    }

    [Fact]
    public void Current_CanBeClearedToNull()
    {
        MacroSelectionContext.Current = new MacroEntry(
            "X", MacroScope.Repo, @"C:\X.csx", 0, DateTimeOffset.UtcNow, 1,
            System.Array.Empty<Macros.Engine.Triggers.TriggerBinding>());

        MacroSelectionContext.Current = null;

        Assert.Null(MacroSelectionContext.Current);
    }
}
