using System;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.Samples;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies <see cref="MacroItemViewModel"/> projections: the
/// <see cref="MacroItemViewModel.GroupName"/>, <see cref="MacroItemViewModel.StepCount"/>,
/// <see cref="MacroItemViewModel.TriggersSummary"/>, and <see cref="MacroItemViewModel.ModifiedRelative"/>
/// properties.
/// </summary>
public sealed class MacroItemViewModelGroupedTests
{
    [Fact]
    public void GroupName_GlobalScope_ReturnsGlobal()
    {
        var item = MakeItem("X", MacroScope.Global);

        Assert.Equal("Global", item.GroupName);
    }

    [Fact]
    public void GroupName_RepoScope_ReturnsRepo()
    {
        var item = MakeItem("X", MacroScope.Repo);

        Assert.Equal("Repo", item.GroupName);
    }

    [Fact]
    public void SampleRows_ReportSamplesGroup_AndOpenAffordance()
    {
        var template = new SampleTemplate("Insert file header", "Adds a header.", "Macros.Sample.csx");
        var entry = new MacroEntry("Insert file header", MacroScope.Global, "sample:Insert file header", 0, DateTimeOffset.MinValue, 0, new[] { TriggerBinding.Manual });
        var item = new MacroItemViewModel(entry, service: null)
        {
            IsSample = true,
            SampleTemplate = template,
        };

        Assert.Equal("Samples", item.GroupName);
        Assert.Equal("Adds a header.", item.SampleDescription);
        // Samples show only title in the list; TriggersSummary is empty.
        Assert.Equal(string.Empty, item.TriggersSummary);
        Assert.Equal("Adds a header.", item.TriggersDetail);
        Assert.Equal(string.Empty, item.StepCountDisplay);
        Assert.Equal("Adds a header.", item.ItemToolTip);
        Assert.Equal("Open sample", item.PrimaryActionToolTip);
        Assert.Equal("Open sample", item.PrimaryActionAutomationName);
        Assert.True(item.CanPrimaryAction);
        Assert.Contains("Adds a header.", item.AutomationName);
    }

    [Fact]
    public void StepCount_SurfacesFromDescriptor()
    {
        var entry = new MacroEntry(
            "X",
            MacroScope.Global,
            "p",
            StepCount: 42,
            DateTimeOffset.UtcNow,
            1,
            Array.Empty<TriggerBinding>());
        var item = new MacroItemViewModel(entry, service: null);

        Assert.Equal(42, item.StepCount);
        Assert.Equal("42 steps", item.StepCountDisplay);
    }

    [Fact]
    public void TriggersSummary_DerivesFromDescriptor()
    {
        var entry = new MacroEntry(
            "X",
            MacroScope.Global,
            "p",
            0,
            DateTimeOffset.UtcNow,
            1,
            new[] { new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone") });
        var item = new MacroItemViewModel(entry, service: null);

        Assert.Equal("Build", item.TriggersSummary);
        Assert.Contains("Build.SolutionBuildDone", item.TriggersDetail);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m")]
    [InlineData(60 * 5, "5m")]
    [InlineData(60 * 60, "1h")]
    [InlineData(60 * 60 * 5, "5h")]
    [InlineData(60 * 60 * 24, "yesterday")]
    [InlineData(60 * 60 * 47, "yesterday")]
    [InlineData(60 * 60 * 48, "2d")]
    [InlineData(60 * 60 * 24 * 6, "6d")]
    public void FormatModifiedRelative_BucketsByAge(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var stamp = now.AddSeconds(-secondsAgo);

        Assert.Equal(expected, MacroItemViewModel.FormatModifiedRelative(stamp, now));
    }

    [Fact]
    public void FormatModifiedRelative_OlderThanOneWeek_FallsBackToAbsoluteDate()
    {
        var now = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var twoWeeks = now.AddDays(-14);

        var formatted = MacroItemViewModel.FormatModifiedRelative(twoWeeks, now);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", formatted);
    }

    private static MacroItemViewModel MakeItem(string name, MacroScope scope)
    {
        var entry = new MacroEntry(name, scope, $"X:\\fake\\{name}.csx", 0, DateTimeOffset.UtcNow, 1, Array.Empty<TriggerBinding>());
        return new MacroItemViewModel(entry, service: null);
    }
}
