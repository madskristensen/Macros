// Polyfill for C# 9 `record class` and `init` setters on .NET Framework 4.8.
// The runtime BCL on net48 lacks System.Runtime.CompilerServices.IsExternalInit,
// which the compiler emits as a modreq for init-only setters. Defining it
// internally is sufficient — the JIT only needs the type to exist somewhere.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
