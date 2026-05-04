using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Macros.Tests.TestUtilities;

/// <summary>
/// Builds <see cref="MetadataLoadContext"/> instances for metadata-only (no JIT)
/// inspection of the project's compiled assemblies. Centralises the search-path
/// boilerplate that smoke tests would otherwise copy-paste.
/// </summary>
/// <remarks>
/// Two flavours are exposed:
/// <list type="bullet">
///   <item><description><see cref="CreateForMacrosVsix"/> — loads the
///     <c>Macros.dll</c> VSIX assembly. Cannot be runtime-loaded outside a hosted VS
///     process because of its transitive Microsoft.VisualStudio.Shell dependency, so we
///     read it through metadata.</description></item>
///   <item><description><see cref="CreateForEngine"/> — loads
///     <c>Macros.Engine.dll</c>. The engine references the VS SDK with
///     <c>ExcludeAssets=Runtime</c>, so EnvDTE / EnvDTE80 / Microsoft.VisualStudio.Interop
///     are not deployed to the engine bin; this helper pulls them in from the user's
///     NuGet package cache so signatures involving <c>EnvDTE80.DTE2</c> resolve cleanly
///     under metadata inspection.</description></item>
/// </list>
/// </remarks>
internal static class MetadataContextFactory
{
    /// <summary>
    /// Creates a <see cref="MetadataLoadContext"/> rooted at the built <c>Macros.dll</c>
    /// VSIX assembly. The returned context is owned by the caller and must be disposed.
    /// </summary>
    public static MetadataLoadContext CreateForMacrosVsix(out Assembly macrosAssembly)
    {
        string macrosDll = MacrosAssemblyLocator.Locate();
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

    /// <summary>
    /// Creates a <see cref="MetadataLoadContext"/> rooted at the built
    /// <c>Macros.Engine.dll</c> assembly, with EnvDTE / Microsoft.VisualStudio.Interop
    /// pulled in from the user's NuGet cache so DTE-bound signatures resolve.
    /// </summary>
    /// <param name="engineAssembly">Receives the loaded engine assembly.</param>
    /// <param name="anchorType">
    /// A type whose <c>Assembly.Location</c> identifies the engine bin folder. Defaults
    /// to <c>typeof(Macros.Engine.Scripting.MacroGlobals)</c> when null is passed.
    /// </param>
    public static MetadataLoadContext CreateForEngine(
        out Assembly engineAssembly,
        Type? anchorType = null)
    {
        Type anchor = anchorType ?? typeof(Macros.Engine.Scripting.MacroGlobals);
        string engineDll = anchor.Assembly.Location;
        string engineBinDir = Path.GetDirectoryName(engineDll)!;
        string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

        // Pull EnvDTE / Interop from NuGet cache — they're ExcludeAssets=Runtime in the
        // engine project, so they aren't copied to the engine bin folder.
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

        // De-dupe by file *name* (first wins) so we don't hand the resolver two different
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
}
