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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macros");
    }

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
