using Microsoft.VisualStudio.Shell;

// Binding redirects for strong-named assemblies shipped inside the VSIX.
//
// VS 17.x already loads its own copies of several strong-named assemblies
// (Roslyn, BCL facades, the Community Toolkit, etc.) into devenv.exe before
// our package is sited. If the version the CLR resolves at runtime does not
// match the version one of our types references at compile time, the loader
// raises COR_E_TYPELOAD (0x80131500) and SetSite fails for the package.
//
// Extensions cannot edit devenv.exe.config, so we use the VSSDK-supported
// [ProvideBindingRedirection] attribute. The pkgdef generator emits matching
// entries under $RootKey$\RuntimeConfiguration\dependentAssembly\bindingRedirection
// which VS applies when our package loads. Each entry below covers exactly the
// strong-named DLLs we ship in Macros.vsix.

[assembly: ProvideBindingRedirection(
    AssemblyName = "Microsoft.CodeAnalysis.Scripting",
    PublicKeyToken = "31bf3856ad364e35",
    OldVersionLowerBound = "0.0.0.0",
    OldVersionUpperBound = "4.11.0.0",
    NewVersion = "4.11.0.0")]

[assembly: ProvideBindingRedirection(
    AssemblyName = "Microsoft.CodeAnalysis.CSharp.Scripting",
    PublicKeyToken = "31bf3856ad364e35",
    OldVersionLowerBound = "0.0.0.0",
    OldVersionUpperBound = "4.11.0.0",
    NewVersion = "4.11.0.0")]

[assembly: ProvideBindingRedirection(
    AssemblyName = "System.Text.Encoding.CodePages",
    PublicKeyToken = "b03f5f7f11d50a3a",
    OldVersionLowerBound = "0.0.0.0",
    OldVersionUpperBound = "7.0.0.0",
    NewVersion = "7.0.0.0")]
