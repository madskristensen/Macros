using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Macros.Tests.Commands.Context;

/// <summary>
/// Reflection smoke test for <c>Macros.Commands.Context.ManageTriggersContextCommand</c>.
/// Mirrors the M3 <c>ContextCommandSmokeTests</c> pattern: uses
/// <see cref="MetadataLoadContext"/> to read the <c>[Command]</c> attribute without
/// runtime-loading the VS shell.
/// </summary>
public sealed class ManageTriggersContextCommandTests
{
    private const string CommandSetGuid = "1f6e8c4b-2d54-4a76-9c2c-9d8b5a7e1d31";
    private const int CmdCtxManageTriggers = 0x2116;
    private const string TypeFullName = "Macros.Commands.Context.ManageTriggersContextCommand";

    [Fact]
    public void ManageTriggersContextCommand_HasMatchingCommandAttribute()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType(TypeFullName);
        Assert.NotNull(type);

        CustomAttributeData? attr = type!.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Community.VisualStudio.Toolkit.CommandAttribute");
        Assert.NotNull(attr);

        Assert.Equal(2, attr!.ConstructorArguments.Count);
        Assert.Equal(CommandSetGuid, (string)attr.ConstructorArguments[0].Value!);
        Assert.Equal(CmdCtxManageTriggers, (int)attr.ConstructorArguments[1].Value!);
    }

    [Fact]
    public void ManageTriggersContextCommand_DerivesFromBaseCommand()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType(TypeFullName);
        Assert.NotNull(type);

        Type? bt = type!.BaseType;
        Assert.NotNull(bt);
        Assert.StartsWith("BaseCommand", bt!.Name);
        Assert.Equal("Community.VisualStudio.Toolkit", bt.Namespace);
    }

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
    {
        string testBin = AppContext.BaseDirectory;
        string config = new DirectoryInfo(testBin).Parent!.Name;
        string repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));
        string macrosDll = Path.Combine(repoRoot, "src", "Macros", "bin", config, "net48", "Macros.dll");
        if (!File.Exists(macrosDll))
        {
            throw new FileNotFoundException(
                $"Could not locate built Macros.dll at expected path '{macrosDll}'. " +
                "Ensure the Macros VSIX project has been built before running these tests.",
                macrosDll);
        }

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
