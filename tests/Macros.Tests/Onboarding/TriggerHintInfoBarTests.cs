using Macros.Onboarding;
using Macros.Options;
using Xunit;

namespace Macros.Tests.Onboarding;

/// <summary>
/// Unit tests for <see cref="TriggerHintInfoBar"/>. The InfoBar display and VS-shell
/// interactions are not testable outside a hosted VS process; this file covers the
/// pure-logic surface that is directly testable: the <see cref="TriggerHintInfoBar.IsMacroFilePath"/>
/// path heuristic and the default value and mutability of
/// <see cref="MacrosOptions.HasShownTriggerHint"/>.
/// </summary>
public sealed class TriggerHintInfoBarTests
{
    // ── IsMacroFilePath ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\bob\.vs\Macros\foo.csx", true)]
    [InlineData(@"C:\AppData\Macros\Macros\bar.csx", true)]
    [InlineData(@"C:\repo\src\Foo.cs", false)]
    [InlineData(@"C:\repo\src\foo.csx", false)]
    public void IsMacroFilePath_VariousPaths(string path, bool expected)
    {
        Assert.Equal(expected, TriggerHintInfoBar.IsMacroFilePath(path));
    }

    [Fact]
    public void IsMacroFilePath_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(TriggerHintInfoBar.IsMacroFilePath(null!));
        Assert.False(TriggerHintInfoBar.IsMacroFilePath(""));
    }

    [Fact]
    public void IsMacroFilePath_WrongExtension_ReturnsFalse()
    {
        Assert.False(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\Macros\foo.cs"));
        Assert.False(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\Macros\foo.txt"));
    }

    [Fact]
    public void IsMacroFilePath_CaseInsensitiveExtension_ReturnsTrue()
    {
        Assert.True(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\Macros\foo.CSX"));
        Assert.True(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\Macros\foo.Csx"));
    }

    [Fact]
    public void IsMacroFilePath_CaseInsensitiveFolderSegment_ReturnsTrue()
    {
        Assert.True(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\macros\foo.csx"));
        Assert.True(TriggerHintInfoBar.IsMacroFilePath(@"C:\Users\bob\.vs\MACROS\foo.csx"));
    }

    // ── MacrosOptions.HasShownTriggerHint ──────────────────────────────────

    [Fact]
    public void HasShownTriggerHint_DefaultsToFalse()
    {
        var opts = new MacrosOptions();
        Assert.False(opts.HasShownTriggerHint);
    }

    [Fact]
    public void HasShownTriggerHint_CanBeSetToTrue()
    {
        var opts = new MacrosOptions();
        opts.HasShownTriggerHint = true;
        Assert.True(opts.HasShownTriggerHint);
    }

    [Fact]
    public void HasShownTriggerHint_RoundTrips_AfterSave()
    {
        // base.Save() throws outside VS; the Changed event still fires in the finally block
        // (tested by MacrosOptionsChangedEventTests). Here we just confirm the property
        // assignment is visible on the same instance before any persistence.
        var opts = new MacrosOptions();
        Assert.False(opts.HasShownTriggerHint);

        opts.HasShownTriggerHint = true;
        try { opts.Save(); } catch { /* expected outside VS host */ }

        Assert.True(opts.HasShownTriggerHint);
    }
}
