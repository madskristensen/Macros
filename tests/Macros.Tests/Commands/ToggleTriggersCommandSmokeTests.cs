using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Macros.Tests.TestUtilities;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Smoke test verifying that <c>ToggleTriggersCommand</c> is decorated with the expected
/// <c>CommandAttribute</c> so the toolkit wires it to the correct cmdid.
/// Uses <see cref="MetadataLoadContext"/> to inspect the VSIX assembly without requiring
/// a hosted VS process.
/// </summary>
public sealed class ToggleTriggersCommandSmokeTests
{
    private const string CommandSetGuid = "1f6e8c4b-2d54-4a76-9c2c-9d8b5a7e1d31";
    private const int CmdidMacrosToggleTriggers = 0x0014;

    [Fact]
    public void ToggleTriggersCommand_HasCorrectCommandAttribute()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType("Macros.Commands.ToggleTriggersCommand");
        Assert.NotNull(type);

        CustomAttributeData? attr = type!.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Community.VisualStudio.Toolkit.CommandAttribute");
        Assert.NotNull(attr);

        Assert.Equal(2, attr!.ConstructorArguments.Count);
        var guidArg = (string)attr.ConstructorArguments[0].Value!;
        var idArg = (int)attr.ConstructorArguments[1].Value!;

        Assert.Equal(CommandSetGuid, guidArg);
        Assert.Equal(CmdidMacrosToggleTriggers, idArg);
    }

    [Fact]
    public void ToggleTriggersCommand_DerivesFromBaseCommand()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType("Macros.Commands.ToggleTriggersCommand");
        Assert.NotNull(type);

        Type? bt = type!.BaseType;
        Assert.NotNull(bt);
        Assert.StartsWith("BaseCommand", bt!.Name);
        Assert.Equal("Community.VisualStudio.Toolkit", bt.Namespace);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

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
