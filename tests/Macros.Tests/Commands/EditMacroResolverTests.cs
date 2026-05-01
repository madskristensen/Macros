using System;
using Macros.Commands.Context;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="EditMacroResolver"/> — the pure path-resolution helper
/// that can be tested without the VS host.
/// </summary>
public sealed class EditMacroResolverTests
{
    private static MacroEntry MakeDescriptor(string filePath = @"C:\macros\foo.csx") =>
        new("foo", MacroScope.Global, filePath, 0, DateTimeOffset.UtcNow, 42,
            System.Array.Empty<Macros.Engine.Triggers.TriggerBinding>());

    [Fact]
    public void Resolve_NullDescriptor_ReturnsFalseWithMessage()
    {
        var (ok, path, msg) = EditMacroResolver.Resolve(null, _ => true);

        Assert.False(ok);
        Assert.Equal("", path);
        Assert.Contains("No macro selected", msg);
    }

    [Fact]
    public void Resolve_FileExists_ReturnsTrueWithPath()
    {
        var desc = MakeDescriptor(@"C:\macros\foo.csx");

        var (ok, path, msg) = EditMacroResolver.Resolve(desc, _ => true);

        Assert.True(ok);
        Assert.Equal(@"C:\macros\foo.csx", path);
        Assert.Equal("", msg);
    }

    [Fact]
    public void Resolve_FileMissing_ReturnsFalseWithPath()
    {
        var desc = MakeDescriptor(@"C:\macros\gone.csx");

        var (ok, path, msg) = EditMacroResolver.Resolve(desc, _ => false);

        Assert.False(ok);
        Assert.Equal(@"C:\macros\gone.csx", path);
        Assert.Contains("gone.csx", msg);
        Assert.NotEmpty(msg);
    }

    [Fact]
    public void Resolve_FileExistencePredicateReceivesDescriptorPath()
    {
        const string expected = @"C:\macros\specific.csx";
        string? received = null;
        var desc = MakeDescriptor(expected);

        EditMacroResolver.Resolve(desc, p => { received = p; return true; });

        Assert.Equal(expected, received);
    }
}
