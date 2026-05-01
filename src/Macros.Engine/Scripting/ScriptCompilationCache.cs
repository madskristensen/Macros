using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Macros.Engine.Scripting;

/// <summary>
/// Caches compiled Roslyn <see cref="Script{TResult}"/> instances keyed by the SHA-256 hash
/// of the source text, with an LRU eviction policy.
/// </summary>
/// <remarks>
/// <para>
/// Compiling a <c>.csx</c> script via <see cref="CSharpScript"/> takes ~200–500 ms on first
/// run; cached re-plays are effectively instant. The cache is keyed by content hash so that
/// two macros with identical source share a compiled <see cref="Script{TResult}"/>.
/// </para>
/// <para>
/// <b>Threading.</b> All public members are thread-safe. Mutations to the dictionary and the
/// LRU linked list happen under a single monitor (<c>_sync</c>). The user-supplied
/// <c>factory</c> delegate is called <em>without</em> the lock held — compilation is
/// expensive, and we don't want to block other readers/writers while it runs. Two callers
/// racing to compile the same source may both invoke <c>factory</c>; only the first inserted
/// entry wins, and both callers receive that single cached instance after the race resolves.
/// </para>
/// </remarks>
internal sealed class ScriptCompilationCache
{
    /// <summary>Maximum number of cached compilations before LRU eviction kicks in.</summary>
    public const int MaxEntries = 50;

    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.Ordinal);

    // Most-recently-used at the head, least-recently-used at the tail.
    private readonly LinkedList<CacheEntry> _lru = new();

    /// <summary>Gets the current number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached compiled script for <paramref name="source"/>, or invokes
    /// <paramref name="factory"/> to produce one and caches the result.
    /// </summary>
    /// <param name="source">The C# script source text.</param>
    /// <param name="factory">
    /// Compiles the script for the given source. Receives the source text as input. Called
    /// with no lock held; may be invoked concurrently for the same source under contention,
    /// but only one resulting <see cref="Script{TResult}"/> is ever cached.
    /// </param>
    /// <returns>The cached or newly compiled script.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> or <paramref name="factory"/> is <see langword="null"/>.
    /// </exception>
    public Script<object> GetOrAdd(string source, Func<string, Script<object>> factory)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        string key = ComputeSha256(source);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry> existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Script;
            }
        }

        // Compile outside the lock — Roslyn compilation is CPU-heavy.
        Script<object> compiled = factory(source);
        if (compiled is null)
        {
            throw new InvalidOperationException("Script factory returned null.");
        }

        lock (_sync)
        {
            // Re-check: another thread may have inserted while we were compiling.
            if (_entries.TryGetValue(key, out LinkedListNode<CacheEntry> raced))
            {
                _lru.Remove(raced);
                _lru.AddFirst(raced);
                return raced.Value.Script;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, compiled));
            _lru.AddFirst(node);
            _entries.Add(key, node);

            if (_entries.Count > MaxEntries)
            {
                LinkedListNode<CacheEntry> tail = _lru.Last!;
                _lru.RemoveLast();
                _entries.Remove(tail.Value.Key);
            }

            return compiled;
        }
    }

    /// <summary>Removes all cached entries.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private static string ComputeSha256(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private readonly struct CacheEntry
    {
        public CacheEntry(string key, Script<object> script)
        {
            Key = key;
            Script = script;
        }

        public string Key { get; }

        public Script<object> Script { get; }
    }
}
