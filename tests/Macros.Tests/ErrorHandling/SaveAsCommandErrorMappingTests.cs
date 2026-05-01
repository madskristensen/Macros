using System;
using System.IO;
using Macros.Commands;
using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.ErrorHandling;

/// <summary>
/// Pins the v1.0 error-message mapping in <see cref="SaveAsCommand.MapSaveError"/>.
/// The mapping replaces raw .NET / OS messages with action-oriented guidance so a
/// permission failure or full-disk failure points the user at a fix instead of dumping
/// a stack-trace excerpt into a MessageBox.
/// </summary>
public sealed class SaveAsCommandErrorMappingTests
{
    [Fact]
    public void MapSaveError_UnauthorizedAccess_RepoScope_SuggestsGlobalFallback()
    {
        var ex = new UnauthorizedAccessException("Access to the path is denied.");
        var (title, message) = SaveAsCommand.MapSaveError(ex, "MyMacro", MacroScope.Repo);

        Assert.Equal("Save failed — access denied", title);
        Assert.Contains("MyMacro", message);
        Assert.Contains("Repo", message);
        Assert.Contains("Global", message);
    }

    [Fact]
    public void MapSaveError_UnauthorizedAccess_GlobalScope_PointsAtOptionsPage()
    {
        var ex = new UnauthorizedAccessException("Access to the path is denied.");
        var (title, message) = SaveAsCommand.MapSaveError(ex, "MyMacro", MacroScope.Global);

        Assert.Equal("Save failed — access denied", title);
        Assert.Contains("Options", message);
        Assert.Contains("Global", message);
    }

    [Fact]
    public void MapSaveError_DirectoryNotFound_DistinctTitle()
    {
        var ex = new DirectoryNotFoundException("Could not find a part of the path.");
        var (title, message) = SaveAsCommand.MapSaveError(ex, "X", MacroScope.Global);

        Assert.Equal("Save failed — folder not found", title);
        Assert.Contains("Options", message);
    }

    [Fact]
    public void MapSaveError_PathTooLong_AdvisesShorterName()
    {
        var ex = new PathTooLongException();
        var (title, message) = SaveAsCommand.MapSaveError(ex, "X", MacroScope.Global);

        Assert.Equal("Save failed — path too long", title);
        Assert.Contains("shorter", message);
    }

    [Fact]
    public void MapSaveError_IOException_DiskFullStyle_SurfacesUnderlyingMessage()
    {
        // Disk full surfaces as IOException with platform message; the mapper preserves it.
        var ex = new IOException("There is not enough space on the disk.");
        var (title, message) = SaveAsCommand.MapSaveError(ex, "MyMacro", MacroScope.Global);

        Assert.Equal("Save failed — I/O error", title);
        Assert.Contains("MyMacro", message);
        Assert.Contains("not enough space", message);
    }

    [Fact]
    public void MapSaveError_InvalidOperation_PassesThroughMessage()
    {
        // Storage layer's own contract violations carry user-facing messages already.
        var ex = new InvalidOperationException("Macro already exists: MyMacro");
        var (title, message) = SaveAsCommand.MapSaveError(ex, "MyMacro", MacroScope.Global);

        Assert.Equal("Save failed", title);
        Assert.Equal("Macro already exists: MyMacro", message);
    }
}
