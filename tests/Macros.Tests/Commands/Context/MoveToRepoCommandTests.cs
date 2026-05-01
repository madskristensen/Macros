using System;
using System.Linq;
using Community.VisualStudio.Toolkit;
using Macros.Commands.Context;
using Xunit;

namespace Macros.Tests.Commands.Context;

/// <summary>
/// Reflection-based smoke tests for <see cref="MoveToRepoCommand"/>.
/// Verifies structural invariants — correct <c>[Command]</c> attribute and
/// <c>BaseCommand&lt;T&gt;</c> ancestry — without requiring a VS host.
/// </summary>
public sealed class MoveToRepoCommandTests
{
    [Fact]
    public void MoveToRepoCommand_HasCorrectCommandAttribute()
    {
        var type = typeof(MoveToRepoCommand);
        var attr = type.GetCustomAttributes(typeof(CommandAttribute), inherit: false)
            .Cast<CommandAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attr);
        Assert.Equal(PackageIds.cmdidMacrosCtxMoveToRepo, (int)attr.Id);
    }

    [Fact]
    public void MoveToRepoCommand_InheritsBaseCommand()
    {
        var type = typeof(MoveToRepoCommand);
        var baseType = type.BaseType;

        Assert.NotNull(baseType);
        Assert.True(baseType!.IsGenericType, "MoveToRepoCommand should be BaseCommand<T>");
        Assert.Equal(typeof(BaseCommand<MoveToRepoCommand>), baseType);
    }
}
