using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Scripting.NuGet;

/// <summary>
/// <see cref="INuGetPackageResolver"/> implementation that shells out to the <c>dotnet</c>
/// CLI to perform the actual package restore. Pragmatic and reliable: it sidesteps the
/// non-trivial NuGet.Client dependency surface (which would otherwise conflict with the
/// VS host process's own NuGet assemblies) by delegating to an SDK that's already
/// installed on every Visual Studio C# developer's machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> For a given set of references:
/// </para>
/// <list type="number">
///   <item><description>Compute a stable cache key (SHA-256 of the canonical id+version list).</description></item>
///   <item><description>If the cache folder for that key already contains a <c>.success</c> marker,
///     enumerate <c>*.dll</c>s and return — no network access.</description></item>
///   <item><description>Otherwise, generate a tiny <c>net48</c> SDK-style <c>.csproj</c> with
///     <c>&lt;PackageReference&gt;</c> for each ref and a property that copies the dependency
///     closure into the output folder.</description></item>
///   <item><description>Run <c>dotnet build /t:Restore /t:Build</c> in the cache folder.</description></item>
///   <item><description>On success, drop the <c>.success</c> marker and enumerate output DLLs.</description></item>
///   <item><description>On failure, throw <see cref="NuGetResolveException"/> with the captured
///     CLI output truncated to a sensible length.</description></item>
/// </list>
/// <para>
/// <b>Concurrency.</b> Two concurrent macros that reference the same package set would
/// otherwise race on the cache folder. We guard with a per-key <see cref="SemaphoreSlim"/>
/// so duplicate work is serialised but unrelated keys still proceed in parallel.
/// </para>
/// <para>
/// <b>Limitation.</b> Roslyn-loaded assemblies share the AppDomain with the VS host
/// process. If a package brings a different version of an assembly VS has already loaded
/// (e.g. <c>Newtonsoft.Json</c>), the host's version wins and may produce surprising
/// runtime behaviour. This is a fundamental .NET Framework limitation and is documented
/// in <c>docs/csx-reference.md</c>.
/// </para>
/// </remarks>
internal sealed class DotNetCliPackageResolver : INuGetPackageResolver
{
    private const string ProjectFileName = "macros-nuget.csproj";
    private const string SuccessMarker = ".success";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks = new(StringComparer.Ordinal);

    private readonly string _cacheRoot;
    private readonly Func<ProcessStartInfo, IProcessRunner> _processFactory;

    /// <summary>Initializes a new resolver writing caches under <c>%LOCALAPPDATA%\Macros\NuGet\</c>.</summary>
    public DotNetCliPackageResolver()
        : this(DefaultCacheRoot(), psi => new RealProcessRunner(psi))
    {
    }

    /// <summary>Initializes a new resolver with custom cache root and process runner (for tests).</summary>
    /// <param name="cacheRoot">Absolute path to the cache root directory.</param>
    /// <param name="processFactory">
    /// Factory that returns a process runner for a given <see cref="ProcessStartInfo"/>.
    /// Tests use a fake to bypass the real <c>dotnet</c> CLI.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    internal DotNetCliPackageResolver(string cacheRoot, Func<ProcessStartInfo, IProcessRunner> processFactory)
    {
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<NuGetReference> references,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (references is null) throw new ArgumentNullException(nameof(references));
        if (references.Count == 0) return Array.Empty<string>();

        foreach (var r in references)
        {
            if (!NuGetReferenceParser.IsPathSafe(r))
            {
                throw new NuGetResolveException(
                    $"Refusing to resolve unsafe package reference '{r.PackageId}, {r.Version}': contains characters that aren't valid in a file path.");
            }
        }

        cancellation.ThrowIfCancellationRequested();

        string key = NuGetReferenceParser.ComputeCacheKey(references);
        string cacheFolder = Path.Combine(_cacheRoot, key);
        string outputFolder = Path.Combine(cacheFolder, "bin");
        string marker = Path.Combine(cacheFolder, SuccessMarker);

        // Fast path: cache hit.
        if (File.Exists(marker) && Directory.Exists(outputFolder))
        {
            return EnumerateAssemblies(outputFolder);
        }

        // Serialize per-key restores so two macros with the same package set don't race.
        var gate = KeyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring — another caller may have just finished.
            if (File.Exists(marker) && Directory.Exists(outputFolder))
            {
                return EnumerateAssemblies(outputFolder);
            }

            progress?.Report(BuildProgressMessage(references));
            await RestoreAndBuildAsync(cacheFolder, references, cancellation).ConfigureAwait(false);

            // Drop the marker only after a successful build. Atomic via temp+rename.
            File.WriteAllText(marker + ".tmp", DateTime.UtcNow.ToString("o"));
            try
            {
                if (File.Exists(marker)) File.Delete(marker);
                File.Move(marker + ".tmp", marker);
            }
            catch (IOException)
            {
                // Best-effort: on failure to commit the marker, leave the .tmp behind so the
                // next attempt re-runs the restore. The output DLLs are already present, so
                // this only costs a re-run.
            }

            return EnumerateAssemblies(outputFolder);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RestoreAndBuildAsync(string cacheFolder, IReadOnlyList<NuGetReference> references, CancellationToken cancellation)
    {
        Directory.CreateDirectory(cacheFolder);
        string projectPath = Path.Combine(cacheFolder, ProjectFileName);
        File.WriteAllText(projectPath, BuildProjectXml(references));

        // Use `dotnet build /t:Restore /t:Build` rather than `dotnet restore`-only: build
        // copies the entire dependency closure into bin/ via CopyLocalLockFileAssemblies,
        // which gives us the exact set of DLLs we need without parsing project.assets.json.
        var psi = new ProcessStartInfo("dotnet", BuildCliArguments(projectPath))
        {
            WorkingDirectory = cacheFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        IProcessRunner runner;
        try
        {
            runner = _processFactory(psi);
        }
        catch (Exception ex)
        {
            throw new NuGetResolveException(
                "The Macros extension's NuGet support requires the .NET SDK on PATH (it shells out to `dotnet build` for restore).\n" +
                "Install the latest .NET SDK from https://dot.net/download and restart Visual Studio.\n\n" +
                $"Underlying error: {ex.Message}",
                ex);
        }

        ProcessRunResult result;
        try
        {
            result = await runner.RunAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NuGetResolveException(
                $"`dotnet build` failed to start while restoring NuGet packages: {ex.Message}",
                ex);
        }

        if (result.ExitCode != 0)
        {
            throw new NuGetResolveException(BuildFailureMessage(references, result));
        }
    }

    /// <summary>
    /// Builds the command-line argument string for the <c>dotnet build</c> invocation.
    /// .NET Framework 4.8 doesn't expose <c>ProcessStartInfo.ArgumentList</c> (added in
    /// .NET Core 2.1+), so we have to assemble a properly-quoted command line. The only
    /// argument we receive that may contain spaces is <paramref name="projectPath"/>;
    /// quoting with surrounding double-quotes is sufficient for Windows process startup.
    /// </summary>
    /// <param name="projectPath">Absolute path to the temp <c>.csproj</c>.</param>
    /// <returns>The argument string passed to <see cref="ProcessStartInfo.Arguments"/>.</returns>
    internal static string BuildCliArguments(string projectPath)
    {
        return $"build \"{projectPath}\" -c Release -nologo --verbosity quiet";
    }

    private static IReadOnlyList<string> EnumerateAssemblies(string outputFolder)
    {
        // The simplest filter: any *.dll under the output is a candidate. We exclude PDB
        // files implicitly by matching only *.dll. We exclude likely-native binaries by
        // skipping anything in a `runtimes/` subfolder (which `dotnet publish` uses for
        // RID-specific natives; `dotnet build` rarely emits them but be defensive).
        var dlls = new List<string>();
        foreach (string path in Directory.EnumerateFiles(outputFolder, "*.dll", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.IndexOf("/runtimes/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            dlls.Add(path);
        }

        // Sort for determinism so logs / Output pane entries are stable.
        dlls.Sort(StringComparer.OrdinalIgnoreCase);
        return dlls;
    }

    /// <summary>
    /// Builds the SDK-style csproj XML for a fresh restore. Exposed internal for unit tests.
    /// </summary>
    /// <param name="references">The packages to add.</param>
    /// <returns>The project file XML, suitable for <see cref="File.WriteAllText(string, string)"/>.</returns>
    internal static string BuildProjectXml(IReadOnlyList<NuGetReference> references)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net48</TargetFramework>");
        sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");
        sb.AppendLine("    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>");
        sb.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>");
        sb.AppendLine("    <OutputPath>bin\\</OutputPath>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var r in references)
        {
            sb.Append("    <PackageReference Include=\"");
            sb.Append(EscapeXml(r.PackageId));
            sb.Append("\" Version=\"");
            sb.Append(EscapeXml(r.Version));
            sb.AppendLine("\" />");
        }

        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string BuildProgressMessage(IReadOnlyList<NuGetReference> references)
    {
        if (references.Count == 1)
        {
            var r = references[0];
            return $"Macros: Restoring NuGet package {r.PackageId} {r.Version}…";
        }

        return $"Macros: Restoring {references.Count} NuGet packages…";
    }

    private static string BuildFailureMessage(IReadOnlyList<NuGetReference> references, ProcessRunResult result)
    {
        var sb = new StringBuilder();
        sb.Append("NuGet restore failed for ");
        sb.Append(string.Join(", ", references.Select(r => $"{r.PackageId} {r.Version}")));
        sb.Append(" (exit code ").Append(result.ExitCode).AppendLine(").");

        // Captured CLI output can be huge for transitive errors; truncate to keep Output
        // pane and Error List entries readable. The full log lives in the cache folder
        // for users to inspect manually.
        const int MaxOutputChars = 4000;
        string combined = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
        if (combined.Length > MaxOutputChars)
        {
            combined = "…(truncated)…\n" + combined.Substring(combined.Length - MaxOutputChars);
        }

        if (combined.Length > 0)
        {
            sb.AppendLine().AppendLine(combined);
        }

        return sb.ToString();
    }

    private static string DefaultCacheRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Macros", "NuGet");
    }
}

/// <summary>Indirection over <see cref="Process"/> to enable unit testing the resolver.</summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Starts the configured process, captures its standard streams, and returns the result
    /// when it exits. Cancellation kills the process if still running.
    /// </summary>
    /// <param name="cancellation">Token observed for the process lifetime.</param>
    /// <returns>The combined exit code + captured stdout / stderr.</returns>
    Task<ProcessRunResult> RunAsync(CancellationToken cancellation);
}

/// <summary>The outcome of an <see cref="IProcessRunner.RunAsync"/> call.</summary>
/// <param name="ExitCode">The process's exit code; <c>0</c> means success.</param>
/// <param name="StandardOutput">Captured stdout (full text).</param>
/// <param name="StandardError">Captured stderr (full text).</param>
internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Captures stdout / stderr concurrently to avoid the classic "buffer fills up, child
/// blocks on write" deadlock.
/// </summary>
internal sealed class RealProcessRunner : IProcessRunner
{
    private readonly ProcessStartInfo _psi;

    /// <summary>Initializes a new runner for the supplied <paramref name="psi"/>.</summary>
    /// <param name="psi">The process configuration to run.</param>
    public RealProcessRunner(ProcessStartInfo psi)
    {
        _psi = psi ?? throw new ArgumentNullException(nameof(psi));
    }

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = _psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the dotnet process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        using (cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* best-effort */ }
            tcs.TrySetCanceled(cancellation);
        }))
        {
            int exit = await tcs.Task.ConfigureAwait(false);

            // Drain any remaining stream events.
            process.WaitForExit();

            string outText, errText;
            lock (stdout) outText = stdout.ToString();
            lock (stderr) errText = stderr.ToString();

            return new ProcessRunResult(exit, outText, errText);
        }
    }
}
