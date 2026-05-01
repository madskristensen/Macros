using System;
using System.IO;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

public sealed class MacrosPathsTests
{
    [Fact]
    public void ResolveGlobalFolder_Null_ReturnsAppDataMacros()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macros");

        Assert.Equal(expected, MacrosPaths.ResolveGlobalFolder(null));
    }

    [Fact]
    public void ResolveGlobalFolder_Empty_ReturnsAppDataMacros()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macros");

        Assert.Equal(expected, MacrosPaths.ResolveGlobalFolder(""));
    }

    [Fact]
    public void ResolveGlobalFolder_Whitespace_ReturnsAppDataMacros()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macros");

        Assert.Equal(expected, MacrosPaths.ResolveGlobalFolder("   "));
    }

    [Fact]
    public void ResolveGlobalFolder_ValidPath_ReturnsAsIs()
    {
        var configured = @"D:\custom\path\to\macros";

        Assert.Equal(configured, MacrosPaths.ResolveGlobalFolder(configured));
    }

    [Fact]
    public void ResolveGlobalFolder_RelativePath_ReturnsAsIs()
    {
        // The helper does NOT normalize — that's intentional, the options page is the
        // right place to validate paths. This test pins that contract.
        var configured = "relative/macros/folder";

        Assert.Equal(configured, MacrosPaths.ResolveGlobalFolder(configured));
    }

    [Fact]
    public void ResolveRepoFolder_NullSolutionDirectory_ReturnsNull()
    {
        Assert.Null(MacrosPaths.ResolveRepoFolder(null, "Macros"));
    }

    [Fact]
    public void ResolveRepoFolder_EmptySolutionDirectory_ReturnsNull()
    {
        Assert.Null(MacrosPaths.ResolveRepoFolder("", "Macros"));
    }

    [Fact]
    public void ResolveRepoFolder_ValidInputs_ProducesDotVsSubpath()
    {
        var got = MacrosPaths.ResolveRepoFolder(@"D:\src\MyApp", "Macros");

        Assert.Equal(@"D:\src\MyApp\.vs\Macros", got);
    }

    [Fact]
    public void ResolveRepoFolder_WhitespaceFolderName_FallsBackToDefault()
    {
        // The defensive fallback prevents producing a broken path that ends in ".vs\".
        var got = MacrosPaths.ResolveRepoFolder(@"D:\src\MyApp", "   ");

        Assert.Equal(@"D:\src\MyApp\.vs\Macros", got);
    }
}
