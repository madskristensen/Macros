using System.IO;
using Macros.Options;
using Xunit;

namespace Macros.Tests.Options;

/// <summary>
/// Unit tests for the trust/block helper methods on <see cref="MacrosOptions"/>.
/// These methods are pure string-manipulation logic — no VS shell required.
/// </summary>
public sealed class TrustedSolutionsTests
{
    private static MacrosOptions Fresh() => new MacrosOptions();

    [Fact]
    public void IsSolutionTrusted_EmptyList_ReturnsFalse()
    {
        var opts = Fresh();
        Assert.False(opts.IsSolutionTrusted(@"C:\Projects\MySolution.sln"));
    }

    [Fact]
    public void TrustSolution_AddsToTrustedSet_IsTrustedReturnsTrue()
    {
        var opts = Fresh();
        opts.TrustSolution(@"C:\Projects\MySolution.sln");
        Assert.True(opts.IsSolutionTrusted(@"C:\Projects\MySolution.sln"));
    }

    [Fact]
    public void IsSolutionTrusted_CanonicalizesPath_MixedCaseAndTrailingSlash()
    {
        var opts = Fresh();
        opts.TrustSolution(@"C:\Projects\MySolution.sln");
        // Different case + trailing separator — must still match.
        Assert.True(opts.IsSolutionTrusted(@"C:\PROJECTS\MYSOLUTION.SLN"));
    }

    [Fact]
    public void BlockSolution_RemovesFromTrusted_MutuallyExclusive()
    {
        var opts = Fresh();
        opts.TrustSolution(@"C:\Projects\MySolution.sln");
        opts.BlockSolution(@"C:\Projects\MySolution.sln");

        Assert.False(opts.IsSolutionTrusted(@"C:\Projects\MySolution.sln"));
        Assert.True(opts.IsSolutionBlocked(@"C:\Projects\MySolution.sln"));
    }

    [Fact]
    public void TrustSolution_RemovesFromBlocked_MutuallyExclusive()
    {
        var opts = Fresh();
        opts.BlockSolution(@"C:\Projects\MySolution.sln");
        opts.TrustSolution(@"C:\Projects\MySolution.sln");

        Assert.True(opts.IsSolutionTrusted(@"C:\Projects\MySolution.sln"));
        Assert.False(opts.IsSolutionBlocked(@"C:\Projects\MySolution.sln"));
    }

    [Fact]
    public void RevokeTrust_RemovesFromTrusted_DoesNotAddToBlocked()
    {
        var opts = Fresh();
        opts.TrustSolution(@"C:\Projects\MySolution.sln");
        opts.RevokeTrust(@"C:\Projects\MySolution.sln");

        Assert.False(opts.IsSolutionTrusted(@"C:\Projects\MySolution.sln"));
        Assert.False(opts.IsSolutionBlocked(@"C:\Projects\MySolution.sln"));
    }

    [Fact]
    public void MultipleSolutions_SemicolonList_ParsesCorrectly()
    {
        var opts = Fresh();
        opts.TrustSolution(@"C:\Alpha\A.sln");
        opts.TrustSolution(@"C:\Beta\B.sln");

        Assert.True(opts.IsSolutionTrusted(@"C:\Alpha\A.sln"));
        Assert.True(opts.IsSolutionTrusted(@"C:\Beta\B.sln"));
        Assert.False(opts.IsSolutionTrusted(@"C:\Gamma\C.sln"));
    }

    [Fact]
    public void IsSolutionTrusted_NullPath_ReturnsFalse()
    {
        var opts = Fresh();
        Assert.False(opts.IsSolutionTrusted(null));
    }

    [Fact]
    public void IsSolutionTrusted_EmptyPath_ReturnsFalse()
    {
        var opts = Fresh();
        Assert.False(opts.IsSolutionTrusted(""));
    }

    [Fact]
    public void IsSolutionBlocked_NullPath_ReturnsFalse()
    {
        var opts = Fresh();
        Assert.False(opts.IsSolutionBlocked(null));
    }
}
