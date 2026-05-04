using Macros.Options;
using Xunit;

namespace Macros.Tests.Options;

/// <summary>
/// Pins the contract of the per-macro disable list (issue #8) on
/// <see cref="MacrosOptions"/>: path canonicalization (case-insensitive,
/// trailing-separator stripped), idempotent add/remove, and the on-disk
/// semicolon-delimited persistence format.
/// </summary>
public sealed class MacrosOptionsDisabledMacrosTests
{
    [Fact]
    public void IsMacroDisabled_DefaultOptions_ReturnsFalse()
    {
        var opts = new MacrosOptions();
        Assert.False(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
    }

    [Fact]
    public void IsMacroDisabled_NullOrEmpty_ReturnsFalse()
    {
        var opts = new MacrosOptions();
        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);

        Assert.False(opts.IsMacroDisabled(null));
        Assert.False(opts.IsMacroDisabled(""));
    }

    [Fact]
    public void SetMacroDisabled_True_AddsToPersistedList()
    {
        var opts = new MacrosOptions();

        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);

        Assert.True(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        Assert.Contains("foo.csx", opts.DisabledMacros.ToLowerInvariant());
    }

    [Fact]
    public void SetMacroDisabled_False_RemovesFromPersistedList()
    {
        var opts = new MacrosOptions();
        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);

        opts.SetMacroDisabled(@"X:\macros\Foo.csx", false);

        Assert.False(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        Assert.Equal("", opts.DisabledMacros);
    }

    [Fact]
    public void SetMacroDisabled_Twice_IsIdempotent()
    {
        var opts = new MacrosOptions();

        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);
        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);

        Assert.True(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        // Single entry — no duplicate semicolon-separated copies.
        Assert.DoesNotContain(";;", ";" + opts.DisabledMacros + ";");
        Assert.Single(opts.DisabledMacros.Split(';'));
    }

    [Fact]
    public void IsMacroDisabled_IsCaseInsensitive_OnPath()
    {
        var opts = new MacrosOptions();
        opts.SetMacroDisabled(@"X:\Macros\Foo.csx", true);

        Assert.True(opts.IsMacroDisabled(@"x:\macros\foo.csx"));
        Assert.True(opts.IsMacroDisabled(@"X:\MACROS\FOO.CSX"));
    }

    [Fact]
    public void Multiple_DisabledMacros_RoundTrip_Independently()
    {
        var opts = new MacrosOptions();

        opts.SetMacroDisabled(@"X:\macros\Foo.csx", true);
        opts.SetMacroDisabled(@"X:\macros\Bar.csx", true);

        Assert.True(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        Assert.True(opts.IsMacroDisabled(@"X:\macros\Bar.csx"));

        opts.SetMacroDisabled(@"X:\macros\Foo.csx", false);

        Assert.False(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        Assert.True(opts.IsMacroDisabled(@"X:\macros\Bar.csx"));
    }

    [Fact]
    public void DisabledMacros_RawAssignment_IsHonoredByIsMacroDisabled()
    {
        // Simulates a value loaded from the VS settings store on the next session.
        var opts = new MacrosOptions
        {
            DisabledMacros = @"x:\macros\foo.csx;x:\macros\bar.csx",
        };

        Assert.True(opts.IsMacroDisabled(@"X:\macros\Foo.csx"));
        Assert.True(opts.IsMacroDisabled(@"X:\macros\Bar.csx"));
        Assert.False(opts.IsMacroDisabled(@"X:\macros\Baz.csx"));
    }
}
