using System;
using System.Linq;
using Community.VisualStudio.Toolkit;
using Macros.Commands.Context;
using Xunit;

namespace Macros.Tests.Commands.Context;

/// <summary>
/// Reflection-based smoke tests for the copy context commands.
/// </summary>
public sealed class CopyContextCommandsTests
{
    [Fact]
    public void CopyToRepoCommand_HasCorrectCommandAttribute()
    {
        var type = typeof(CopyToRepoCommand);
        var attr = type.GetCustomAttributes(typeof(CommandAttribute), inherit: false)
            .Cast<CommandAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(PackageIds.cmdidMacrosCtxCopyToRepo, (int)attr.Id);
    }

    [Fact]
    public void CopyToRepoCommand_InheritsBaseCommand()
    {
        var type = typeof(CopyToRepoCommand);
        var baseType = type.BaseType;

        Assert.NotNull(baseType);
        Assert.True(baseType!.IsGenericType, "CopyToRepoCommand should be BaseCommand<T>");
        Assert.Equal(typeof(BaseCommand<CopyToRepoCommand>), baseType);
    }

    [Fact]
    public void CopyToGlobalCommand_HasCorrectCommandAttribute()
    {
        var type = typeof(CopyToGlobalCommand);
        var attr = type.GetCustomAttributes(typeof(CommandAttribute), inherit: false)
            .Cast<CommandAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(PackageIds.cmdidMacrosCtxCopyToGlobal, (int)attr.Id);
    }

    [Fact]
    public void CopyToGlobalCommand_InheritsBaseCommand()
    {
        var type = typeof(CopyToGlobalCommand);
        var baseType = type.BaseType;

        Assert.NotNull(baseType);
        Assert.True(baseType!.IsGenericType, "CopyToGlobalCommand should be BaseCommand<T>");
        Assert.Equal(typeof(BaseCommand<CopyToGlobalCommand>), baseType);
    }
}
