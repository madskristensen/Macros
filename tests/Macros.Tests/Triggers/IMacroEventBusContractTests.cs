using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Reflection-only shape tests for <see cref="IMacroEventBus"/>. The interface is consumed
/// by VSIX-side wiring (<c>MacrosPackage</c>, <c>MacroEventBus</c>) that this project can't
/// take a dependency on, so we pin the contract here to fail fast if a member rename or a
/// signature change slips through code review.
/// </summary>
public sealed class IMacroEventBusContractTests
{
    [Fact]
    public void Interface_ExtendsIDisposable()
    {
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(typeof(IMacroEventBus)),
            "IMacroEventBus must extend IDisposable so the package can deterministically tear down VS subscriptions on unload.");
    }

    [Fact]
    public void Subscribe_Method_HasExpectedSignature()
    {
        var method = typeof(IMacroEventBus).GetMethod(
            nameof(IMacroEventBus.Subscribe),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(IDisposable), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("canonicalName", parameters[0].Name);
        Assert.Equal(typeof(Action<MacroEvent>), parameters[1].ParameterType);
        Assert.Equal("handler", parameters[1].Name);
    }

    [Fact]
    public void GetKnownEvents_Method_HasExpectedSignature()
    {
        var method = typeof(IMacroEventBus).GetMethod(
            nameof(IMacroEventBus.GetKnownEvents),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(IReadOnlyList<KnownVsEvent>), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void Interface_DeclaresExactlyTheExpectedMembers()
    {
        // Catches accidental new methods on the interface — anything new should land in a
        // dedicated todo with explicit test coverage rather than being absorbed silently.
        var declared = typeof(IMacroEventBus)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { nameof(IMacroEventBus.GetKnownEvents), nameof(IMacroEventBus.Subscribe) },
            declared);
    }
}
