using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the contract of <see cref="TriggerReentranceGuard"/>: depth tracking, per-key
/// suppression, and AsyncLocal isolation between parent and child execution contexts.
/// </summary>
public sealed class TriggerReentranceGuardTests
{
    [Fact]
    public void TryEnter_FirstKey_ReturnsTrueDepthOne()
    {
        var guard = new TriggerReentranceGuard();

        var entered = guard.TryEnter("a", out var scope);

        Assert.True(entered);
        Assert.NotNull(scope);
        Assert.Equal(1, guard.CurrentDepth);

        scope!.Dispose();
    }

    [Fact]
    public void TryEnter_SameKeyWhileActive_ReturnsFalse()
    {
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("a", out var outer);

        var second = guard.TryEnter("a", out var scope);

        Assert.False(second);
        Assert.Null(scope);

        outer!.Dispose();
    }

    [Fact]
    public void TryEnter_DifferentKeyWhileActive_ReturnsTrueDepthTwo()
    {
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("a", out var outer);

        var entered = guard.TryEnter("b", out var inner);

        Assert.True(entered);
        Assert.Equal(2, guard.CurrentDepth);

        inner!.Dispose();
        outer!.Dispose();
    }

    [Fact]
    public void TryEnter_AtMaxDepth_ReturnsFalse()
    {
        var guard = new TriggerReentranceGuard();

        // Fill up to MaxDepth with distinct keys.
        var scopes = new List<IDisposable>();
        for (int i = 0; i < TriggerReentranceGuard.MaxDepth; i++)
        {
            var ok = guard.TryEnter($"key-{i}", out var s);
            Assert.True(ok, $"expected success at depth {i + 1}");
            scopes.Add(s!);
        }

        Assert.Equal(TriggerReentranceGuard.MaxDepth, guard.CurrentDepth);

        // Any further attempt — even a new key — must be refused.
        var blocked = guard.TryEnter("new-key", out var blocked2);
        Assert.False(blocked);
        Assert.Null(blocked2);

        foreach (var s in scopes) s.Dispose();
    }

    [Fact]
    public void Dispose_DecrementsDepthAndReleasesKey()
    {
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("a", out var scope);

        Assert.Equal(1, guard.CurrentDepth);
        scope!.Dispose();

        Assert.Equal(0, guard.CurrentDepth);

        // Key "a" should be free again.
        var re = guard.TryEnter("a", out var scope2);
        Assert.True(re);
        scope2!.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("a", out var scope);
        scope!.Dispose();
        scope.Dispose(); // second dispose must not throw or double-decrement
        Assert.Equal(0, guard.CurrentDepth);
    }

    [Fact]
    public void Nested_KeysRestoreOnDispose()
    {
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("a", out var outer);
        guard.TryEnter("b", out var inner);

        inner!.Dispose();
        Assert.Equal(1, guard.CurrentDepth);

        // "b" should be free, "a" still active.
        var reB = guard.TryEnter("b", out var b2);
        Assert.True(reB);

        var reA = guard.TryEnter("a", out _);
        Assert.False(reA);

        b2!.Dispose();
        outer!.Dispose();
        Assert.Equal(0, guard.CurrentDepth);
    }

    [Fact]
    public async Task ChildTask_InheritsParentDepthButIsolatesOwnEntries()
    {
        // AsyncLocal propagates a COPY into child tasks. The child can observe the
        // parent depth but its own TryEnter mutations don't bleed back to the parent.
        var guard = new TriggerReentranceGuard();
        guard.TryEnter("parent-key", out var parentScope);

        int childDepthBeforeEnter = -1;
        bool childEnteredNewKey = false;

        await Task.Run(() =>
        {
            // Child inherits depth=1 from parent.
            childDepthBeforeEnter = guard.CurrentDepth;

            // Child can enter a new key — it doesn't see "parent-key" as owned
            // because AsyncLocal copies are independent value snapshots.
            childEnteredNewKey = guard.TryEnter("child-key", out var childScope);
            childScope?.Dispose();
        });

        // Parent context: depth still 1, "parent-key" still locked.
        Assert.Equal(1, guard.CurrentDepth);
        var parentReenter = guard.TryEnter("parent-key", out _);
        Assert.False(parentReenter);

        parentScope!.Dispose();
    }
}
