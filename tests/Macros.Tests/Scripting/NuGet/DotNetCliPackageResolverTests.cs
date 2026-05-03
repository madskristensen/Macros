using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Scripting.NuGet;
using Xunit;

namespace Macros.Tests.Scripting.NuGet;

/// <summary>
/// Behavioural tests for <see cref="DotNetCliPackageResolver"/> using a fake process runner
/// so we exercise the cache, marker semantics, and failure surfaces without invoking the
/// real <c>dotnet</c> CLI. The "happy-path actually downloads" case is covered by
/// integration tests, not here — those are slow and network-dependent.
/// </summary>
public sealed class DotNetCliPackageResolverTests : IDisposable
{
    private readonly string _cacheRoot;

    public DotNetCliPackageResolverTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "Macros-Tests-NuGet", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
        }
        catch
        {
            // Best-effort: another test instance or virus scanner may hold the folder briefly.
        }
    }

    [Fact]
    public async Task ResolveAsync_EmptyInput_ReturnsEmpty()
    {
        var resolver = new DotNetCliPackageResolver(_cacheRoot, _ => new FakeRunner(0, "", "", onRun: null));

        var result = await resolver.ResolveAsync(Array.Empty<NuGetReference>(), progress: null, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAsync_HappyPath_RunsCliAndReturnsBinAssemblies()
    {
        const string id = "Test.Package";
        const string version = "1.0.0";
        var reference = new NuGetReference(id, version);

        int factoryCalls = 0;
        var resolver = new DotNetCliPackageResolver(_cacheRoot, psi =>
        {
            factoryCalls++;
            return new FakeRunner(0, "", "", onRun: () =>
            {
                // Simulate dotnet build by writing the expected output DLL into bin\.
                string bin = Path.Combine(psi.WorkingDirectory, "bin");
                Directory.CreateDirectory(bin);
                File.WriteAllText(Path.Combine(bin, "Test.Package.dll"), "fake-dll");
                // Sneak a runtimes/<rid>/native/something.dll to verify it gets filtered.
                string native = Path.Combine(bin, "runtimes", "win-x64", "native");
                Directory.CreateDirectory(native);
                File.WriteAllText(Path.Combine(native, "native.dll"), "fake-native");
            });
        });

        var first = await resolver.ResolveAsync(new[] { reference }, progress: null, CancellationToken.None);

        Assert.Single(first);
        Assert.EndsWith("Test.Package.dll", first[0]);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ResolveAsync_SecondCallHitsCache_NoCliInvocation()
    {
        var reference = new NuGetReference("Cached.Pkg", "1.0.0");

        int factoryCalls = 0;
        var resolver = new DotNetCliPackageResolver(_cacheRoot, psi =>
        {
            factoryCalls++;
            return new FakeRunner(0, "", "", onRun: () =>
            {
                string bin = Path.Combine(psi.WorkingDirectory, "bin");
                Directory.CreateDirectory(bin);
                File.WriteAllText(Path.Combine(bin, "Cached.Pkg.dll"), "fake");
            });
        });

        await resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None);
        await resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None);
        await resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None);

        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ResolveAsync_NonZeroExitCode_ThrowsNuGetResolveException()
    {
        var reference = new NuGetReference("Bad.Pkg", "9.9.9");
        var resolver = new DotNetCliPackageResolver(_cacheRoot, _ =>
            new FakeRunner(1, stdout: "Some build log...", stderr: "error NU1101: Unable to find package", onRun: null));

        var ex = await Assert.ThrowsAsync<NuGetResolveException>(
            () => resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None));

        Assert.Contains("Bad.Pkg", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NU1101", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_DotnetMissing_ThrowsActionableMessage()
    {
        var reference = new NuGetReference("X", "1.0.0");
        var resolver = new DotNetCliPackageResolver(_cacheRoot, _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified"));

        var ex = await Assert.ThrowsAsync<NuGetResolveException>(
            () => resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None));

        Assert.Contains(".NET SDK", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dot.net/download", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UnsafeReference_RejectsBeforeRunningCli()
    {
        var reference = new NuGetReference("..\\..\\evil", "1.0.0");
        int factoryCalls = 0;
        var resolver = new DotNetCliPackageResolver(_cacheRoot, _ => { factoryCalls++; return new FakeRunner(0, "", "", null); });

        await Assert.ThrowsAsync<NuGetResolveException>(
            () => resolver.ResolveAsync(new[] { reference }, null, CancellationToken.None));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ResolveAsync_Cancellation_PropagatesCleanly()
    {
        var reference = new NuGetReference("X", "1.0.0");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var resolver = new DotNetCliPackageResolver(_cacheRoot, _ => new FakeRunner(0, "", "", null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(new[] { reference }, null, cts.Token));
    }

    [Fact]
    public void BuildProjectXml_EmitsPackageReferencesWithEscaping()
    {
        var refs = new[]
        {
            new NuGetReference("A.B", "1.0.0"),
            new NuGetReference("Quoted&Funky", "2.0.0-rc<1>"),
        };

        string xml = DotNetCliPackageResolver.BuildProjectXml(refs);

        Assert.Contains("<TargetFramework>net48</TargetFramework>", xml);
        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", xml);
        Assert.Contains("<PackageReference Include=\"A.B\" Version=\"1.0.0\" />", xml);
        Assert.Contains("Quoted&amp;Funky", xml);
        Assert.Contains("2.0.0-rc&lt;1&gt;", xml);
    }

    [Fact]
    public void BuildCliArguments_QuotesPathWithSpaces()
    {
        string args = DotNetCliPackageResolver.BuildCliArguments(@"C:\with space\proj.csproj");
        Assert.StartsWith("build \"C:\\with space\\proj.csproj\" -c Release", args);
        Assert.Contains("--verbosity quiet", args);
    }

    /// <summary>
    /// Test double for <see cref="IProcessRunner"/>. Optionally invokes a callback before
    /// returning so a test can simulate the side-effect of <c>dotnet build</c> writing into
    /// the cache folder.
    /// </summary>
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly int _exit;
        private readonly string _stdout;
        private readonly string _stderr;
        private readonly Action? _onRun;

        public FakeRunner(int exitCode, string stdout, string stderr, Action? onRun)
        {
            _exit = exitCode;
            _stdout = stdout;
            _stderr = stderr;
            _onRun = onRun;
        }

        public Task<ProcessRunResult> RunAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            _onRun?.Invoke();
            return Task.FromResult(new ProcessRunResult(_exit, _stdout, _stderr));
        }
    }
}
