using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        // silently no-op'd. The repro: record a macro, hit Refresh — new macro doesn't
        // appear; only restarting VS shows it.
        //
        // Pure source-level check: walk every .cs file under src/Macros/Commands and
        // collect class names whose source contains the [Command(...) attribute. Then
        // verify each appears as `<TypeName>.InitializeAsync(this)` inside
        // MacrosPackage.RegisterCommandsAsync. Source-level grep is robust enough here —
        // every contributor edits the same files when adding a new command.
        string commandsDir = LocateRepoPath(Path.Combine("src", "Macros", "Commands"));
        var commandClassNames = new List<string>();
        foreach (string file in Directory.EnumerateFiles(commandsDir, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            int attrIdx = text.IndexOf("[Command(PackageGuids.guidMacrosPackageCmdSetString", StringComparison.Ordinal);
            if (attrIdx < 0) continue;

            // Match the class declaration that follows the [Command(...)] attribute, not any
            // type token in surrounding doc comments.
            var classMatch = Regex.Match(text.Substring(attrIdx),
                @"\bclass\s+(?<name>\w+)\b");
            if (classMatch.Success)
                commandClassNames.Add(classMatch.Groups["name"].Value);
        }

        Assert.NotEmpty(commandClassNames);

        string packageSource = File.ReadAllText(LocateRepoPath(Path.Combine("src", "Macros", "MacrosPackage.cs")));
        int regionStart = packageSource.IndexOf("private async Task RegisterCommandsAsync()", StringComparison.Ordinal);
        Assert.True(regionStart > 0, "Could not locate RegisterCommandsAsync method declaration in MacrosPackage source.");

        // Step from "private async Task RegisterCommandsAsync()" past the opening brace,
        // then walk to the matching closing brace by tracking depth.
        int openBrace = packageSource.IndexOf('{', regionStart);
        Assert.True(openBrace > regionStart, "Could not locate opening brace of RegisterCommandsAsync.");

        int depth = 1;
        int i = openBrace + 1;
        while (i < packageSource.Length && depth > 0)
        {
            char c = packageSource[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            i++;
        }
        Assert.Equal(0, depth);

        string body = packageSource.Substring(openBrace, i - openBrace);

        var missing = commandClassNames
            .Where(name => !body.Contains($"{name}.InitializeAsync(this)"))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Command class(es) decorated with [Command] but missing from MacrosPackage.RegisterCommandsAsync: " +
            string.Join(", ", missing));
    }

    private static string LocateRepoPath(string relative, [System.Runtime.CompilerServices.CallerFilePath] string callerFilePath = "")
    {
        // Use CallerFilePath (resolved at compile time) to pin to a known location inside the
        // repo, then walk up to the repo root deterministically. This is robust to CI bin
        // depths (D:\a\…\bin\Release\net48\) where AppContext.BaseDirectory is many levels
        // deep and the previous fixed-iteration walk-up didn't reach the repo root.
        if (!string.IsNullOrEmpty(callerFilePath))
        {
            // tests/Macros.Tests/CommandHandlerSmokeTests.cs → repo root is two parents up.
            string? testDir = Path.GetDirectoryName(callerFilePath);
            string? testsDir = Path.GetDirectoryName(testDir);
            string? repoRoot = Path.GetDirectoryName(testsDir);
            if (!string.IsNullOrEmpty(repoRoot))
            {
                string candidate = Path.Combine(repoRoot!, relative);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
        }

        // Fallback: walk up from AppContext.BaseDirectory. CI bin paths can be ~6 levels
        // below the repo root; allow plenty of headroom.
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 16 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }

        throw new FileNotFoundException($"Could not locate {relative} from test bin directory '{AppContext.BaseDirectory}' or caller file '{callerFilePath}'.");
    }

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
        => MetadataContextFactory.CreateForMacrosVsix(out macrosAssembly);
}
