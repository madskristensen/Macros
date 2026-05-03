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

    [Fact]
    public void RegisterCommandsAsync_InvokesInitializeAsync_ForEveryCommandClass()
    {
        // Regression: RefreshCommand was decorated with [Command] but its InitializeAsync
        // was never called from RegisterCommandsAsync, so the toolbar Refresh button
        // silently no-op'd. Verify every [Command] class is covered going forward.
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);

        var commandClasses = macrosAsm.GetTypes()
            .Where(t => t.GetCustomAttributesData().Any(a =>
                a.AttributeType.FullName == "Community.VisualStudio.Toolkit.CommandAttribute"))
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // Read the IL of MacrosPackage.RegisterCommandsAsync's state machine MoveNext to find
        // every "<TypeName>.InitializeAsync(this)" reference. Async methods compile into a
        // generated state-machine type; locate it via the AsyncStateMachineAttribute pointing
        // at the compiler-generated type, then scan its MoveNext for call/callvirt opcodes.
        Type packageType = macrosAsm.GetType("Macros.MacrosPackage")!;
        MethodInfo? registerMethod = packageType.GetMethod(
            "RegisterCommandsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(registerMethod);

        CustomAttributeData? asmAttr = registerMethod!.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        Assert.NotNull(asmAttr);

        Type stateMachineType = (Type)asmAttr!.ConstructorArguments[0].Value!;
        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        // Body retrieval through MetadataLoadContext. Decode CALL / CALLVIRT to record which
        // declaring types are referenced.
        MethodBody? body = moveNext.GetMethodBody();
        Assert.NotNull(body);

        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);
        byte[] il = body!.GetILAsByteArray()!;

        // Resolve the methods called inside MoveNext by walking the IL stream and looking up
        // tokens for OpCodes.Call / OpCodes.Callvirt. Both are 0x28 / 0x6F respectively.
        var module = stateMachineType.Module;
        for (int i = 0; i < il.Length; )
        {
            byte opcodeByte = il[i];
            int opcodeSize = 1;
            ushort opcodeValue = opcodeByte;

            // 2-byte opcodes are prefixed with 0xFE.
            if (opcodeByte == 0xFE && i + 1 < il.Length)
            {
                opcodeValue = (ushort)((opcodeByte << 8) | il[i + 1]);
                opcodeSize = 2;
            }

            int operandSize = GetOperandSize(opcodeValue);
            int operandStart = i + opcodeSize;

            // 0x28 = call, 0x6F = callvirt — both have a 4-byte method token operand.
            if ((opcodeValue == 0x28 || opcodeValue == 0x6F) && operandStart + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32(il, operandStart);
                try
                {
                    MethodBase? called = module.ResolveMethod(token,
                        stateMachineType.GetGenericArguments(),
                        Array.Empty<Type>());
                    if (called?.Name == "InitializeAsync" && called.DeclaringType?.FullName is string fn)
                    {
                        registeredTypes.Add(fn);
                    }
                }
                catch
                {
                    // Token resolution can fail for instantiations we don't care about; ignore.
                }
            }

            i = operandStart + operandSize;
        }

        // Every class with [Command] must show up among the InitializeAsync targets.
        var missing = commandClasses.Where(c => !registeredTypes.Contains(c)).ToList();
        Assert.True(
            missing.Count == 0,
            $"Command class(es) decorated with [Command] but not registered in MacrosPackage.RegisterCommandsAsync: {string.Join(", ", missing)}");
    }

    private static int GetOperandSize(ushort opcode)
    {
        // Sufficient to handle the opcodes the C# async state machine emits; covers the
        // call/callvirt and ldarg/stloc family. Fallback to 0 (treat as no operand) for
        // anything we don't recognize — the IL walker still advances by opcodeSize, so a
        // misjudged operand at most skips a few bytes ahead and we recover at the next call.
        switch (opcode)
        {
            case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
            case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x14: case 0x15:
            case 0x16: case 0x17: case 0x18: case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D:
            case 0x1E: case 0x25: case 0x26: case 0x2A: case 0x55: case 0x58: case 0x59: case 0x5A:
            case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F: case 0x60: case 0x61: case 0x62:
            case 0x63: case 0x64: case 0x65: case 0x66: case 0x67: case 0x68: case 0x69: case 0x6A:
            case 0x6B: case 0x6C: case 0x6D: case 0x6E:
                return 0;
            case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: case 0x2B: case 0x2C:
            case 0x2D: case 0x2E: case 0x2F: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34:
            case 0x35: case 0x36: case 0x37: case 0x1F:
                return 1;
            case 0x20: case 0x22: case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D:
            case 0x3E: case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45:
            case 0x46: case 0x47: case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D:
            case 0x4E: case 0x4F: case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x6F:
            case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
            case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F:
            case 0x80: case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87:
            case 0x88: case 0x89: case 0x8A: case 0x8B: case 0x8C: case 0x8D: case 0x8E: case 0x8F:
            case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
            case 0x98: case 0x99: case 0x9A: case 0x9B: case 0x9C: case 0x9D: case 0x9E: case 0x9F:
            case 0xA0: case 0xA1: case 0xA2: case 0xA3: case 0xA4: case 0xA5:
            case 0x28: // call
                return 4;
            case 0x21: case 0x23:
                return 8;
            case 0x45: // switch — variable length, not used in async state machine bodies we inspect
                return 4;
            default:
                return 4;
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
