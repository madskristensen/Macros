using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Macros.Tests.TestUtilities;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Smoke tests for the M3 context-menu command handlers (<c>Macros.Commands.Context.*</c>).
/// Mirrors the M1-era <see cref="CommandHandlerSmokeTests"/>: uses
/// <see cref="MetadataLoadContext"/> to inspect <c>[Command]</c> attributes without
/// runtime-loading the VS shell dependencies.
/// </summary>
public sealed class ContextCommandSmokeTests
{
    // Mirror of Macros.PackageGuids.CommandSetGuidString.
    private const string CommandSetGuid = "1f6e8c4b-2d54-4a76-9c2c-9d8b5a7e1d31";

    // Mirror of Macros.PackageIds.cmdidMacrosCtx*.
    private const int CmdCtxPlay = 0x2110;
    private const int CmdCtxEdit = 0x2111;
    private const int CmdCtxRename = 0x2112;
    private const int CmdCtxDelete = 0x2113;
    private const int CmdCtxManageTriggers = 0x2116;
    private const int CmdCtxOpenFolder = 0x2210;

    [Theory]
    [InlineData("Macros.Commands.Context.PlayContextCommand", CmdCtxPlay)]
    [InlineData("Macros.Commands.Context.EditContextCommand", CmdCtxEdit)]
    [InlineData("Macros.Commands.Context.RenameContextCommand", CmdCtxRename)]
    [InlineData("Macros.Commands.Context.DeleteContextCommand", CmdCtxDelete)]
    [InlineData("Macros.Commands.Context.ManageTriggersContextCommand", CmdCtxManageTriggers)]
    [InlineData("Macros.Commands.Context.OpenFolderContextCommand", CmdCtxOpenFolder)]
    public void ContextCommandHandler_HasMatchingCommandAttribute(string typeFullName, int expectedCmdId)
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType(typeFullName);
        Assert.NotNull(type);

        CustomAttributeData? attr = type!.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Community.VisualStudio.Toolkit.CommandAttribute");
        Assert.NotNull(attr);

        Assert.Equal(2, attr!.ConstructorArguments.Count);
        var guidArg = (string)attr.ConstructorArguments[0].Value!;
        var idArg = (int)attr.ConstructorArguments[1].Value!;

        Assert.Equal(CommandSetGuid, guidArg);
        Assert.Equal(expectedCmdId, idArg);
    }

    [Fact]
    public void AllContextCommandHandlers_DeriveFromBaseCommand()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        string[] expected =
        {
            "Macros.Commands.Context.PlayContextCommand",
            "Macros.Commands.Context.EditContextCommand",
            "Macros.Commands.Context.RenameContextCommand",
            "Macros.Commands.Context.DeleteContextCommand",
            "Macros.Commands.Context.ManageTriggersContextCommand",
            "Macros.Commands.Context.OpenFolderContextCommand",
        };

        foreach (var name in expected)
        {
            Type? type = macrosAsm.GetType(name);
            Assert.NotNull(type);

            Type? bt = type!.BaseType;
            Assert.NotNull(bt);
            // BaseType through MetadataLoadContext is the closed generic BaseCommand<TSelf>;
            // check by name to avoid resolving the toolkit type's own base chain.
            Assert.StartsWith("BaseCommand", bt!.Name);
            Assert.Equal("Community.VisualStudio.Toolkit", bt.Namespace);
        }
    }

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
    {
        string macrosDll = MacrosAssemblyLocator.Locate();

        string macrosBinDir = Path.GetDirectoryName(macrosDll)!;
        string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

        var paths = new[] { macrosDll }
            .Concat(Directory.EnumerateFiles(macrosBinDir, "*.dll"))
            .Concat(Directory.EnumerateFiles(runtimeDir, "*.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var resolver = new PathAssemblyResolver(paths);
        var ctx = new MetadataLoadContext(resolver);
        macrosAssembly = ctx.LoadFromAssemblyPath(macrosDll);
        return ctx;
    }
}
