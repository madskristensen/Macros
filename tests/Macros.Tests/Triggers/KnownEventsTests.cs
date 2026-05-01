using System;
using System.Linq;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the contract of <see cref="KnownEvents"/>: a lazy, exception-tolerant reflection
/// catalog of VS events. The toolkit assembly may or may not be loaded in the test process
/// depending on what other tests have JIT'd, so the assertions here either accept "empty"
/// (toolkit absent) or, when entries are present, verify their shape.
/// </summary>
public sealed class KnownEventsTests
{
    [Fact]
    public void All_ReturnsNonNullList()
    {
        // Empty is the documented signal that the toolkit isn't loaded — never a null.
        Assert.NotNull(KnownEvents.All);
    }

    [Fact]
    public void All_IsLazy_CalledOnce()
    {
        // The Lazy<> contract: the same instance comes back every time, regardless of how
        // many callers race the first access. If Discover were re-run we'd see a fresh list.
        var first = KnownEvents.All;
        var second = KnownEvents.All;
        Assert.Same(first, second);
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        Assert.Null(KnownEvents.Find("Bogus.NonexistentEvent_xyz_123"));
    }

    [Fact]
    public void Find_KnownName_IsCaseInsensitive()
    {
        // Skip when the toolkit isn't loaded in this test run — we still cover the negative
        // path above, and the impl test suite (m4-macroeventbus-impl) covers VS-loaded paths.
        if (KnownEvents.All.Count == 0)
        {
            return;
        }

        var sample = KnownEvents.All[0];
        var hit = KnownEvents.Find(sample.CanonicalName.ToUpperInvariant());
        Assert.NotNull(hit);
        Assert.Equal(sample.CanonicalName, hit!.CanonicalName);
    }

    [Fact]
    public void Find_BuildSolutionBuildDone_WhenToolkitLoaded()
    {
        if (KnownEvents.All.Count == 0)
        {
            return;
        }

        var hit = KnownEvents.Find("Build.SolutionBuildDone");
        if (hit == null)
        {
            // Toolkit is loaded but this specific event isn't on it (e.g. older version).
            // Don't fail — the discovery contract is what we're testing, not the toolkit's
            // event surface.
            return;
        }

        Assert.Equal("Build.SolutionBuildDone", hit.CanonicalName, ignoreCase: true);
        Assert.Equal("Build", hit.Category);
        Assert.Equal("SolutionBuildDone", hit.EventName);
    }

    [Fact]
    public void All_EveryEntry_HasNonNullFields()
    {
        foreach (var ev in KnownEvents.All)
        {
            Assert.False(string.IsNullOrEmpty(ev.CanonicalName));
            Assert.False(string.IsNullOrEmpty(ev.Category));
            Assert.False(string.IsNullOrEmpty(ev.EventName));
            Assert.NotNull(ev.EventArgsType);
            Assert.NotNull(ev.DeclaringType);
            // CanonicalName must always be the flattened "Category.EventName" form.
            Assert.Equal($"{ev.Category}.{ev.EventName}", ev.CanonicalName);
        }
    }

    [Fact]
    public void All_CanonicalNames_AreUnique()
    {
        var names = KnownEvents.All.Select(e => e.CanonicalName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_NoCategoryRetainsEventsSuffix()
    {
        // StripEventsSuffix must run on every category — "BuildEvents" must surface as
        // "Build", never "BuildEvents". Catches accidental string-handling regressions.
        foreach (var ev in KnownEvents.All)
        {
            Assert.False(
                ev.Category.EndsWith("Events", StringComparison.Ordinal),
                $"Category '{ev.Category}' for event '{ev.EventName}' was not stripped of its 'Events' suffix.");
        }
    }
}
