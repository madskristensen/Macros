using System;
using System.Collections.Generic;
using Macros.Engine.Triggers;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies the pure formatting helpers behind the tool window's "Triggers" column:
/// <see cref="TriggerSummaryFormatter.Summary"/> and
/// <see cref="TriggerSummaryFormatter.Detail"/>.
/// </summary>
public sealed class TriggerSummaryFormatterTests
{
    [Fact]
    public void Summary_NoBindings_ReturnsManual()
    {
        Assert.Equal(TriggerSummaryFormatter.ManualSummary, TriggerSummaryFormatter.Summary(Array.Empty<TriggerBinding>()));
        Assert.Equal(TriggerSummaryFormatter.ManualSummary, TriggerSummaryFormatter.Summary(null));
    }

    [Fact]
    public void Summary_ManualOnly_ReturnsManual()
    {
        var bindings = new[] { TriggerBinding.Manual };
        Assert.Equal(TriggerSummaryFormatter.ManualSummary, TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Summary_SingleVsEvent_ReturnsFirstSegment()
    {
        var bindings = new[] { new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone") };
        Assert.Equal("Build", TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Summary_SingleVsEvent_NoDot_ReturnsFullName()
    {
        var bindings = new[] { new TriggerBinding(TriggerKind.VsEvent, "DocumentSaved") };
        Assert.Equal("DocumentSaved", TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Summary_SingleBeforeCommand_ReturnsBeforePrefix()
    {
        var bindings = new[] { new TriggerBinding(TriggerKind.BeforeCommand, "File.Save") };
        Assert.Equal("Before File.Save", TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Summary_SingleAfterCommand_ReturnsAfterPrefix()
    {
        var bindings = new[] { new TriggerBinding(TriggerKind.AfterCommand, "File.Save") };
        Assert.Equal("After File.Save", TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Summary_ThreeMixed_ReturnsBellGlyphWithCount()
    {
        var bindings = new[]
        {
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"),
            new TriggerBinding(TriggerKind.AfterCommand, "Edit.Paste"),
        };

        var summary = TriggerSummaryFormatter.Summary(bindings);

        Assert.Contains(TriggerSummaryFormatter.BellGlyph, summary);
        Assert.Contains("3", summary);
    }

    [Fact]
    public void Summary_ManualPlusOneEvent_TreatsManualAsNoise()
    {
        // Manual is a structural default; it doesn't count toward the meaningful trigger
        // count so a "Manual + one VsEvent" macro still summarises as the single VsEvent.
        var bindings = new[]
        {
            TriggerBinding.Manual,
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
        };

        Assert.Equal("Build", TriggerSummaryFormatter.Summary(bindings));
    }

    [Fact]
    public void Detail_NoBindings_ExplainsManual()
    {
        Assert.Contains("Manual", TriggerSummaryFormatter.Detail(Array.Empty<TriggerBinding>()));
        Assert.Contains("Manual", TriggerSummaryFormatter.Detail(null));
    }

    [Fact]
    public void Detail_ManualOnly_ExplainsManual()
    {
        Assert.Contains("Manual", TriggerSummaryFormatter.Detail(new[] { TriggerBinding.Manual }));
    }

    [Fact]
    public void Detail_MultipleBindings_RendersOneLineEach()
    {
        var bindings = new[]
        {
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"),
        };

        var detail = TriggerSummaryFormatter.Detail(bindings);

        Assert.Contains("Build.SolutionBuildDone", detail);
        Assert.Contains("File.Save", detail);
        Assert.Contains("\n", detail);
    }

    [Fact]
    public void Detail_VsEventWithFilter_AppendsWhenClause()
    {
        var bindings = new[]
        {
            new TriggerBinding(
                TriggerKind.VsEvent,
                "Document.Saved",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["filename"] = "*.cs" }),
        };

        var detail = TriggerSummaryFormatter.Detail(bindings);

        Assert.Contains("Document.Saved", detail);
        Assert.Contains("when", detail);
        Assert.Contains("filename=*.cs", detail);
    }
}
