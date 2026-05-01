using System;
using Macros.Options;
using Xunit;

namespace Macros.Tests.Options;

/// <summary>
/// Unit tests for the <see cref="MacrosOptions.Changed"/> static event and the
/// <see cref="MacrosOptions.Save"/> override that fires it.
/// </summary>
/// <remarks>
/// <see cref="MacrosOptions.Save"/> calls <c>base.Save()</c> which requires the VS settings
/// store; outside a VS host it throws. The implementation wraps <c>base.Save()</c> in a
/// <see langword="try"/>/<see langword="finally"/> so <see cref="MacrosOptions.Changed"/>
/// fires regardless — these tests rely on that contract.
/// </remarks>
public sealed class MacrosOptionsChangedEventTests
{
    /// <summary>
    /// Subscribes, calls Save, verifies Changed fired, then unsubscribes to avoid state leak.
    /// </summary>
    [Fact]
    public void Save_RaisesChangedEvent()
    {
        bool raised = false;
        EventHandler handler = (_, _) => raised = true;
        MacrosOptions.Changed += handler;
        try
        {
            var opts = new MacrosOptions();
            try { opts.Save(); } catch { /* base.Save() may throw outside VS */ }
            Assert.True(raised);
        }
        finally
        {
            MacrosOptions.Changed -= handler;
        }
    }

    [Fact]
    public void Save_MultipleSubscribers_AllReceive()
    {
        int callCount = 0;
        EventHandler h1 = (_, _) => callCount++;
        EventHandler h2 = (_, _) => callCount++;
        MacrosOptions.Changed += h1;
        MacrosOptions.Changed += h2;
        try
        {
            var opts = new MacrosOptions();
            try { opts.Save(); } catch { /* outside VS */ }
            Assert.Equal(2, callCount);
        }
        finally
        {
            MacrosOptions.Changed -= h1;
            MacrosOptions.Changed -= h2;
        }
    }

    [Fact]
    public void Save_AfterUnsubscribe_DoesNotReceive()
    {
        bool raised = false;
        EventHandler handler = (_, _) => raised = true;
        MacrosOptions.Changed += handler;
        MacrosOptions.Changed -= handler;

        var opts = new MacrosOptions();
        try { opts.Save(); } catch { /* outside VS */ }
        Assert.False(raised);
    }
}
