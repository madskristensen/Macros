using System;
using System.IO;

namespace Macros.Engine.Storage;

/// <summary>
/// Static helpers for resolving the on-disk locations the macro storage layer reads and
/// writes. Lives here (not on the options model) so engine consumers can resolve paths
/// without taking a dependency on the VSIX assembly.
/// </summary>
public static class MacrosPaths
{
    /// <summary>
    /// Resolves the effective global macros folder. When <paramref name="configuredFolder"/>
    /// is <see langword="null"/>, empty, or whitespace, the default
    /// <c>%APPDATA%\Macros</c> is returned. Otherwise the configured value is returned
    /// verbatim — callers (and the options page) are responsible for validating the path.
    /// </summary>
    /// <param name="configuredFolder">
    /// The value of <c>MacrosOptions.GlobalMacrosFolder</c>, which is initialized to the
    /// empty string by the toolkit and only populated when the user overrides it.
    /// </param>
    public static string ResolveGlobalFolder(string? configuredFolder)
    {
        if (!string.IsNullOrWhiteSpace(configuredFolder))
        {
            return configuredFolder!;
        }

        return GetDefaultGlobalFolder();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="configuredFolder"/> is either
    /// empty (the "use default" sentinel) or a syntactically valid absolute / relative
    /// path that the file-system layer can later try to create. Returns
    /// <see langword="false"/> for paths that contain Windows-illegal characters, are
    /// rooted on a malformed drive specifier, or otherwise fail
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    /// <remarks>
    /// Pure syntactic validation — does NOT touch the file system. A valid syntax does
    /// not guarantee the path exists or is writable; callers still need to handle disk
    /// errors when they actually try to write. The point of this helper is to catch the
    /// "user typed garbage in Tools → Options" case before it surfaces as a cryptic
    /// <see cref="ArgumentException"/> deep inside the storage layer.
    /// </remarks>
    public static bool IsValidGlobalFolder(string? configuredFolder)
    {
        // Empty / whitespace is the "use default" sentinel — always valid because
        // ResolveGlobalFolder will swap in the well-known default.
        if (string.IsNullOrWhiteSpace(configuredFolder))
        {
            return true;
        }

        try
        {
            // Reject Windows-illegal characters explicitly first — Path.GetFullPath on
            // .NET Framework 4.8 silently strips some of them on certain inputs, which
            // would let "Q:\bad|name" sneak through as "Q:\badname".
            foreach (char invalid in Path.GetInvalidPathChars())
            {
                if (configuredFolder!.IndexOf(invalid) >= 0)
                {
                    return false;
                }
            }

            // Path.GetFullPath round-trips the syntactic validation that Directory.CreateDirectory
            // would do later. Any ArgumentException / NotSupportedException / PathTooLongException
            // here means the storage layer would have failed at first save with a worse message.
            _ = Path.GetFullPath(configuredFolder!);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>
    /// Variant of <see cref="ResolveGlobalFolder(string?)"/> that falls back to the
    /// default <c>%APPDATA%\Macros</c> location when <paramref name="configuredFolder"/>
    /// fails <see cref="IsValidGlobalFolder(string?)"/>. The <paramref name="usedFallback"/>
    /// out-parameter tells the caller whether the fallback was triggered, so a one-time
    /// warning can be surfaced (e.g. status bar / Output pane) without crashing the
    /// package on first save.
    /// </summary>
    /// <param name="configuredFolder">
    /// The value of <c>MacrosOptions.GlobalMacrosFolder</c>. May be null/empty (use default)
    /// or a user-typed string that hasn't been validated yet.
    /// </param>
    /// <param name="usedFallback">
    /// <see langword="true"/> when <paramref name="configuredFolder"/> was non-empty but
    /// invalid and the default was substituted; <see langword="false"/> when the default
    /// was used because nothing was configured, or when the configured value passed
    /// validation and was returned verbatim.
    /// </param>
    public static string ResolveGlobalFolderOrFallback(string? configuredFolder, out bool usedFallback)
    {
        if (string.IsNullOrWhiteSpace(configuredFolder))
        {
            usedFallback = false;
            return GetDefaultGlobalFolder();
        }

        if (!IsValidGlobalFolder(configuredFolder))
        {
            usedFallback = true;
            return GetDefaultGlobalFolder();
        }

        usedFallback = false;
        return configuredFolder!;
    }

    private static string GetDefaultGlobalFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macros");

    /// <summary>
    /// Resolves the absolute path of the per-solution repo macros folder, or
    /// <see langword="null"/> when no solution is open. Pure path computation — does not
    /// touch the file system.
    /// </summary>
    /// <param name="solutionDirectory">
    /// The directory of the active <c>.sln</c> / <c>.slnx</c>, typically obtained via
    /// <c>Path.GetDirectoryName(VS.Solutions.GetCurrentSolutionAsync().FullPath)</c>.
    /// <see langword="null"/> or empty signals "no solution open".
    /// </param>
    /// <param name="repoFolderName">
    /// The configurable subfolder name under <c>.vs\</c>. Wired from
    /// <c>MacrosOptions.RepoMacrosFolderName</c> (default <c>"Macros"</c>).
    /// </param>
    public static string? ResolveRepoFolder(string? solutionDirectory, string repoFolderName)
    {
        if (string.IsNullOrEmpty(solutionDirectory))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(repoFolderName))
        {
            // Fall back to the documented default rather than producing a broken path
            // that ends in ".vs\" — the options page keeps this from happening in
            // practice, but defending here keeps the helper total.
            repoFolderName = "Macros";
        }

        return Path.Combine(solutionDirectory!, ".vs", repoFolderName);
    }
}
