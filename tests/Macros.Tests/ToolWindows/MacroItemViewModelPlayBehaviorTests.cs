using System;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Recording;
using Macros.Engine.Storage;
using Macros.Engine.Triggers;
using Macros.ToolWindows;
using Moq;
using Xunit;

namespace Macros.Tests.ToolWindows;

/// <summary>
/// Verifies that <see cref="MacroItemViewModel.PlayCommand"/> properly surfaces
/// <see cref="MacroPlayResult"/> failures via the injected error renderer, and that
/// successful plays do not invoke the renderer.
/// </summary>
/// <remarks>
/// These tests exercise the fix for the silent-failure bug where
/// <c>InvokePlay</c> previously fire-and-forgot <c>PlayByNameAsync</c> without
/// checking the returned <see cref="MacroPlayResult"/>. A failed macro (compile
/// error or runtime exception) now flows through the error renderer instead of
/// disappearing silently.
/// </remarks>
public sealed class MacroItemViewModelPlayBehaviorTests
{
    private static MacroEntry MakeEntry(string name = "Greeting") =>
        new(name, MacroScope.Global, $@"X:\fake\{name}.csx", 0,
            DateTimeOffset.UtcNow, 128, Array.Empty<TriggerBinding>());

    /// <summary>
    /// When <see cref="IMacroService.PlayByNameAsync"/> returns a failed result, the
    /// view-model must invoke the error renderer with that result and the macro name.
    /// </summary>
    [Fact]
    public async Task PlayCommand_WhenPlayerReturnsFailure_InvokesErrorRenderer()
    {
        var failResult = new MacroPlayResult(
            Success: false,
            CompilationError: "CS0001: some compile error",
            RuntimeError: null,
            Duration: TimeSpan.Zero);

        var service = new Mock<IMacroService>(MockBehavior.Loose);
        service
            .Setup(s => s.PlayByNameAsync(
                It.IsAny<string>(), It.IsAny<MacroScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        MacroPlayResult? renderedResult = null;
        string? renderedName = null;
        Task FakeRenderer(MacroPlayResult r, string n)
        {
            renderedResult = r;
            renderedName = n;
            return Task.CompletedTask;
        }

        var vm = new MacroItemViewModel(MakeEntry("Greeting"), service.Object, FakeRenderer);

        vm.PlayCommand.Execute(null);
        await vm.LastPlayTask!;

        Assert.Same(failResult, renderedResult);
        Assert.Equal("Greeting", renderedName);
    }

    /// <summary>
    /// When <see cref="IMacroService.PlayByNameAsync"/> returns a runtime-error result,
    /// the view-model must invoke the error renderer (same path as a compile error).
    /// </summary>
    [Fact]
    public async Task PlayCommand_WhenPlayerReturnsRuntimeError_InvokesErrorRenderer()
    {
        var runtimeEx = new InvalidOperationException("boom");
        var failResult = new MacroPlayResult(
            Success: false,
            CompilationError: null,
            RuntimeError: runtimeEx,
            Duration: TimeSpan.FromMilliseconds(12));

        var service = new Mock<IMacroService>(MockBehavior.Loose);
        service
            .Setup(s => s.PlayByNameAsync(
                It.IsAny<string>(), It.IsAny<MacroScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        MacroPlayResult? renderedResult = null;
        Task FakeRenderer(MacroPlayResult r, string n) { renderedResult = r; return Task.CompletedTask; }

        var vm = new MacroItemViewModel(MakeEntry("Exploding"), service.Object, FakeRenderer);

        vm.PlayCommand.Execute(null);
        await vm.LastPlayTask!;

        Assert.NotNull(renderedResult);
        Assert.Same(runtimeEx, renderedResult!.RuntimeError);
    }

    /// <summary>
    /// When <see cref="IMacroService.PlayByNameAsync"/> returns a successful result,
    /// the error renderer must NOT be invoked.
    /// </summary>
    [Fact]
    public async Task PlayCommand_WhenPlayerSucceeds_DoesNotInvokeErrorRenderer()
    {
        var okResult = new MacroPlayResult(
            Success: true,
            CompilationError: null,
            RuntimeError: null,
            Duration: TimeSpan.FromMilliseconds(5));

        var service = new Mock<IMacroService>(MockBehavior.Loose);
        service
            .Setup(s => s.PlayByNameAsync(
                It.IsAny<string>(), It.IsAny<MacroScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(okResult);

        bool rendererCalled = false;
        Task FakeRenderer(MacroPlayResult r, string n) { rendererCalled = true; return Task.CompletedTask; }

        var vm = new MacroItemViewModel(MakeEntry("MyMacro"), service.Object, FakeRenderer);

        vm.PlayCommand.Execute(null);
        await vm.LastPlayTask!;

        Assert.False(rendererCalled);
    }

    /// <summary>
    /// When <see cref="IMacroService"/> is <see langword="null"/> (design-time scenario),
    /// executing the play command must remain a no-op — no <see cref="NullReferenceException"/>.
    /// </summary>
    [Fact]
    public void PlayCommand_NullService_IsNoOp()
    {
        bool rendererCalled = false;
        Task FakeRenderer(MacroPlayResult r, string n) { rendererCalled = true; return Task.CompletedTask; }

        var vm = new MacroItemViewModel(MakeEntry(), service: null, FakeRenderer);

        vm.PlayCommand.Execute(null);

        Assert.Null(vm.LastPlayTask);
        Assert.False(rendererCalled);
    }
}
