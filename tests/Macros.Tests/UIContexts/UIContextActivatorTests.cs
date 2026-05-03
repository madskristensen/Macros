using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Tests.TestUtilities;
using Microsoft.VisualStudio.Threading;
using Xunit;

#pragma warning disable VSSDK005 // Use ThreadHelper.JoinableTaskContext (no VS host available)

namespace Macros.Tests.UIContexts;

/// <summary>
/// Unit tests for <c>Macros.UIContexts.UIContextActivator</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Macros VSIX assembly has VS shell runtime dependencies that prevent normal type loading
/// in a plain unit-test process. Structural tests use <see cref="MetadataLoadContext"/> (no
/// JIT / type construction). Behavioural tests use <c>UIContextActivator.CreateForTests</c>
/// together with a test-owned <see cref="JoinableTaskContext"/> to avoid the VS threading
/// singleton requirement.
/// </para>
/// </remarks>
public sealed class UIContextActivatorTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Type LoadActivatorType()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType("Macros.UIContexts.UIContextActivator");
        Assert.NotNull(type);
        return type!;
    }

    private static MetadataLoadContext CreateMetadataContext(out Assembly macrosAssembly)
    {
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

    private static string LocateMacrosAssembly() => MacrosAssemblyLocator.Locate();

    /// <summary>
    /// Creates a <see cref="UIContextActivator"/> via the <c>CreateForTests</c> reflective
    /// helper so we never need to directly reference Macros.dll types in this test project.
    /// </summary>
    private static (object activator, List<(uint cookie, int active)> calls)
        CreateActivatorViaReflection(
            FakeMacroService service,
            JoinableTaskFactory jtf,
            uint recCookie = 10u,
            uint notRecCookie = 20u)
    {
        string macrosDll = LocateMacrosAssembly();
        var macrosAsm = Assembly.LoadFrom(macrosDll);

        Type activatorType = macrosAsm.GetType("Macros.UIContexts.UIContextActivator")!;
        Assert.NotNull(activatorType);

        var calls = new List<(uint, int)>();
        Action<uint, int> setCmdUIContext = (cookie, active) => calls.Add((cookie, active));

        var createForTests = activatorType.GetMethod(
            "CreateForTests",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(createForTests);

        object instance = createForTests!.Invoke(null, new object[]
        {
            service, jtf, setCmdUIContext, recCookie, notRecCookie
        })!;

        return (instance, calls);
    }

    private static Task InvokeApplyStateAsync(object activator, MacroState state)
    {
        MethodInfo applyStateAsync = activator.GetType()
            .GetMethod("ApplyStateAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.NotNull(applyStateAsync);
        return (Task)applyStateAsync.Invoke(activator, new object[] { state })!;
    }

    private static JoinableTaskContext CreateTestJtc() => new JoinableTaskContext();

    // ---------------------------------------------------------------------------
    // Structural tests (MetadataLoadContext — no VS shell required)
    // ---------------------------------------------------------------------------

    [Fact]
    public void UIContextActivator_IsInternalSealed()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType("Macros.UIContexts.UIContextActivator");

        Assert.NotNull(type);
        Assert.True(type!.IsSealed, "UIContextActivator should be sealed.");
        // Internal = not public and not nested-public
        Assert.False(type.IsPublic, "UIContextActivator should be internal (not public).");
    }

    [Fact]
    public void UIContextActivator_ImplementsIDisposable()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType("Macros.UIContexts.UIContextActivator");
        Assert.NotNull(type);

        bool implementsDisposable = type!.GetInterfaces()
            .Any(i => i.FullName == "System.IDisposable");

        Assert.True(implementsDisposable, "UIContextActivator must implement IDisposable.");
    }

    [Fact]
    public void UIContextActivator_HasCreateForTestsMethod()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType("Macros.UIContexts.UIContextActivator");
        Assert.NotNull(type);

        MethodInfo? method = type!.GetMethod(
            "CreateForTests",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
    }

    [Fact]
    public void UIContextActivator_HasApplyStateAsyncMethod()
    {
        using var ctx = CreateMetadataContext(out Assembly macrosAsm);
        Type? type = macrosAsm.GetType("Macros.UIContexts.UIContextActivator");
        Assert.NotNull(type);

        MethodInfo? method = type!.GetMethod(
            "ApplyStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.False(method!.IsStatic);
    }

    // ---------------------------------------------------------------------------
    // Behavioural tests (runtime reflection via Assembly.LoadFrom)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ApplyStateAsync_Recording_ActivatesRecordingCookie_DeactivatesNotRecording()
    {
        using var jtc = CreateTestJtc();
        var service = new FakeMacroService();
        var (activator, calls) = CreateActivatorViaReflection(service, jtc.Factory, recCookie: 10u, notRecCookie: 20u);

        await InvokeApplyStateAsync(activator, MacroState.Recording);

        Assert.Contains((10u, 1), calls);
        Assert.Contains((20u, 0), calls);
    }

    [Fact]
    public async Task ApplyStateAsync_Idle_DeactivatesRecordingCookie_ActivatesNotRecording()
    {
        using var jtc = CreateTestJtc();
        var service = new FakeMacroService();
        var (activator, calls) = CreateActivatorViaReflection(service, jtc.Factory, recCookie: 10u, notRecCookie: 20u);

        await InvokeApplyStateAsync(activator, MacroState.Idle);

        Assert.Contains((10u, 0), calls);
        Assert.Contains((20u, 1), calls);
    }

    [Fact]
    public async Task ApplyStateAsync_Playing_DeactivatesRecordingCookie_ActivatesNotRecording()
    {
        using var jtc = CreateTestJtc();
        var service = new FakeMacroService();
        var (activator, calls) = CreateActivatorViaReflection(service, jtc.Factory, recCookie: 10u, notRecCookie: 20u);

        await InvokeApplyStateAsync(activator, MacroState.Playing);

        Assert.Contains((10u, 0), calls);
        Assert.Contains((20u, 1), calls);
    }

    [Fact]
    public async Task ApplyStateAsync_SetsCookiesInOrder_RecordingFirst()
    {
        using var jtc = CreateTestJtc();
        var service = new FakeMacroService();
        var (activator, calls) = CreateActivatorViaReflection(service, jtc.Factory, recCookie: 10u, notRecCookie: 20u);

        await InvokeApplyStateAsync(activator, MacroState.Recording);

        Assert.Equal(2, calls.Count);
        Assert.Equal((10u, 1), calls[0]); // recording cookie activated first
        Assert.Equal((20u, 0), calls[1]); // not-recording cookie deactivated second
    }

    [Fact]
    public void Dispose_UnsubscribesFromStateChanged()
    {
        using var jtc = CreateTestJtc();
        var service = new FakeMacroService();
        var (activator, calls) = CreateActivatorViaReflection(service, jtc.Factory);

        Assert.True(service.HasStateChangedSubscribers, "Expected StateChanged subscription after construction.");

        ((IDisposable)activator).Dispose();

        Assert.False(service.HasStateChangedSubscribers, "Expected StateChanged subscription removed after Dispose.");
    }
}

/// <summary>
/// Minimal <see cref="IMacroService"/> fake that tracks event subscriptions and lets tests
/// inspect them without VS shell or Moq.
/// </summary>
internal sealed class FakeMacroService : IMacroService
{
    public MacroState State { get; set; } = MacroState.Idle;
    public IRecordingSink? CurrentSession => null;
    public string? CurrentMacroSource => null;
    public string? CurrentMacroName => null;

    private EventHandler<MacroStateChangedEventArgs>? _stateChanged;
    private EventHandler? _recordingCapReached;
    private EventHandler<int>? _recordingStepCountChanged;

    public event EventHandler<MacroStateChangedEventArgs>? StateChanged
    {
        add => _stateChanged += value;
        remove => _stateChanged -= value;
    }

    public event EventHandler? RecordingCapReached
    {
        add => _recordingCapReached += value;
        remove => _recordingCapReached -= value;
    }

    public event EventHandler<RecordingSavedEventArgs>? RecordingSaved { add { } remove { } }

    public event EventHandler<int>? RecordingStepCountChanged
    {
        add => _recordingStepCountChanged += value;
        remove => _recordingStepCountChanged -= value;
    }

    private EventHandler<TriggeredExecutionEventArgs>? _triggeredExecutionStarted;
    private EventHandler<TriggeredExecutionEventArgs>? _triggeredExecutionEnded;

    public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionStarted
    {
        add => _triggeredExecutionStarted += value;
        remove => _triggeredExecutionStarted -= value;
    }

    public event EventHandler<TriggeredExecutionEventArgs>? TriggeredExecutionEnded
    {
        add => _triggeredExecutionEnded += value;
        remove => _triggeredExecutionEnded -= value;
    }

    public int CurrentRecordingMaxSteps => int.MaxValue;

    public string? CurrentMacroPath => null;

    public bool HasStateChangedSubscribers => _stateChanged != null;

    public void RaiseStateChanged(MacroState oldState, MacroState newState)
        => _stateChanged?.Invoke(this, new MacroStateChangedEventArgs(oldState, newState));

    public Task StartRecordingAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> StopRecordingAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(string.Empty);
    public Task<MacroPlayResult> PlayCurrentAsync(System.Threading.CancellationToken ct = default)
        => Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
    public Task<MacroPlayResult> PlayByNameAsync(string name, Macros.Engine.Storage.MacroScope scope, System.Threading.CancellationToken cancellation = default)
        => Task.FromResult(new MacroPlayResult(true, null, null, TimeSpan.Zero));
    public Task CancelAsync() => Task.CompletedTask;
    public void CancelActivePlay() { }
}
