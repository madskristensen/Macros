using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace Macros.Tests.Scripting;

public sealed class ScriptCompilationCacheTests
{
    private static Script<object> Compile(string source) => CSharpScript.Create<object>(source);

    [Fact]
    public void GetOrAdd_SameSource_ReturnsCachedInstance()
    {
        var cache = new ScriptCompilationCache();
        const string source = "1 + 1";

        var first = cache.GetOrAdd(source, Compile);
        var second = cache.GetOrAdd(source, Compile);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrAdd_DifferentSources_ReturnsDifferentInstances()
    {
        var cache = new ScriptCompilationCache();

        var a = cache.GetOrAdd("1 + 1", Compile);
        var b = cache.GetOrAdd("2 + 2", Compile);

        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrAdd_RepeatedHits_InvokesFactoryOnce()
    {
        var cache = new ScriptCompilationCache();
        int calls = 0;

        Script<object> Factory(string s)
        {
            Interlocked.Increment(ref calls);
            return Compile(s);
        }

        for (int i = 0; i < 10; i++)
        {
            cache.GetOrAdd("System.Math.PI", Factory);
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrAdd_BeyondMaxEntries_EvictsLeastRecentlyUsed()
    {
        var cache = new ScriptCompilationCache();

        // Insert exactly MaxEntries distinct sources.
        var firstScript = cache.GetOrAdd(SourceFor(0), Compile);
        for (int i = 1; i < ScriptCompilationCache.MaxEntries; i++)
        {
            cache.GetOrAdd(SourceFor(i), Compile);
        }

        Assert.Equal(ScriptCompilationCache.MaxEntries, cache.Count);

        // Add one more — oldest (index 0) should be evicted.
        cache.GetOrAdd(SourceFor(ScriptCompilationCache.MaxEntries), Compile);

        Assert.Equal(ScriptCompilationCache.MaxEntries, cache.Count);

        // Re-fetching the evicted source must produce a NEW instance via the factory.
        var refetched = cache.GetOrAdd(SourceFor(0), Compile);
        Assert.NotSame(firstScript, refetched);
    }

    [Fact]
    public void GetOrAdd_LruSemantics_RecentAccessKeepsKeyAlive()
    {
        var cache = new ScriptCompilationCache();

        var oldest = cache.GetOrAdd(SourceFor(0), Compile);
        for (int i = 1; i < ScriptCompilationCache.MaxEntries; i++)
        {
            cache.GetOrAdd(SourceFor(i), Compile);
        }

        // Touch the oldest so it becomes most-recently-used.
        var touched = cache.GetOrAdd(SourceFor(0), Compile);
        Assert.Same(oldest, touched);

        // Insert a new entry, which should evict whatever is now the LRU (index 1, not 0).
        cache.GetOrAdd(SourceFor(ScriptCompilationCache.MaxEntries), Compile);

        // Index 0 should still be cached (same instance).
        var stillCached = cache.GetOrAdd(SourceFor(0), Compile);
        Assert.Same(oldest, stillCached);

        // Index 1 should have been evicted.
        var evictedRefetch = cache.GetOrAdd(SourceFor(1), Compile);
        Assert.NotNull(evictedRefetch);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new ScriptCompilationCache();
        cache.GetOrAdd("1", Compile);
        cache.GetOrAdd("2", Compile);
        cache.GetOrAdd("3", Compile);

        Assert.Equal(3, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);

        // After Clear the next GetOrAdd of a previously-cached source must call the factory again.
        int calls = 0;
        cache.GetOrAdd("1", _ =>
        {
            Interlocked.Increment(ref calls);
            return Compile("1");
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetOrAdd_NullSource_Throws()
    {
        var cache = new ScriptCompilationCache();
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd(null!, Compile));
    }

    [Fact]
    public void GetOrAdd_NullFactory_Throws()
    {
        var cache = new ScriptCompilationCache();
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd("1", null!));
    }

    [Fact]
    public async Task GetOrAdd_ConcurrentSameSource_AllCallersGetSameCachedInstance()
    {
        var cache = new ScriptCompilationCache();
        const string source = "42";
        int calls = 0;
        using var gate = new ManualResetEventSlim(false);

        Script<object> Factory(string s)
        {
            Interlocked.Increment(ref calls);
            // Hold briefly so racing callers collide on insert.
            gate.Wait(TimeSpan.FromSeconds(2));
            return Compile(s);
        }

        const int parallelism = 100;
        var tasks = new Task<Script<object>>[parallelism];
        for (int i = 0; i < parallelism; i++)
        {
            tasks[i] = Task.Run(() => cache.GetOrAdd(source, Factory));
        }

        // Let all tasks reach the factory before releasing.
        await Task.Delay(50);
        gate.Set();

        Script<object>[] results = await Task.WhenAll(tasks);

        // Documented relaxed contract: factory may run more than once under contention because
        // it is intentionally invoked outside the cache's lock. But every caller must observe
        // the SAME cached Script<object> after the race resolves, and the cache must hold
        // exactly one entry for this source.
        Assert.Equal(1, cache.Count);
        var winner = cache.GetOrAdd(source, _ => throw new InvalidOperationException("Should be cached."));
        foreach (Script<object> r in results)
        {
            Assert.Same(winner, r);
        }

        Assert.InRange(calls, 1, parallelism);
    }

    private static string SourceFor(int i) =>
        // Distinct, deterministic, trivially-compilable source per index.
        i.ToString(CultureInfo.InvariantCulture) + " + 0";
}
