using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Macros.Engine.Scripting;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;

namespace Macros.Tests.Scripting;

public sealed class HelpersPromptTests
{
    private sealed class StubPromptService : IMacroPromptService
    {
        private readonly Func<string, string, string> _callback;

        public StubPromptService(Func<string, string, string> callback)
            => _callback = callback;

        public Task<string> PromptAsync(string label, string defaultValue)
            => Task.FromResult(_callback(label, defaultValue));
    }

    private sealed class PromptHarness
    {
        public PromptHarness(IMacroPromptService? promptService)
        {
#pragma warning disable VSSDK005
            Jtc = new JoinableTaskContext();
#pragma warning restore VSSDK005
            Globals = new MacroGlobals(Mock.Of<DTE2>(), new MacroContext("prompt-test"))
            {
                UiThreadFactory = Jtc.Factory,
                PromptService = promptService,
            };
        }

        public JoinableTaskContext Jtc { get; }

        public MacroGlobals Globals { get; }

        public string Prompt(string label, string defaultValue = "")
        {
            Helpers.CurrentGlobals.Value = Globals;
            try
            {
                return Jtc.Factory.Run(() => Helpers.PromptAsync(label, defaultValue));
            }
            finally
            {
                Helpers.CurrentGlobals.Value = null;
            }
        }
    }

    [Fact]
    public void PromptAsync_UsesPromptServiceAndReturnsAcceptedText()
    {
        string? capturedLabel = null;
        string? capturedDefault = null;
        var harness = new PromptHarness(new StubPromptService((label, defaultValue) =>
        {
            capturedLabel = label;
            capturedDefault = defaultValue;
            return "typed value";
        }));

        string result = harness.Prompt("Enter value", "seed");

        Assert.Equal("typed value", result);
        Assert.Equal("Enter value", capturedLabel);
        Assert.Equal("seed", capturedDefault);
    }

    [Fact]
    public void PromptAsync_CancelPathReturnsDefaultValue()
    {
        var harness = new PromptHarness(new StubPromptService((_, defaultValue) => defaultValue));

        string result = harness.Prompt("Enter value", "seed");

        Assert.Equal("seed", result);
    }

    [Fact]
    public async Task PromptAsync_NullLabel_ThrowsArgumentNullException()
    {
        var harness = new PromptHarness(new StubPromptService((_, defaultValue) => defaultValue));
        Helpers.CurrentGlobals.Value = harness.Globals;
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => Helpers.PromptAsync(null!));
        }
        finally
        {
            Helpers.CurrentGlobals.Value = null;
        }
    }

    [Fact]
    public void PromptAsync_WithoutConfiguredService_ThrowsInvalidOperationException()
    {
        var harness = new PromptHarness(promptService: null);

        var ex = Assert.Throws<InvalidOperationException>(() => harness.Prompt("Enter value", "seed"));
        Assert.Contains("prompt service", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
