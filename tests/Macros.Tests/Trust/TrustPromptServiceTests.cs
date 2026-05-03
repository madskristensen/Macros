using System;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Storage;
using Macros.Options;
using Macros.Trust;
using Microsoft.VisualStudio.Threading;
using Xunit;

namespace Macros.Tests.Trust;

public sealed class TrustPromptServiceTests
{
    private const string SolutionPath = @"C:\repo\Example.sln";

    private static JoinableTaskFactory CreateJtf()
    {
#pragma warning disable VSSDK005
        return new JoinableTaskContext().Factory;
#pragma warning restore VSSDK005
    }

    private static MacroEntry RepoEntry(string name) => new(
        Name: name,
        Scope: MacroScope.Repo,
        Path: $@"C:\repo\.vs\Macros\{name}.csx",
        StepCount: 1,
        Modified: DateTimeOffset.UtcNow,
        SizeBytes: 32,
        Triggers: null!);

    private static MacroEntry GlobalEntry(string name) => new(
        Name: name,
        Scope: MacroScope.Global,
        Path: $@"C:\Users\me\AppData\Roaming\Macros\{name}.csx",
        StepCount: 1,
        Modified: DateTimeOffset.UtcNow,
        SizeBytes: 32,
        Triggers: null!);

    [Fact]
    public async Task AlreadyTrustedSolution_DoesNotPrompt()
    {
        var options = new MacrosOptions();
        options.TrustSolution(SolutionPath);
        var prompted = false;
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: (_, _) =>
            {
                prompted = true;
                return Task.FromResult(true);
            });

        var allowed = await service.IsAllowedAsync(RepoEntry("OnBuild"));

        Assert.True(allowed);
        Assert.False(prompted);
    }

    [Fact]
    public async Task GlobalMacro_DoesNotPrompt()
    {
        var options = new MacrosOptions();
        var prompted = false;
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: (_, _) =>
            {
                prompted = true;
                return Task.FromResult(true);
            });

        var allowed = await service.IsAllowedAsync(GlobalEntry("Global"));

        Assert.True(allowed);
        Assert.False(prompted);
    }

    [Fact]
    public async Task UndecidedSolution_Allow_TrustsSolution()
    {
        var options = new MacrosOptions();
        var promptCount = 0;
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: (_, _) =>
            {
                promptCount++;
                return Task.FromResult(true);
            });

        var allowed = await service.IsAllowedAsync(RepoEntry("OnBuild"));

        Assert.True(allowed);
        Assert.Equal(1, promptCount);
        Assert.True(options.IsSolutionTrusted(SolutionPath));
        Assert.False(options.IsSolutionBlocked(SolutionPath));
    }

    [Fact]
    public async Task UndecidedSolution_DontAllow_BlocksSolution()
    {
        var options = new MacrosOptions();
        var promptCount = 0;
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: (_, _) =>
            {
                promptCount++;
                return Task.FromResult(false);
            });

        var allowed = await service.IsAllowedAsync(RepoEntry("OnBuild"));

        Assert.False(allowed);
        Assert.Equal(1, promptCount);
        Assert.True(options.IsSolutionBlocked(SolutionPath));
        Assert.False(options.IsSolutionTrusted(SolutionPath));
    }

    [Fact]
    public async Task ConcurrentCalls_SameSolution_PromptsOnce()
    {
        var options = new MacrosOptions();
        var promptCount = 0;
        var promptStarted = new TaskCompletionSource<object?>();
        var releasePrompt = new TaskCompletionSource<object?>();
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: async (_, _) =>
            {
                Interlocked.Increment(ref promptCount);
                promptStarted.TrySetResult(null);
                await releasePrompt.Task;
                return true;
            });

        var first = service.IsAllowedAsync(RepoEntry("One"));
        await promptStarted.Task;
        var second = service.IsAllowedAsync(RepoEntry("Two"));
        releasePrompt.SetResult(null);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(new[] { true, true }, results);
        Assert.Equal(1, promptCount);
        Assert.True(options.IsSolutionTrusted(SolutionPath));
    }

    [Fact]
    public async Task BlockedSolution_ReturnsFalseWithoutPrompt()
    {
        var options = new MacrosOptions();
        options.BlockSolution(SolutionPath);
        var prompted = false;
        var service = new TrustPromptService(
            CreateJtf(),
            solutionPathProvider: () => SolutionPath,
            optionsProvider: () => Task.FromResult(options),
            showPromptAsync: (_, _) =>
            {
                prompted = true;
                return Task.FromResult(true);
            });

        var allowed = await service.IsAllowedAsync(RepoEntry("OnBuild"));

        Assert.False(allowed);
        Assert.False(prompted);
    }
}
