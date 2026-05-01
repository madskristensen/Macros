using System;
using System.IO;

namespace Macros.Tests.TestUtilities;

/// <summary>
/// Resolves the on-disk path of the built <c>Macros.dll</c> (the VSIX assembly) for
/// reflection / <see cref="System.Reflection.MetadataLoadContext"/>-based smoke tests.
/// </summary>
/// <remarks>
/// <para>
/// Centralised so that build-layout changes (per-project <c>bin\&lt;Config&gt;\net48</c>
/// vs. flat <c>OutDir=\_built</c> in CI) only have to be handled in one place.
/// Historically each smoke-test class duplicated this logic and any new build layout
/// silently broke a fresh subset of tests.
/// </para>
/// <para>
/// Lookup order:
/// <list type="number">
///   <item><description>Probe alongside the test assembly (<c>AppContext.BaseDirectory\Macros.dll</c>).
///     Covers any layout where <c>Macros.dll</c> is copied into the test bin — including
///     CI's flat <c>/p:OutDir=\_built</c> output and the default <c>&lt;ProjectReference&gt;</c>
///     copy-local behaviour.</description></item>
///   <item><description>Walk back to the standard per-project layout
///     <c>tests\Macros.Tests\bin\&lt;Config&gt;\net48 → src\Macros\bin\&lt;Config&gt;\net48\Macros.dll</c>.
///     Preserves local Visual Studio Debug/Release behaviour.</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class MacrosAssemblyLocator
{
    /// <summary>
    /// Returns the absolute path to the built <c>Macros.dll</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the assembly cannot be located in either the test bin directory
    /// or the standard per-project output path.
    /// </exception>
    public static string Locate()
    {
        string testBin = AppContext.BaseDirectory;

        // 1. Flat / copy-local layout — fastest path and works for CI's OutDir=\_built.
        string siblingCandidate = Path.Combine(testBin, "Macros.dll");
        if (File.Exists(siblingCandidate))
        {
            return siblingCandidate;
        }

        // 2. Standard per-project layout fallback for local dev:
        //    tests\Macros.Tests\bin\<Config>\net48  →  ..\..\..\..\..\src\Macros\bin\<Config>\net48\Macros.dll
        string config = new DirectoryInfo(testBin).Parent!.Name; // "Debug" or "Release"
        string repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));
        string perProjectCandidate = Path.Combine(repoRoot, "src", "Macros", "bin", config, "net48", "Macros.dll");
        if (File.Exists(perProjectCandidate))
        {
            return perProjectCandidate;
        }

        throw new FileNotFoundException(
            $"Could not locate built Macros.dll next to the test assembly ('{siblingCandidate}') " +
            $"or at the per-project path ('{perProjectCandidate}'). " +
            "Ensure the Macros VSIX project has been built before running these tests.",
            perProjectCandidate);
    }
}
