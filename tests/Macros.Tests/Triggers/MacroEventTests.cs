using System;
using System.Collections.Generic;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins <see cref="MacroEvent"/> as a plain value carrier. Trivial today, but locks in
/// the public surface that <c>MacroEventBus</c> and trigger listeners both depend on.
/// </summary>
public sealed class MacroEventTests
{
    [Fact]
    public void Ctor_SetsAllProperties()
    {
        var firedAt = new DateTimeOffset(2026, 4, 30, 16, 0, 0, TimeSpan.Zero);
        var payload = new Dictionary<string, object?> { ["filename"] = "Foo.cs" };

        var ev = new MacroEvent("Document.Saved", firedAt, payload);

        Assert.Equal("Document.Saved", ev.CanonicalName);
        Assert.Equal(firedAt, ev.FiredAt);
        Assert.Same(payload, ev.Payload);
        Assert.Equal("Foo.cs", ev.Payload["filename"]);
    }

    [Fact]
    public void Ctor_AllowsEmptyPayload()
    {
        var payload = new Dictionary<string, object?>();
        var ev = new MacroEvent("Build.SolutionBuildDone", DateTimeOffset.UtcNow, payload);
        Assert.Empty(ev.Payload);
    }

    [Fact]
    public void Ctor_NullCanonicalName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MacroEvent(null!, DateTimeOffset.UtcNow, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Ctor_NullPayload_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MacroEvent("Build.SolutionBuildDone", DateTimeOffset.UtcNow, null!));
    }
}
