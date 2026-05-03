using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Scripting.NuGet;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Macros.Tests.Scripting.NuGet;

/// <summary>
/// Tests for <see cref="NuGetMetadataReferenceResolver"/>. Verifies the dispatch contract
/// (nuget directives go to the package resolver, everything else delegates to the inner
/// resolver) and that compilation-time failures from the package resolver produce a
/// useful Roslyn diagnostic.
/// </summary>
public sealed class NuGetMetadataReferenceResolverTests
{
    [Fact]
    public void ResolveReference_NonNuGet_DelegatesToInner()
    {
        var inner = new RecordingResolver();
        var packages = new ThrowingPackageResolver();
        var resolver = new NuGetMetadataReferenceResolver(inner, packages);

        _ = resolver.ResolveReference("System.Net.Http", baseFilePath: null, MetadataReferenceProperties.Assembly);

        Assert.Equal(new[] { "System.Net.Http" }, inner.Calls);
    }

    [Fact]
    public void ResolveReference_PlainAbsolutePath_DelegatesToInner()
    {
        var inner = new RecordingResolver();
        var packages = new ThrowingPackageResolver();
        var resolver = new NuGetMetadataReferenceResolver(inner, packages);

        _ = resolver.ResolveReference(@"C:\path\to\some.dll", baseFilePath: null, MetadataReferenceProperties.Assembly);

        Assert.Equal(new[] { @"C:\path\to\some.dll" }, inner.Calls);
    }

    [Fact]
    public void ResolveReference_NuGetDirective_DispatchesToPackageResolver()
    {
        var dll = WriteTempDll("Pkg.dll");
        try
        {
            var inner = new RecordingResolver();
            var packages = new FakePackageResolver(new[] { dll });
            var resolver = new NuGetMetadataReferenceResolver(inner, packages);

            var refs = resolver.ResolveReference("nuget: Pkg, 1.0.0", baseFilePath: null, MetadataReferenceProperties.Assembly);

            Assert.Equal(1, packages.CallCount);
            Assert.Empty(inner.Calls);
            Assert.Single(refs);
        }
        finally
        {
            try { File.Delete(dll); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ResolveReference_PackageResolverFailure_ThrowsFileNotFound()
    {
        var inner = new RecordingResolver();
        var packages = new FailingPackageResolver(new NuGetResolveException("NU1101: package not found"));
        var resolver = new NuGetMetadataReferenceResolver(inner, packages);

        var ex = Assert.Throws<FileNotFoundException>(() =>
            resolver.ResolveReference("nuget: Missing.Pkg, 1.0.0", baseFilePath: null, MetadataReferenceProperties.Assembly));

        Assert.Contains("NU1101", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveReference_CachesPerNuGetReference()
    {
        var dll = WriteTempDll("CachePkg.dll");
        try
        {
            var packages = new FakePackageResolver(new[] { dll });
            var resolver = new NuGetMetadataReferenceResolver(new NoopResolver(), packages);

            _ = resolver.ResolveReference("nuget: CachePkg, 1.0.0", null, MetadataReferenceProperties.Assembly);
            _ = resolver.ResolveReference("nuget: CachePkg, 1.0.0", null, MetadataReferenceProperties.Assembly);
            _ = resolver.ResolveReference("nuget: CachePkg, 1.0.0", null, MetadataReferenceProperties.Assembly);

            Assert.Equal(1, packages.CallCount);
        }
        finally
        {
            try { File.Delete(dll); } catch { /* best-effort */ }
        }
    }

    private static string WriteTempDll(string fileName)
    {
        // MetadataReference.CreateFromFile validates the file exists and is a PE image; a
        // zero-byte file would throw BadImageFormatException. The simplest workaround is
        // to copy an existing assembly we already reference (System.dll is loaded into
        // every test process).
        string source = typeof(string).Assembly.Location;
        string dest = Path.Combine(Path.GetTempPath(), $"Macros-Tests-{Guid.NewGuid():N}-{fileName}");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private sealed class RecordingResolver : MetadataReferenceResolver
    {
        public List<string> Calls { get; } = new();

        public override bool ResolveMissingAssemblies => false;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
        {
            Calls.Add(reference);
            return ImmutableArray<PortableExecutableReference>.Empty;
        }

        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => 0;
    }

    private sealed class NoopResolver : MetadataReferenceResolver
    {
        public override bool ResolveMissingAssemblies => false;
        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
            => ImmutableArray<PortableExecutableReference>.Empty;
        public override bool Equals(object? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => 0;
    }

    private sealed class FakePackageResolver : INuGetPackageResolver
    {
        private readonly IReadOnlyList<string> _paths;

        public FakePackageResolver(IReadOnlyList<string> paths)
        {
            _paths = paths;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<NuGetReference> references, IProgress<string>? progress, CancellationToken cancellation)
        {
            CallCount++;
            return Task.FromResult(_paths);
        }
    }

    private sealed class FailingPackageResolver : INuGetPackageResolver
    {
        private readonly Exception _ex;

        public FailingPackageResolver(Exception ex) => _ex = ex;

        public Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<NuGetReference> references, IProgress<string>? progress, CancellationToken cancellation)
            => Task.FromException<IReadOnlyList<string>>(_ex);
    }

    private sealed class ThrowingPackageResolver : INuGetPackageResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<NuGetReference> references, IProgress<string>? progress, CancellationToken cancellation)
            => throw new InvalidOperationException("ThrowingPackageResolver should never be called for non-nuget references.");
    }
}
