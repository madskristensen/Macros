using System.Threading;
using EnvDTE80;
using Macros.Engine.Player;
using Macros.Engine.Scripting;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;

namespace Macros.Tests.Player;

/// <summary>
/// Tests for the <c>SkipIntelliSenseShimSourceResolver</c> baked into
/// <see cref="MacroPlayer"/>. Every test runs through the full compile + run path so we
/// confirm the resolver integrates end-to-end, not just in isolation.
/// </summary>
public sealed class MacroPlayerShimSkipTests
{
    // ── harness (mirrors MacroPlayerTests.CreatePlayer) ──────────────────────────────

    private sealed class PlayerHarness
    {
        private readonly MacroPlayer _player;
        private readonly JoinableTaskFactory _jtf;

        public PlayerHarness()
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            JoinableTaskContext jtc;
            try
            {
#pragma warning disable VSSDK005
                jtc = new JoinableTaskContext();
#pragma warning restore VSSDK005
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            _jtf = jtc.Factory;
            _player = new MacroPlayer(_jtf, new ScriptCompilationCache(), Mock.Of<DTE2>());
        }

        public MacroPlayResult Play(string source, string macroName = "test", string? csxFilePath = null) =>
            _jtf.Run(() => _player.PlayAsync(source, macroName, trigger: null, CancellationToken.None, csxFilePath));
    }

    // ── tests ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A script whose first line is the generated <c>#load</c> shim directive compiles
    /// and runs without error even though the referenced file does not exist on disk.
    /// The resolver short-circuits before the file system is touched.
    /// </summary>
    [Fact]
    public void Play_WithShimLoad_NoFileOnDisk_SucceedsWithoutError()
    {
        var harness = new PlayerHarness();

        const string source =
            "#load \".intellisense/Macros.Intellisense.csx\"\n" +
            "int x = 1 + 1;\n";

        MacroPlayResult result = harness.Play(source);

        Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
        Assert.Null(result.CompilationError);
        Assert.Null(result.RuntimeError);
    }

    /// <summary>
    /// Shim stubs (<c>DTE = null!</c>, etc.) are NOT compiled in at runtime because the
    /// resolver returns an empty stream. The real <see cref="MacroGlobals"/> injected by
    /// the player therefore wins — a script reading <c>Context.MacroName</c> sees the
    /// value we passed, not a null stub.
    /// </summary>
    [Fact]
    public void Play_WithShimLoad_ContextGlobals_AreRealNotShimStubs()
    {
        var harness = new PlayerHarness();

        const string source =
            "#load \".intellisense/Macros.Intellisense.csx\"\n" +
            "if (Context is null) throw new System.Exception(\"Context is null\");\n" +
            "if (Context.MacroName != \"shimtest\") throw new System.Exception(\"MacroName: \" + Context.MacroName);\n";

        MacroPlayResult result = harness.Play(source, macroName: "shimtest");

        Assert.True(result.Success, result.RuntimeError?.Message ?? "no error");
        Assert.Null(result.RuntimeError);
    }

    /// <summary>
    /// An unrelated <c>#load</c> that points to a non-existent file is forwarded to
    /// <see cref="Microsoft.CodeAnalysis.Scripting.SourceFileResolver"/> and produces a
    /// real compilation error — proving the resolver delegates for non-shim paths.
    /// </summary>
    [Fact]
    public void Play_WithUnrelatedLoad_NonexistentFile_ProducesCompilationError()
    {
        var harness = new PlayerHarness();

        const string source = "#load \"/nonexistent/other.csx\"\nint x = 1;\n";

        MacroPlayResult result = harness.Play(source);

        Assert.False(result.Success);
        Assert.NotNull(result.CompilationError);
        Assert.False(string.IsNullOrWhiteSpace(result.CompilationError));
        Assert.Null(result.RuntimeError);
    }

    /// <summary>
    /// The match is on the file-name portion only. A shim under a completely different
    /// absolute path is still intercepted and resolved as empty — the shim moves between
    /// <c>%APPDATA%\Macros\.intellisense\</c> (global) and
    /// <c>&lt;sln&gt;\.vs\Macros\.intellisense\</c> (repo).
    /// </summary>
    [Fact]
    public void Play_WithShimLoad_DifferentPrefixPath_IsAlsoShortCircuited()
    {
        var harness = new PlayerHarness();

        const string source =
            "#load \"/some/totally/different/path/Macros.Intellisense.csx\"\n" +
            "int x = 42;\n";

        MacroPlayResult result = harness.Play(source);

        Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
        Assert.Null(result.CompilationError);
    }

    /// <summary>
    /// The shim filename match is case-insensitive (<see cref="StringComparison.OrdinalIgnoreCase"/>),
    /// so mixed-case variants like <c>Macros.intellisense.CSX</c> are also short-circuited.
    /// </summary>
    [Fact]
    public void Play_WithShimLoad_MixedCase_IsAlsoShortCircuited()
    {
        var harness = new PlayerHarness();

        const string source =
            "#load \"Macros.intellisense.CSX\"\n" +
            "int x = 7;\n";

        MacroPlayResult result = harness.Play(source);

        Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
        Assert.Null(result.CompilationError);
    }

    /// <summary>
    /// Regression: when a macro file path is supplied, relative <c>#load</c> directives
    /// must resolve against the macro's own directory so user-authored multi-file scripts
    /// (e.g. <c>#load "shared.csx"</c> next to the macro) work as expected. Previously
    /// the player ignored the path and forwarded to a <c>SourceFileResolver</c> rooted at
    /// <c>baseDirectory: null</c>, which broke every relative include.
    /// </summary>
    [Fact]
    public void Play_WithRelativeLoad_ResolvesAgainstMacroFolder()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacrosCsxPathTest_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string sharedPath = System.IO.Path.Combine(dir, "shared.csx");
            System.IO.File.WriteAllText(sharedPath, "int sharedValue = 42;\n");

            string macroPath = System.IO.Path.Combine(dir, "main.csx");
            const string source = "#load \"shared.csx\"\nif (sharedValue != 42) throw new System.Exception(\"sharedValue not loaded\");\n";

            var harness = new PlayerHarness();
            MacroPlayResult result = harness.Play(source, macroName: "main", csxFilePath: macroPath);

            Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
            Assert.Null(result.CompilationError);
            Assert.Null(result.RuntimeError);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Regression: identical source text at two different macro paths must NOT share the
    /// compiled-script cache entry, because <c>#load</c> resolution depends on the macro's
    /// directory. Previously the cache key was <c>source</c> alone, aliasing all paths.
    /// </summary>
    [Fact]
    public void Play_SameSource_DifferentPaths_AreCachedSeparately()
    {
        var dirA = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacrosCacheKeyA_" + System.Guid.NewGuid().ToString("N"));
        var dirB = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacrosCacheKeyB_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dirA);
        System.IO.Directory.CreateDirectory(dirB);
        try
        {
            // Each folder has its own shared.csx with a DIFFERENT integer value. If the
            // player aliased these two paths in the cache, the second play would observe
            // the first folder's value and the assertion would fire.
            System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "shared.csx"), "int sharedValue = 1;\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dirB, "shared.csx"), "int sharedValue = 2;\n");

            const string source = "#load \"shared.csx\"\nif (sharedValue != EXPECTED) throw new System.Exception(\"got \" + sharedValue);\n";

            var harness = new PlayerHarness();

            string macroA = System.IO.Path.Combine(dirA, "main.csx");
            string macroB = System.IO.Path.Combine(dirB, "main.csx");

            MacroPlayResult resultA = harness.Play(source.Replace("EXPECTED", "1"), macroName: "a", csxFilePath: macroA);
            Assert.True(resultA.Success, resultA.CompilationError ?? resultA.RuntimeError?.ToString());

            MacroPlayResult resultB = harness.Play(source.Replace("EXPECTED", "2"), macroName: "b", csxFilePath: macroB);
            Assert.True(resultB.Success, resultB.CompilationError ?? resultB.RuntimeError?.ToString());
        }
        finally
        {
            try { System.IO.Directory.Delete(dirA, recursive: true); } catch { }
            try { System.IO.Directory.Delete(dirB, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Regression: the shim skip must continue to work when a macro file path is supplied.
    /// </summary>
    [Fact]
    public void Play_WithShimLoad_AndPath_StillSkipsShim()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacrosShimPathTest_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string macroPath = System.IO.Path.Combine(dir, "main.csx");

            const string source =
                "#load \".intellisense/Macros.Intellisense.csx\"\n" +
                "int x = 1 + 1;\n";

            var harness = new PlayerHarness();
            MacroPlayResult result = harness.Play(source, macroName: "main", csxFilePath: macroPath);

            Assert.True(result.Success, result.CompilationError ?? result.RuntimeError?.ToString() ?? "no error");
            Assert.Null(result.CompilationError);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
