using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Macros.Engine.Scripting.NuGet;

/// <summary>
/// Roslyn <see cref="MetadataReferenceResolver"/> that recognizes the
/// <c>#r "nuget: PkgId, Version"</c> directive convention and resolves it via an
/// <see cref="INuGetPackageResolver"/>. Every other <c>#r</c> is delegated to an inner
/// resolver (typically the existing <c>InteropAwareMetadataResolver</c>) so the
/// behaviour for plain DLL paths, the EnvDTE simple-name shim, and user-supplied
/// absolute paths is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's resolver contract is synchronous (<see cref="ResolveReference"/> returns
/// directly), so this class blocks on <see cref="INuGetPackageResolver.ResolveAsync"/>
/// using <see cref="System.Threading.Tasks.Task{T}.GetAwaiter"/>. Compilation runs on
/// the threadpool from <c>MacroPlayer.PlayAsync</c>, so the synchronous wait is
/// acceptable — the UI thread is not blocked. First-time resolution can still take
/// seconds; the player surfaces a status-bar message via the <see cref="IProgress{T}"/>
/// callback.
/// </para>
/// <para>
/// Once a NuGet directive has been resolved, the resulting set of assembly paths is
/// cached on this resolver instance so a single Roslyn compile that calls back for
/// the same directive multiple times only pays the resolver cost once.
/// </para>
/// </remarks>
internal sealed class NuGetMetadataReferenceResolver : MetadataReferenceResolver
{
    private readonly MetadataReferenceResolver _inner;
    private readonly INuGetPackageResolver _packages;
    private readonly IProgress<string>? _progress;

    private readonly ConcurrentDictionary<NuGetReference, ImmutableArray<PortableExecutableReference>> _cache
        = new();

    /// <summary>Initializes a new instance.</summary>
    /// <param name="inner">Resolver that handles non-<c>nuget:</c> references.</param>
    /// <param name="packages">The package resolver (CLI or test fake).</param>
    /// <param name="progress">Optional status callback for first-time restores.</param>
    /// <exception cref="ArgumentNullException">Either required argument is <see langword="null"/>.</exception>
    public NuGetMetadataReferenceResolver(
        MetadataReferenceResolver inner,
        INuGetPackageResolver packages,
        IProgress<string>? progress = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _progress = progress;
    }

    /// <inheritdoc />
    public override bool ResolveMissingAssemblies => _inner.ResolveMissingAssemblies;

    /// <inheritdoc />
    public override ImmutableArray<PortableExecutableReference> ResolveReference(
        string reference,
        string? baseFilePath,
        MetadataReferenceProperties properties)
    {
        if (NuGetReferenceParser.TryParseRoslynReference(reference, out NuGetReference parsed))
        {
            return ResolveNuGet(parsed, properties);
        }

        return _inner.ResolveReference(reference, baseFilePath, properties);
    }

    /// <inheritdoc />
    public override PortableExecutableReference? ResolveMissingAssembly(
        MetadataReference definition,
        AssemblyIdentity referenceIdentity)
        => _inner.ResolveMissingAssembly(definition, referenceIdentity);

    /// <summary>Reference-equality is sufficient because each MacroPlayer invocation builds a fresh resolver.</summary>
    public override bool Equals(object? other) => ReferenceEquals(this, other);

    /// <summary>Stable hash that doesn't conflict with sibling resolvers.</summary>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    private ImmutableArray<PortableExecutableReference> ResolveNuGet(NuGetReference parsed, MetadataReferenceProperties properties)
    {
        if (_cache.TryGetValue(parsed, out var cached))
        {
            return cached;
        }

        // Block the threadpool thread on the async resolver. We deliberately do NOT use
        // JoinableTaskFactory here — Roslyn invokes us from a plain Task.Run inside
        // MacroPlayer, with no UI-thread affinity to preserve. The threading analyzer
        // (VSTHRD002) flags this; the suppression is intentional and scoped to this site.
        IReadOnlyList<string> paths;
        try
        {
#pragma warning disable VSTHRD002 // Roslyn's MetadataReferenceResolver contract is sync-only.
            paths = _packages.ResolveAsync(new[] { parsed }, _progress, CancellationToken.None)
                .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
        catch (NuGetResolveException ex)
        {
            // Surface as a Roslyn diagnostic rather than crashing the compile. Returning
            // empty here causes Roslyn to emit CS0006 (metadata file not found) which is
            // the right semantic. We also throw an inner-message-rich exception so the
            // player's existing failure surface (Output pane + Error List) renders the
            // root cause.
            throw new FileNotFoundException(ex.Message, parsed.PackageId, ex);
        }

        var refs = ImmutableArray.CreateBuilder<PortableExecutableReference>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                refs.Add(MetadataReference.CreateFromFile(path, properties));
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException)
            {
                // Native and resource DLLs occasionally slip through. Skipping is safer
                // than failing the whole compile.
            }
        }

        var result = refs.ToImmutable();
        _cache[parsed] = result;
        return result;
    }
}
