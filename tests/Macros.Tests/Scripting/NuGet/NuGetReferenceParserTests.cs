using System;
using System.Linq;
using Macros.Engine.Scripting.NuGet;
using Xunit;

namespace Macros.Tests.Scripting.NuGet;

/// <summary>
/// Pure-function tests for the <c>#r "nuget: ..."</c> directive parser. The parser is the
/// outermost surface of the NuGet feature; bugs here would silently swallow user intent or
/// allow malformed strings to flow into the resolver, so the contract is tested
/// exhaustively.
/// </summary>
public sealed class NuGetReferenceParserTests
{
    [Fact]
    public void Extract_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(NuGetReferenceParser.Extract(null));
        Assert.Empty(NuGetReferenceParser.Extract(""));
    }

    [Fact]
    public void Extract_NoDirectives_ReturnsEmpty()
    {
        const string src = """
            // a comment
            await TypeAsync("hello");
            """;

        Assert.Empty(NuGetReferenceParser.Extract(src));
    }

    [Theory]
    [InlineData("#r \"nuget: Newtonsoft.Json, 13.0.3\"")]
    [InlineData("#r \"nuget:Newtonsoft.Json,13.0.3\"")]
    [InlineData("  #r   \"nuget : Newtonsoft.Json , 13.0.3 \"   ")]
    [InlineData("#R \"NUGET: Newtonsoft.Json, 13.0.3\"")]
    public void Extract_SingleDirective_ParsesCorrectly(string line)
    {
        var refs = NuGetReferenceParser.Extract(line);
        var single = Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", single.PackageId);
        Assert.Equal("13.0.3", single.Version);
    }

    [Fact]
    public void Extract_MultipleDirectives_PreservesOrder()
    {
        const string src = """
            #r "nuget: A.B, 1.0.0"
            // some comment
            #r "nuget: C.D, 2.0.0"
            #r "nuget: A.B, 1.0.0"
            await TypeAsync("ok");
            """;

        var refs = NuGetReferenceParser.Extract(src);
        Assert.Equal(3, refs.Count);
        Assert.Equal("A.B", refs[0].PackageId);
        Assert.Equal("C.D", refs[1].PackageId);
        Assert.Equal("A.B", refs[2].PackageId);
    }

    [Fact]
    public void Extract_DoesNotMatchPlainAssemblyReference()
    {
        const string src = """
            #r "System.Net.Http"
            #r "C:\\path\\to\\some.dll"
            """;

        Assert.Empty(NuGetReferenceParser.Extract(src));
    }

    [Theory]
    [InlineData("#r \"nuget: , 1.0.0\"")]      // empty id
    [InlineData("#r \"nuget: A.B, \"")]         // empty version
    [InlineData("#r \"nuget: A.B 1.0.0\"")]     // missing comma
    [InlineData("#r \"nuget: A.B,\"")]          // trailing comma only
    public void Extract_RejectsMalformedDirectives(string line)
    {
        Assert.Empty(NuGetReferenceParser.Extract(line));
    }

    [Fact]
    public void TryParseRoslynReference_ParsesInnerForm()
    {
        // Roslyn passes the inside of the quotes to MetadataReferenceResolver.
        Assert.True(NuGetReferenceParser.TryParseRoslynReference("nuget: Newtonsoft.Json, 13.0.3", out var parsed));
        Assert.Equal("Newtonsoft.Json", parsed.PackageId);
        Assert.Equal("13.0.3", parsed.Version);
    }

    [Fact]
    public void TryParseRoslynReference_ParsesFullDirectiveLine()
    {
        Assert.True(NuGetReferenceParser.TryParseRoslynReference("#r \"nuget: Newtonsoft.Json, 13.0.3\"", out var parsed));
        Assert.Equal("Newtonsoft.Json", parsed.PackageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Newtonsoft.Json")]            // not a nuget directive
    [InlineData("System.Net.Http")]
    [InlineData("C:\\some.dll")]
    [InlineData("nuget: ")]
    [InlineData("nuget: id-without-comma")]
    public void TryParseRoslynReference_RejectsNonNuGetForms(string? input)
    {
        Assert.False(NuGetReferenceParser.TryParseRoslynReference(input, out _));
    }

    [Fact]
    public void ComputeCacheKey_OrderInsensitive()
    {
        var a = new[]
        {
            new NuGetReference("A", "1.0"),
            new NuGetReference("B", "2.0"),
        };
        var b = new[]
        {
            new NuGetReference("B", "2.0"),
            new NuGetReference("A", "1.0"),
        };

        Assert.Equal(NuGetReferenceParser.ComputeCacheKey(a), NuGetReferenceParser.ComputeCacheKey(b));
    }

    [Fact]
    public void ComputeCacheKey_CaseInsensitiveOnId_CaseSensitiveOnVersion()
    {
        var lower = new[] { new NuGetReference("newtonsoft.json", "13.0.3") };
        var mixed = new[] { new NuGetReference("Newtonsoft.Json", "13.0.3") };
        Assert.Equal(NuGetReferenceParser.ComputeCacheKey(lower), NuGetReferenceParser.ComputeCacheKey(mixed));

        // NuGet treats versions case-sensitively (e.g. preview suffixes); we mirror that.
        var v1 = new[] { new NuGetReference("X", "1.0.0-Beta") };
        var v2 = new[] { new NuGetReference("X", "1.0.0-beta") };
        Assert.NotEqual(NuGetReferenceParser.ComputeCacheKey(v1), NuGetReferenceParser.ComputeCacheKey(v2));
    }

    [Fact]
    public void ComputeCacheKey_EmptyOrNull_ReturnsSentinel()
    {
        Assert.Equal("empty", NuGetReferenceParser.ComputeCacheKey(null));
        Assert.Equal("empty", NuGetReferenceParser.ComputeCacheKey(Array.Empty<NuGetReference>()));
    }

    [Fact]
    public void IsPathSafe_AcceptsTypicalPackageIds()
    {
        Assert.True(NuGetReferenceParser.IsPathSafe(new NuGetReference("Newtonsoft.Json", "13.0.3")));
        Assert.True(NuGetReferenceParser.IsPathSafe(new NuGetReference("Microsoft.Extensions.Logging", "9.0.0-preview.1")));
    }

    [Theory]
    [InlineData("..\\evil", "1.0.0")]
    [InlineData("X", "../1.0.0")]
    [InlineData("X", "1.0|0")]
    [InlineData("X", "1.0\"0")]
    public void IsPathSafe_RejectsPathInjectionAttempts(string id, string version)
    {
        Assert.False(NuGetReferenceParser.IsPathSafe(new NuGetReference(id, version)));
    }
}
