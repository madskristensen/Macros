using System;
using System.Linq;
using System.Reflection;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Surface-lock test for <see cref="IMacroEventBus"/>. The interface is consumed by VSIX-side
/// wiring (<c>MacrosPackage</c>, <c>MacroEventBus</c>) that this project can't take a dependency
/// on, so we pin the member set here; signatures themselves are enforced by the C# compiler at
/// every implementation site.
/// </summary>
public sealed class IMacroEventBusContractTests
{
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
