using System.IO;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 contract for the m5-error-handling-audit fix that added
/// <see cref="MacrosPaths.IsValidGlobalFolder(string?)"/> and
/// <see cref="MacrosPaths.ResolveGlobalFolderOrFallback(string?, out bool)"/>.
/// The motivating scenario: a user types a malformed path in
/// Tools → Options → Macros → Global macros folder. Without these helpers, the storage
/// layer crashes at first save with a cryptic <see cref="System.ArgumentException"/>.
/// </summary>
public sealed class MacrosPathsValidationTests
{
    private static readonly string DefaultFolder = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Macros");

    [Fact]
    public void IsValidGlobalFolder_Null_ReturnsTrue()
    {
        // Empty / whitespace is the "use default" sentinel; resolution swaps in %APPDATA%\Macros.
        Assert.True(MacrosPaths.IsValidGlobalFolder(null));
        Assert.True(MacrosPaths.IsValidGlobalFolder(""));
        Assert.True(MacrosPaths.IsValidGlobalFolder("   "));
    }

    [Fact]
    public void IsValidGlobalFolder_ValidAbsolutePath_ReturnsTrue()
    {
        Assert.True(MacrosPaths.IsValidGlobalFolder(@"D:\dev\macros"));
    }

    [Fact]
    public void IsValidGlobalFolder_ValidRelativePath_ReturnsTrue()
    {
        // The helper is purely syntactic; relative paths are still acceptable input
        // (it's the storage layer that decides what to do with them).
        Assert.True(MacrosPaths.IsValidGlobalFolder("relative/path"));
    }

    [Fact]
    public void IsValidGlobalFolder_PathWithInvalidChar_ReturnsFalse()
    {
        // Path.GetInvalidPathChars() includes most C0 control characters. Guarantee one
        // is present so the test is deterministic across platforms.
        var bad = "C:\\bad" + (char)1 + "name";
        Assert.False(MacrosPaths.IsValidGlobalFolder(bad));
    }

    [Fact]
    public void IsValidGlobalFolder_PathWithPipeChar_ReturnsFalse()
    {
        // '|' is in Path.GetInvalidPathChars() on Windows.
        Assert.False(MacrosPaths.IsValidGlobalFolder(@"Q:\bad|name"));
    }

    [Fact]
    public void ResolveGlobalFolderOrFallback_Empty_UsesDefault_NoFallbackFlag()
    {
        var got = MacrosPaths.ResolveGlobalFolderOrFallback("", out bool usedFallback);
        Assert.Equal(DefaultFolder, got);
        Assert.False(usedFallback);
    }

    [Fact]
    public void ResolveGlobalFolderOrFallback_ValidPath_ReturnedVerbatim()
    {
        var got = MacrosPaths.ResolveGlobalFolderOrFallback(@"D:\custom\macros", out bool usedFallback);
        Assert.Equal(@"D:\custom\macros", got);
        Assert.False(usedFallback);
    }

    [Fact]
    public void ResolveGlobalFolderOrFallback_InvalidPath_FallsBackToDefault_AndSignals()
    {
        var bad = @"Q:\invalid|name";
        var got = MacrosPaths.ResolveGlobalFolderOrFallback(bad, out bool usedFallback);
        Assert.Equal(DefaultFolder, got);
        Assert.True(usedFallback);
    }
}
