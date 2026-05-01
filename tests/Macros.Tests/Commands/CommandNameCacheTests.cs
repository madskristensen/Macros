using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Commands;
using Xunit;

namespace Macros.Tests.Commands;

public sealed class CommandNameCacheTests
{
    // Each test gets its own instance to avoid shared-singleton pollution.
    private static CommandNameCache MakeCache() => new CommandNameCache();

    private static readonly Guid GroupA = new("A1A2A3A4-B1B2-C1C2-D1D2-E1E2E3E4E5E6");
    private static readonly Guid GroupB = new("B1B2B3B4-A1A2-C1C2-D1D2-E1E2E3E4E5E6");

    // ------------------------------------------------------------------ empty cache

    [Fact]
    public void TryGetCommand_EmptyCache_ReturnsFalse()
    {
        var cache = MakeCache();
        var found = cache.TryGetCommand("File.Save", out _, out _);
        Assert.False(found);
    }

    [Fact]
    public void TryGetName_EmptyCache_ReturnsFalse()
    {
        var cache = MakeCache();
        var found = cache.TryGetName((GroupA, 1u), out _);
        Assert.False(found);
    }

    [Fact]
    public void Lookup_EmptyCache_ReturnsNull()
    {
        var cache = MakeCache();
        Assert.Null(cache.Lookup(GroupA, 1u));
    }

    // ------------------------------------------------------------------ after TryAddName

    [Fact]
    public void TryGetCommand_AfterAdd_ReturnsTrue()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 42u, "File.Save");

        var found = cache.TryGetCommand("File.Save", out var g, out var id);
        Assert.True(found);
        Assert.Equal(GroupA, g);
        Assert.Equal(42u, id);
    }

    [Fact]
    public void TryGetName_AfterAdd_ReturnsTrue()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 42u, "File.Save");

        var found = cache.TryGetName((GroupA, 42u), out var name);
        Assert.True(found);
        Assert.Equal("File.Save", name);
    }

    [Fact]
    public void Lookup_AfterAdd_ReturnsName()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 7u, "Build.BuildSolution");

        Assert.Equal("Build.BuildSolution", cache.Lookup(GroupA, 7u));
    }

    [Fact]
    public void TryGetCommand_IsCaseInsensitive()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 1u, "File.Save");

        Assert.True(cache.TryGetCommand("file.save", out _, out _));
        Assert.True(cache.TryGetCommand("FILE.SAVE", out _, out _));
    }

    [Fact]
    public void Lookup_UnknownId_ReturnsNull()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 1u, "File.Save");

        Assert.Null(cache.Lookup(GroupB, 1u));
        Assert.Null(cache.Lookup(GroupA, 999u));
    }

    [Fact]
    public void TryAddName_EmptyOrNullName_IsIgnored()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 1u, "");
        cache.TryAddName(GroupA, 2u, null!);

        Assert.Null(cache.Lookup(GroupA, 1u));
        Assert.Null(cache.Lookup(GroupA, 2u));
    }

    [Fact]
    public void TryAddName_OverwritesExistingEntry()
    {
        var cache = MakeCache();
        cache.TryAddName(GroupA, 1u, "File.Save");
        cache.TryAddName(GroupA, 1u, "File.SaveAll");

        Assert.Equal("File.SaveAll", cache.Lookup(GroupA, 1u));
    }

    // ------------------------------------------------------------------ thread safety

    [Fact]
    public async Task ThreadSafety_ParallelWritesAndReads_DoNotThrow()
    {
        var cache = MakeCache();
        const int N = 100;

        // Kick off N writers in parallel.
        var writers = new List<Task>(N);
        for (var i = 0; i < N; i++)
        {
            var captured = i;
            writers.Add(Task.Run(() =>
                cache.TryAddName(GroupA, (uint)captured, $"Cmd.{captured}")));
        }

        // Kick off N readers that overlap with the writers.
        var readers = new List<Task>(N);
        for (var i = 0; i < N; i++)
        {
            var captured = i;
            readers.Add(Task.Run(() =>
            {
                cache.TryGetCommand($"Cmd.{captured}", out _, out _);
                cache.TryGetName((GroupA, (uint)captured), out _);
                _ = cache.Lookup(GroupA, (uint)captured);
            }));
        }

        await Task.WhenAll(writers);
        await Task.WhenAll(readers);

        // After all writers finished, all names must be present.
        for (var i = 0; i < N; i++)
        {
            Assert.Equal($"Cmd.{i}", cache.Lookup(GroupA, (uint)i));
        }
    }
}
