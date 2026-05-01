using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Macros.Tests.TestUtilities;
using Xunit;

namespace Macros.Tests;

/// <summary>
/// Smoke tests that verify each M1 command handler is decorated with the expected
/// <c>Community.VisualStudio.Toolkit.CommandAttribute</c> binding it to the right cmdid in the
/// shared command set GUID.
/// </summary>
/// <remarks>
/// <para>
/// The Macros VSIX assembly transitively depends on Microsoft.VisualStudio.Shell, which can't
/// be runtime-loaded outside a hosted VS process. We use <see cref="MetadataLoadContext"/> to
/// inspect attributes at the metadata level (no JIT, no type construction) so these tests stay
/// pure unit tests with no VS shell required.
/// </para>
/// <para>
/// Constants are hard-coded here rather than referencing <c>Macros.PackageIds</c> /
/// <c>Macros.PackageGuids</c> because those types live in the VSIX project and are
/// <see langword="internal"/>. The tests will fail loudly if either side drifts.
/// </para>
/// </remarks>
public sealed class CommandHandlerSmokeTests
{
    // Mirror of Macros.PackageGuids.CommandSetGuidString.
    private const string CommandSetGuid = "1f6e8c4b-2d54-4a76-9c2c-9d8b5a7e1d31";

    // Mirror of Macros.PackageIds command IDs.
    private const int CmdRecord = 0x0100;
    private const int CmdStop = 0x0101;
    private const int CmdPlayLast = 0x0102;
    private const int CmdSaveAs = 0x0104;
    private const int CmdShowWindow = 0x0105;
    private const int CmdDelete = 0x0106;
    private const int CmdRename = 0x0107;
    private const int CmdEdit = 0x0108;

    [Theory]
    [InlineData("Macros.Commands.RecordCommand", CmdRecord)]
    [InlineData("Macros.Commands.StopCommand", CmdStop)]
    [InlineData("Macros.Commands.PlayLastCommand", CmdPlayLast)]
    [InlineData("Macros.Commands.ShowToolWindowCommand", CmdShowWindow)]
    [InlineData("Macros.Commands.SaveAsCommand", CmdSaveAs)]
    [InlineData("Macros.Commands.DeleteCommand", CmdDelete)]
    [InlineData("Macros.Commands.RenameCommand", CmdRename)]
    [InlineData("Macros.Commands.EditCommand", CmdEdit)]
    public void CommandHandler_HasMatchingCommandAttribute(string typeFullName, int expectedCmdId)
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        Type? type = macrosAsm.GetType(typeFullName);
        Assert.NotNull(type);

        CustomAttributeData? attr = type!.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Community.VisualStudio.Toolkit.CommandAttribute");
        Assert.NotNull(attr);

        // The two-arg ctor is (string commandGuid, int commandId).
        Assert.Equal(2, attr!.ConstructorArguments.Count);
        var guidArg = (string)attr.ConstructorArguments[0].Value!;
        var idArg = (int)attr.ConstructorArguments[1].Value!;

        Assert.Equal(CommandSetGuid, guidArg);
        Assert.Equal(expectedCmdId, idArg);
    }

    [Fact]
    public void AllM1CommandHandlers_DeriveFromBaseCommand()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        string[] expected =
        {
            "Macros.Commands.RecordCommand",
            "Macros.Commands.StopCommand",
            "Macros.Commands.PlayLastCommand",
            "Macros.Commands.ShowToolWindowCommand",
            "Macros.Commands.SaveAsCommand",
            "Macros.Commands.DeleteCommand",
            "Macros.Commands.RenameCommand",
            "Macros.Commands.EditCommand",
        };

        foreach (var name in expected)
        {
            Type? type = macrosAsm.GetType(name);
            Assert.NotNull(type);

            // BaseType through MetadataLoadContext is the closed generic BaseCommand<TSelf>; check
            // via name to avoid resolving the toolkit type's own base chain.
            Type? bt = type!.BaseType;
            Assert.NotNull(bt);
            Assert.StartsWith("BaseCommand", bt!.Name);
            Assert.Equal("Community.VisualStudio.Toolkit", bt.Namespace);
        }
    }

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
    {
        // The VSIX project is built before this test project (ProjectReference, even though we
        // pass ReferenceOutputAssembly=false). Walk up from the test bin directory to find it.
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
