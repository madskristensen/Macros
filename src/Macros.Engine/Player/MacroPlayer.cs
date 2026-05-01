using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Macros.Engine.Recording;
using Macros.Engine.Scripting;
using Macros.Engine.Triggers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Engine.Player;

/// <summary>
/// Default <see cref="IMacroPlayer"/>. Compiles macro source via
/// <see cref="ScriptCompilationCache"/>, runs the resulting <see cref="Script{TResult}"/> on
/// the Visual Studio UI thread with a fresh <see cref="MacroGlobals"/>, and reports the
/// outcome as a <see cref="MacroPlayResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Compilation is CPU-bound and runs on the threadpool (so
/// <see cref="JoinableTaskFactory"/> never blocks the UI for a 200-500&#160;ms first-time
/// compile). Execution then hops to the UI thread because <see cref="Helpers"/> verbs touch
/// DTE / IVsUIShell synchronously after their own <c>SwitchToMainThreadAsync</c>; starting
/// playback on the UI thread keeps the first helper call fast and avoids surprising
/// double-hops.
/// </para>
/// <para>
/// <b>ReplayGuard.</b> The <c>using (ReplayGuard.Enter())</c> scope wraps the entire
/// <c>RunAsync</c> call chain. Because <see cref="ReplayGuard"/> is backed by
/// <see cref="AsyncLocal{T}"/>, the replaying state flows across <see langword="await"/>
/// boundaries and into any child tasks the user's script may spawn, preventing re-recording
/// of playback events regardless of which thread an observer runs on.
/// </para>
/// </remarks>
internal sealed class MacroPlayer : IMacroPlayer
{
    private readonly JoinableTaskFactory _jtf;
    private readonly ScriptCompilationCache _cache;
    private readonly DTE2 _dte;

    /// <summary>
    /// Initializes a new <see cref="MacroPlayer"/>.
    /// </summary>
    /// <param name="jtf">UI-thread marshalling factory; supplied by the package.</param>
    /// <param name="cache">Shared script compilation cache (process-singleton).</param>
    /// <param name="dte">The hosting Visual Studio's DTE automation root.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MacroPlayer(JoinableTaskFactory jtf, ScriptCompilationCache cache, DTE2 dte)
    {
        _jtf = jtf ?? throw new ArgumentNullException(nameof(jtf));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _dte = dte ?? throw new ArgumentNullException(nameof(dte));
    }

    /// <inheritdoc />
    public async Task<MacroPlayResult> PlayAsync(
        string source,
        string macroName,
        IMacroTrigger? trigger,
        CancellationToken cancellation)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (macroName is null) throw new ArgumentNullException(nameof(macroName));

        var resolvedTrigger = trigger ?? ManualMacroTrigger.Instance;

        var stopwatch = Stopwatch.StartNew();

        if (cancellation.IsCancellationRequested)
        {
            return new MacroPlayResult(
                Success: false,
                CompilationError: null,
                RuntimeError: new OperationCanceledException(cancellation),
                Duration: stopwatch.Elapsed);
        }

        // ── Phase 1: compile on threadpool (CPU-bound, ~200-500 ms cold). ──────────────
        Script<object> script;
        ImmutableArray<Diagnostic> diagnostics;
        try
        {
            (script, diagnostics) = await Task.Run(
                () =>
                {
                    Script<object> compiled = _cache.GetOrAdd(source, CompileScript);
                    return (compiled, compiled.Compile(cancellation));
                },
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return new MacroPlayResult(false, null, oce, stopwatch.Elapsed);
        }
        catch (CompilationErrorException cex)
        {
            // CSharpScript.Create itself can throw for fatal syntax errors before Compile().
            return new MacroPlayResult(false, FormatDiagnostics(cex.Diagnostics), null, stopwatch.Elapsed);
        }

        if (HasErrors(diagnostics))
        {
            return new MacroPlayResult(false, FormatDiagnostics(diagnostics), null, stopwatch.Elapsed);
        }

        // ── Phase 2: marshal to UI thread and execute. ────────────────────────────────
        await _jtf.SwitchToMainThreadAsync(cancellation);

        var ctx = new MacroContext(
            macroName,
            resolvedTrigger,
            cancellation);
        var globals = new MacroGlobals(_dte, ctx);

        using (ReplayGuard.Enter())
        {
            Helpers.CurrentGlobals.Value = globals;
            try
            {
                _ = await script.RunAsync(globals, cancellation).ConfigureAwait(true);
                return new MacroPlayResult(true, null, null, stopwatch.Elapsed);
            }
            catch (CompilationErrorException cex)
            {
                // Should be unreachable because we already called Compile() above, but
                // RunAsync re-validates and we'd rather report cleanly than crash.
                return new MacroPlayResult(false, FormatDiagnostics(cex.Diagnostics), null, stopwatch.Elapsed);
            }
            catch (OperationCanceledException oce)
            {
                return new MacroPlayResult(false, null, oce, stopwatch.Elapsed);
            }
            catch (Exception runtime)
            {
                return new MacroPlayResult(false, null, runtime, stopwatch.Elapsed);
            }
            finally
            {
                Helpers.CurrentGlobals.Value = null;
            }
        }
    }

    private static Script<object> CompileScript(string src) =>
        CSharpScript.Create<object>(src, BuildScriptOptions(), typeof(MacroGlobals));

    private static ScriptOptions BuildScriptOptions() =>
        ScriptOptions.Default
            .WithMetadataResolver(InteropAwareMetadataResolver.Instance)
            .WithReferences(
                // Macros.Engine — Helpers, MacroContext, ReplayGuard.
                typeof(MacroGlobals).Assembly,
                // EnvDTE / EnvDTE80 — DTE2 surface.
                typeof(DTE).Assembly,
                typeof(DTE2).Assembly,
                // Microsoft.VisualStudio.Shell.15.0 — Package, ThreadHelper.
                typeof(Package).Assembly,
                // Community.VisualStudio.Toolkit — VS static facade used by codegen.
                typeof(VS).Assembly)
            .WithImports(
                "System",
                "System.Threading.Tasks",
                "Macros.Engine.Scripting",
                "Macros.Engine.Recording",
                "Macros.Engine.Scripting.Helpers",
                "Macros.Engine.Recording.ReplayGuard",
                "Community.VisualStudio.Toolkit.VS");

    /// <summary>
    /// Resolves <c>#r "EnvDTE"</c> and <c>#r "EnvDTE80"</c> directives that the codegen emits
    /// for external dotnet-script / IntelliSense consumers. Roslyn's default
    /// <see cref="ScriptMetadataResolver"/> only probes the script search paths, GAC, and
    /// trusted platform assemblies — none of which contain the VS interop assemblies — so
    /// without this hook the script fails to compile with
    /// <c>CS0006: Metadata file 'EnvDTE' could not be found</c>. We map those simple names
    /// to the file paths of the interop assemblies that are already loaded in the process,
    /// then delegate everything else to the default resolver so user-written
    /// <c>#r "Some.Other.Assembly"</c> directives keep working.
    /// </summary>
    private sealed class InteropAwareMetadataResolver : MetadataReferenceResolver
    {
        public static readonly InteropAwareMetadataResolver Instance = new();

        private static readonly ScriptMetadataResolver Inner = ScriptMetadataResolver.Default;

        private static readonly Dictionary<string, string> InteropPaths = BuildInteropPaths();

        private InteropAwareMetadataResolver()
        {
        }

        public override bool ResolveMissingAssemblies => Inner.ResolveMissingAssemblies;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(
            string reference,
            string? baseFilePath,
            MetadataReferenceProperties properties)
        {
            if (reference is null)
            {
                return ImmutableArray<PortableExecutableReference>.Empty;
            }

            if (InteropPaths.TryGetValue(reference, out string path)
                && !string.IsNullOrEmpty(path)
                && File.Exists(path))
            {
                return ImmutableArray.Create(MetadataReference.CreateFromFile(path, properties));
            }

            return Inner.ResolveReference(reference, baseFilePath, properties);
        }

        public override PortableExecutableReference? ResolveMissingAssembly(
            MetadataReference definition,
            AssemblyIdentity referenceIdentity) =>
            Inner.ResolveMissingAssembly(definition, referenceIdentity);

        // Roslyn requires value equality on metadata resolvers so that scripts compiled with
        // logically identical options can share Compilation state. Instance is a singleton, so
        // reference equality is sufficient and stable.
        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => typeof(InteropAwareMetadataResolver).GetHashCode();

        private static Dictionary<string, string> BuildInteropPaths()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddIfPresent(map, "EnvDTE", typeof(DTE).Assembly.Location);
            AddIfPresent(map, "EnvDTE80", typeof(DTE2).Assembly.Location);
            return map;
        }

        private static void AddIfPresent(Dictionary<string, string> map, string name, string location)
        {
            if (!string.IsNullOrEmpty(location))
            {
                map[name] = location;
            }
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic> diagnostics)
    {
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var lines = new List<string>();
        foreach (Diagnostic d in diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            lines.Add(d.ToString());
        }

        return string.Join("\n", lines);
    }
}
