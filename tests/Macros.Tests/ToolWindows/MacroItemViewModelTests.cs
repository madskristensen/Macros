using System;
using Macros.Engine.Storage;
using Macros.ToolWindows;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies the per-row helper formatting (<see cref="MacroItemViewModel.FormatRelative"/> and
/// <see cref="MacroItemViewModel.FormatBytes"/>) and that the wrapped descriptor's name /
/// scope are projected without modification.
/// </summary>
public sealed class MacroItemViewModelTests
{
    [Fact]
    public void Constructor_NullDescriptor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MacroItemViewModel(null!, service: null));
    }

    [Fact]
    public void Properties_ProjectFromDescriptor()
    {
        var descriptor = new MacroDescriptor("Greeting", MacroScope.Global, "X:\\fake\\Greeting.csx", DateTime.UtcNow, 256);
        var item = new MacroItemViewModel(descriptor, service: null);

        Assert.Equal("Greeting", item.Name);
        Assert.Equal(MacroScope.Global, item.Scope);
        Assert.Same(descriptor, item.Descriptor);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(150, "2m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(60 * 60 * 5, "5h ago")]
    [InlineData(60 * 60 * 24, "1 day ago")]
    [InlineData(60 * 60 * 24 * 3, "3 days ago")]
    public void FormatRelative_BucketsByAge(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var stamp = now.AddSeconds(-secondsAgo);

        Assert.Equal(expected, MacroItemViewModel.FormatRelative(stamp, now));
    }

    [Fact]
    public void FormatRelative_OlderThanOneWeek_FallsBackToAbsoluteDate()
    {
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var twoWeeks = now.AddDays(-14);

        var formatted = MacroItemViewModel.FormatRelative(twoWeeks, now);

        // Format is yyyy-MM-dd (local) — not asserting the exact local timezone-shifted value
        // here, just that we got a date-shaped string and not the relative bucket.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", formatted);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1024L * 5, "5.0 KB")]
    [InlineData(1024L * 100, "100 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 25, "25 MB")]
    public void FormatBytes_RendersHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, MacroItemViewModel.FormatBytes(bytes));
    }

    [Fact]
    public void PlayCommand_NullService_CannotExecute()
    {
        var descriptor = new MacroDescriptor("X", MacroScope.Global, "p", DateTime.UtcNow, 1);
        var item = new MacroItemViewModel(descriptor, service: null);

        Assert.False(item.PlayCommand.CanExecute(null));
        // Execute should be a no-op when service is null (must not throw).
        item.PlayCommand.Execute(null);
        Assert.Null(item.LastPlayTask);
    }

    [Fact]
    public void CanInvoke_FlippingTo_False_DisablesPlayCommand()
    {
        var descriptor = new MacroDescriptor("X", MacroScope.Global, "p", DateTime.UtcNow, 1);
        var item = new MacroItemViewModel(descriptor, service: null) { CanInvoke = true };

        item.CanInvoke = false;

        Assert.False(item.PlayCommand.CanExecute(null));
    }

    [Fact]
    public void EditRenameDeleteCommands_ArePlaceholders_DisabledUntilContextMenuWave()
    {
        var descriptor = new MacroDescriptor("X", MacroScope.Global, "p", DateTime.UtcNow, 1);
        var item = new MacroItemViewModel(descriptor, service: null);

        Assert.False(item.EditCommand.CanExecute(null));
        Assert.False(item.RenameCommand.CanExecute(null));
        Assert.False(item.DeleteCommand.CanExecute(null));
    }
}
