using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Engine.Scripting.NuGet;

/// <summary>
/// Resolves a list of <see cref="NuGetReference"/> entries into the absolute file paths
/// of the assemblies that need to be referenced when compiling a macro script.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to cache results so subsequent calls with the same
/// reference set complete in milliseconds without network access. The first call for a
/// given set may legitimately take seconds (download + extract + restore).
/// </para>
/// <para>
/// Failures (network, missing dotnet SDK, package not found, version conflict) are
/// surfaced as <see cref="NuGetResolveException"/> with a user-readable <c>Message</c>
/// suitable for direct rendering into the Macros Output pane and Error List.
/// </para>
/// </remarks>
internal interface INuGetPackageResolver
{
    /// <summary>
    /// Resolves <paramref name="references"/> into a flat list of absolute assembly paths
    /// (the package's own DLLs plus its transitive dependency closure, filtered to
    /// managed assemblies only — native binaries are excluded).
    /// </summary>
    /// <param name="references">The packages to resolve. Empty input returns an empty result.</param>
    /// <param name="progress">
    /// Optional progress callback invoked with short user-facing status strings
    /// (e.g. <c>"Restoring 2 NuGet packages..."</c>). Implementations may ignore this.
    /// </param>
    /// <param name="cancellation">Cancellation token observed during the (possibly long) restore.</param>
    /// <returns>An ordered list of absolute paths to managed assemblies.</returns>
    /// <exception cref="NuGetResolveException">Resolution failed; <c>Message</c> is user-facing.</exception>
    /// <exception cref="System.OperationCanceledException">Cancelled via <paramref name="cancellation"/>.</exception>
    Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<NuGetReference> references,
        IProgress<string>? progress,
        CancellationToken cancellation);
}

/// <summary>Surfaces a NuGet resolve failure to the player with a user-actionable message.</summary>
internal sealed class NuGetResolveException : System.Exception
{
    /// <summary>Initializes a new instance with the supplied message.</summary>
    /// <param name="message">User-facing error text.</param>
    public NuGetResolveException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the supplied message and underlying cause.</summary>
    /// <param name="message">User-facing error text.</param>
    /// <param name="inner">The underlying exception (e.g. <see cref="System.IO.IOException"/>).</param>
    public NuGetResolveException(string message, System.Exception inner) : base(message, inner)
    {
    }
}
