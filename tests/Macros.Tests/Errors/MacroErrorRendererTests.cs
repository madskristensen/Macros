using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Macros.Tests.Errors;

/// <summary>
/// Pin the public-ish (assembly-internal) shape of <c>Macros.Errors.MacroErrorRenderer</c>
/// without loading the VSIX assembly into the test process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why metadata-only.</b> <c>MacroErrorRenderer</c> talks to <c>VS.Windows</c>,
/// <c>ErrorListProvider</c>, and <c>VS.InfoBar</c>. None of those resolve outside a hosted
/// Visual Studio process — the Macros.dll smoke tests already use
/// <see cref="MetadataLoadContext"/> for the same reason. Runtime coverage of the renderer
/// therefore lives in <c>Macros.IntegrationTests</c>, which spins up an experimental hive
/// with the toolkit's <c>Microsoft.VisualStudio.Sdk.TestFramework.Xunit</c> harness.
/// </para>
/// <para>
/// The unit-level guarantees we *can* enforce here are mostly contract-level: the type
/// exists, the entry point <c>RenderAsync(MacroPlayResult, string)</c> is a static async
/// method returning <see cref="System.Threading.Tasks.Task"/>, and the namespace matches
/// what <c>PlayLastCommand</c> expects to call.
/// </para>
/// </remarks>
public sealed class MacroErrorRendererTests
{
    private const string RendererTypeName = "Macros.Errors.MacroErrorRenderer";
    private const string RenderMethodName = "RenderAsync";

    [Fact]
    public void MacroErrorRenderer_TypeExistsInMacrosAssembly()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType(RendererTypeName);
        Assert.NotNull(type);
        Assert.True(type!.IsAbstract && type.IsSealed, "MacroErrorRenderer should be a static class.");
    }

    [Fact]
    public void RenderAsync_HasExpectedSignature()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type type = macrosAsm.GetType(RendererTypeName)!;

        MethodInfo? method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == RenderMethodName);

        Assert.NotNull(method);
        Assert.Equal("Task", method!.ReturnType.Name);
        Assert.True(method.IsStatic);

        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("MacroPlayResult", parameters[0].ParameterType.Name);
        Assert.Equal("String", parameters[1].ParameterType.Name);
    }

    [Fact]
    public void RenderAsync_LivesInExpectedNamespace()
    {
        // Pin the namespace so PlayLastCommand's `using Macros.Errors;` keeps resolving.
        // If anyone moves the type, this test names the file they should grep for.
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type type = macrosAsm.GetType(RendererTypeName)!;
        Assert.Equal("Macros.Errors", type.Namespace);
    }

    // Runtime exercise of RenderAsync (success → no-op; failure → Output / Error List / InfoBar)
    // requires a hosted VS process. See Macros.IntegrationTests for those cases.

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
    {
        // Mirrors CommandHandlerSmokeTests.CreateMetadataContext — the Macros VSIX is built
        // ahead of this project but its transitive VS Shell dependency can't be runtime-loaded
        // outside a hosted VS process. MetadataLoadContext gives us read-only metadata access.
        string macrosDll = LocateMacrosAssembly();
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

    private static string LocateMacrosAssembly()
    {
        string testBin = AppContext.BaseDirectory;
        string config = new DirectoryInfo(testBin).Parent!.Name;
        string repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));
        string candidate = Path.Combine(repoRoot, "src", "Macros", "bin", config, "net48", "Macros.dll");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Could not locate built Macros.dll at expected path '{candidate}'. " +
                "Ensure the Macros VSIX project has been built before running these tests.",
                candidate);
        }
        return candidate;
    }
}
