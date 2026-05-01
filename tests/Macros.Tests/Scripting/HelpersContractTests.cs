using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Scripting;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Contract tests for the script-globals surface (<see cref="IMacroContext"/>,
/// <see cref="MacroGlobals"/>, <see cref="Helpers"/>). These verify shape only — the helpers
/// touch DTE / IVsUIShell which require a hosted Visual Studio process, so behaviour is
/// validated by integration tests in <c>Macros.IntegrationTests</c>, not here.
/// </summary>
/// <remarks>
/// XML doc presence is intentionally <em>not</em> asserted: <c>Directory.Build.props</c> sets
/// <c>GenerateDocumentationFile=false</c>, so docs aren't materialised into metadata. The
/// docs are nevertheless required by the codegen contract; a dedicated docfx / public-api
/// linter pass (M5) will cover that gap.
/// </remarks>
public sealed class HelpersContractTests
{
    [Fact]
    public void MacroGlobals_IsPublicSealed()
    {
        Type t = typeof(MacroGlobals);
        Assert.True(t.IsPublic, "MacroGlobals must be public.");
        Assert.True(t.IsSealed, "MacroGlobals must be sealed.");
    }

    [Fact]
    public void MacroGlobals_HasExpectedConstructor()
    {
        // Use MetadataLoadContext: the EnvDTE80.DTE2 parameter type lives in
        // Microsoft.VisualStudio.Interop, which the engine references with ExcludeAssets=Runtime,
        // so the runtime DLL isn't deployed to the test bin. Metadata-only inspection works
        // because we never JIT the signature — same trick as CommandHandlerSmokeTests.
        using var ctx = CreateEngineMetadataContext(out Assembly engine);
        Type globals = engine.GetType("Macros.Engine.Scripting.MacroGlobals")!;

        ConstructorInfo ctor = Assert.Single(globals.GetConstructors());
        ParameterInfo[] parameters = ctor.GetParameters();
        Assert.Equal(2, parameters.Length);

        Assert.Equal("dte", parameters[0].Name);
        Assert.Equal("EnvDTE80.DTE2", parameters[0].ParameterType.FullName);

        Assert.Equal("context", parameters[1].Name);
        Assert.Equal("Macros.Engine.Scripting.IMacroContext", parameters[1].ParameterType.FullName);
    }

    [Fact]
    public void MacroGlobals_HasDteAndContextProperties()
    {
        using var ctx = CreateEngineMetadataContext(out Assembly engine);
        Type globals = engine.GetType("Macros.Engine.Scripting.MacroGlobals")!;

        PropertyInfo? dte = globals.GetProperty("DTE", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(dte);
        Assert.Equal("EnvDTE80.DTE2", dte!.PropertyType.FullName);
        Assert.True(dte.CanRead);
        Assert.False(dte.CanWrite, "DTE must be read-only.");

        PropertyInfo? context = globals.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(context);
        Assert.Equal("Macros.Engine.Scripting.IMacroContext", context!.PropertyType.FullName);
        Assert.True(context.CanRead);
        Assert.False(context.CanWrite, "Context must be read-only.");
    }

    [Fact]
    public void MacroGlobals_DoesNotExposeStaticToolkitVS()
    {
        // Decision recorded in MacroGlobals XML doc: the toolkit's `VS` is a static class and
        // cannot be a property; codegen instead emits `using static Community.VisualStudio.Toolkit.VS;`.
        // Lock that decision in so a future contributor doesn't accidentally re-add a `VS` property.
        using var ctx = CreateEngineMetadataContext(out Assembly engine);
        Type globals = engine.GetType("Macros.Engine.Scripting.MacroGlobals")!;
        PropertyInfo? vs = globals.GetProperty("VS");
        Assert.Null(vs);
    }

    private static MetadataLoadContext CreateEngineMetadataContext(out Assembly engineAssembly)
    {
        string engineDll = typeof(MacroGlobals).Assembly.Location;
        string engineBinDir = Path.GetDirectoryName(engineDll)!;
        string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

        // The engine references the VS SDK with ExcludeAssets=Runtime, so EnvDTE / EnvDTE80 /
        // Microsoft.VisualStudio.Interop are *not* deployed to the engine bin. They live in the
        // user's NuGet package cache; pull the net472-compatible copies into the resolver's
        // search path so MetadataLoadContext can resolve DTE2 et al. without runtime-loading
        // them into the host AppDomain.
        string nugetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
        IEnumerable<string> interopDlls = Array.Empty<string>();
        if (Directory.Exists(nugetRoot))
        {
            string[] interopPackages = { "envdte", "envdte80", "envdte90", "envdte100", "microsoft.visualstudio.interop" };
            interopDlls = interopPackages
                .Select(p => Path.Combine(nugetRoot, p))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
                .Where(p => p.IndexOf(@"\lib\net4", StringComparison.OrdinalIgnoreCase) >= 0
                         || p.IndexOf(@"\lib\netstandard2", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // De-dupe by file *name* (last write wins) so we don't hand the resolver two different
        // versions of the same assembly — MetadataLoadContext throws on that.
        var paths = new[] { engineDll }
            .Concat(Directory.EnumerateFiles(engineBinDir, "*.dll"))
            .Concat(Directory.EnumerateFiles(runtimeDir, "*.dll"))
            .Concat(interopDlls)
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var resolver = new PathAssemblyResolver(paths);
        var ctx = new MetadataLoadContext(resolver);
        engineAssembly = ctx.LoadFromAssemblyPath(engineDll);
        return ctx;
    }

    [Fact]
    public void MacroContext_ManualConstructor_ProducesIsManualTrue()
    {
        var ctx = new MacroContext("MyMacro");

        Assert.Equal("MyMacro", ctx.MacroName);
        Assert.Equal("Manual", ctx.TriggerKind);
        Assert.True(ctx.IsManual);
        Assert.True(ctx.Trigger.IsManual);
        Assert.Empty(ctx.Trigger.Payload);
        Assert.Equal(CancellationToken.None, ctx.Cancellation);
    }

    [Fact]
    public void MacroContext_FullConstructor_RoundTripsAllValues()
    {
        var trigger = new CommandMacroTrigger(TriggerKind.BeforeCommand, "File.Open", DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource();

        var ctx = new MacroContext("BeforeOpen", trigger, cts.Token);

        Assert.Equal("BeforeOpen", ctx.MacroName);
        Assert.Equal("BeforeCommand", ctx.TriggerKind);
        Assert.False(ctx.IsManual);
        Assert.Same(trigger, ctx.Trigger);
        Assert.Equal(cts.Token, ctx.Cancellation);
    }

    [Theory]
    [InlineData("macroName", false)]
    [InlineData("trigger", true)]
    public void MacroContext_FullConstructor_RejectsNullArguments(string expectedParam, bool nullTrigger)
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new MacroContext(
            nullTrigger ? "Foo" : null!,
            nullTrigger ? null! : ManualMacroTrigger.Instance,
            CancellationToken.None));
        Assert.Equal(expectedParam, ex.ParamName);
    }

    [Fact]
    public void Helpers_IsPublicStatic()
    {
        Type t = typeof(Helpers);
        Assert.True(t.IsPublic, "Helpers must be public.");
        Assert.True(t.IsAbstract && t.IsSealed, "Helpers must be a static class.");
    }

    [Theory]
    [InlineData(nameof(Helpers.TypeAsync))]
    [InlineData(nameof(Helpers.MoveCaretAsync))]
    [InlineData(nameof(Helpers.SelectAsync))]
    [InlineData(nameof(Helpers.ExecuteCommandAsync))]
    [InlineData(nameof(Helpers.RunCommandAsync))]
    [InlineData(nameof(Helpers.WaitAsync))]
    [InlineData(nameof(Helpers.OpenFileAsync))]
    public void Helpers_AllVerbsAreStaticAsync(string methodName)
    {
        MethodInfo? method = typeof(Helpers).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True(method!.IsStatic, $"{methodName} must be static.");
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Theory]
    [InlineData(nameof(Helpers.TypeAsync))]
    [InlineData(nameof(Helpers.MoveCaretAsync))]
    [InlineData(nameof(Helpers.SelectAsync))]
    [InlineData(nameof(Helpers.ExecuteCommandAsync))]
    [InlineData(nameof(Helpers.RunCommandAsync))]
    [InlineData(nameof(Helpers.WaitAsync))]
    [InlineData(nameof(Helpers.OpenFileAsync))]
    public void Helpers_AllVerbsAcceptOptionalCancellationToken(string methodName)
    {
        MethodInfo method = typeof(Helpers).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;
        ParameterInfo last = method.GetParameters().Last();
        Assert.Equal(typeof(CancellationToken), last.ParameterType);
        Assert.True(last.HasDefaultValue, $"{methodName}'s CancellationToken must be optional.");
    }

    [Fact]
    public void Helpers_PublicSurfaceIsExactlyTheSevenVerbs()
    {
        // Lock the surface so accidentally adding a public method without updating the codegen
        // contract fails the test loudly.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Helpers.TypeAsync),
            nameof(Helpers.MoveCaretAsync),
            nameof(Helpers.SelectAsync),
            nameof(Helpers.ExecuteCommandAsync),
            nameof(Helpers.RunCommandAsync),
            nameof(Helpers.WaitAsync),
            nameof(Helpers.OpenFileAsync),
        };

        var actual = typeof(Helpers)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Helpers_AmbientGlobalsSlotExists()
    {
        // The macro player (M3) writes to this slot before script.RunAsync. Internal contract,
        // verified here so a refactor can't silently rename it and break the player.
        FieldInfo? slot = typeof(Helpers).GetField("CurrentGlobals", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(slot);
        Assert.Equal(typeof(AsyncLocal<MacroGlobals?>), slot!.FieldType);
    }

    [Fact]
    public async Task Helpers_WaitAsync_DelaysAndHonoursCancellation()
    {
        // WaitAsync is the one helper that doesn't need DTE / IVsUIShell, so we can exercise the
        // full happy path and cancellation path without a hosted VS process. The other helpers
        // are behavior-tested in Macros.IntegrationTests where a real VS shell is available.
        await Helpers.WaitAsync(1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Helpers.WaitAsync(50_000, cts.Token));
    }
}
