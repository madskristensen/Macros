using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Macros.Engine;
using Macros.Engine.Storage;
using Moq;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="SaveAsDialogViewModel"/>. All tests run without a VS host;
/// the <see cref="IMacroStorage"/> is mocked via Moq.
/// </summary>
public sealed class SaveAsDialogViewModelTests
{
    private const string AnySource = "// macro source";

    /// <summary>
    /// Builds a mock storage where <paramref name="name"/> is valid and the target file
    /// does NOT yet exist on disk.
    /// </summary>
    private static Mock<IMacroStorage> MakeStorage(string validName, MacroScope scope = MacroScope.Global)
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);

        // IsValidName: true for exactly validName, false for anything containing '<' or empty
        mock.Setup(s => s.IsValidName(It.IsAny<string>()))
            .Returns<string>(n => !string.IsNullOrEmpty(n) && !n.Contains("<"));

        // GetMacroPath: return a temp path that does NOT exist
        mock.Setup(s => s.GetMacroPath(It.IsAny<string>(), scope))
            .Returns<string, MacroScope>((n, _) =>
                Path.Combine(Path.GetTempPath(), "MacroTests_" + Guid.NewGuid().ToString("N"), n + ".csx"));

        return mock;
    }

    [Fact]
    public void ValidName_NonExisting_CanSave_True_NoMessage()
    {
        var storage = MakeStorage("my-macro");
        var vm = new SaveAsDialogViewModel(AnySource, storage.Object, hasRepo: false);

        vm.Name = "my-macro";

        Assert.True(vm.CanSave);
        Assert.Equal(string.Empty, vm.ValidationMessage);
        Assert.False(vm.ExistsInTargetScope);
    }

    [Fact]
    public void InvalidName_ContainsAngleBracket_CanSave_False_MessageSet()
    {
        var storage = new Mock<IMacroStorage>(MockBehavior.Strict);
        storage.Setup(s => s.IsValidName(It.IsAny<string>()))
               .Returns<string>(n => !n.Contains("<"));

        var vm = new SaveAsDialogViewModel(AnySource, storage.Object, hasRepo: false);

        vm.Name = "bad<name";

        Assert.False(vm.CanSave);
        Assert.NotEmpty(vm.ValidationMessage);
        Assert.Contains("invalid", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyName_CanSave_False_NoMessage()
    {
        var storage = new Mock<IMacroStorage>(MockBehavior.Strict);
        storage.Setup(s => s.IsValidName(It.IsAny<string>()))
               .Returns<string>(n => !string.IsNullOrEmpty(n));

        var vm = new SaveAsDialogViewModel(AnySource, storage.Object, hasRepo: false);

        vm.Name = string.Empty;

        Assert.False(vm.CanSave);
        Assert.Equal(string.Empty, vm.ValidationMessage);
    }

    [Fact]
    public void ExistingName_CanSave_True_OverwriteWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "MacroTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var existingFile = Path.Combine(tempDir, "existing.csx");
        File.WriteAllText(existingFile, "// existing");

        try
        {
            var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
            mock.Setup(s => s.IsValidName("existing")).Returns(true);
            mock.Setup(s => s.GetMacroPath("existing", MacroScope.Global)).Returns(existingFile);

            var vm = new SaveAsDialogViewModel(AnySource, mock.Object, hasRepo: false);
            vm.Name = "existing";

            Assert.True(vm.CanSave);
            Assert.True(vm.ExistsInTargetScope);
            Assert.Contains("overwritten", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RepoScope_WhenNoRepo_CannotBeSet()
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
        mock.Setup(s => s.IsValidName(It.IsAny<string>())).Returns(true);
        mock.Setup(s => s.GetMacroPath(It.IsAny<string>(), MacroScope.Global))
            .Returns<string, MacroScope>((n, _) =>
                Path.Combine(Path.GetTempPath(), n + ".csx"));

        var vm = new SaveAsDialogViewModel(AnySource, mock.Object, hasRepo: false);
        vm.Name = "mymacro";

        // Attempt to set Repo scope when no repo is available — must be silently ignored
        vm.Scope = MacroScope.Repo;

        Assert.Equal(MacroScope.Global, vm.Scope);
        Assert.False(vm.IsRepoEnabled);
    }

    [Fact]
    public void ScopeChange_RerunsValidation()
    {
        var globalDir = Path.Combine(Path.GetTempPath(), "MacroTests_" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(Path.GetTempPath(), "MacroTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(globalDir);
        Directory.CreateDirectory(repoDir);

        // Create a file only in Repo scope
        var repoFile = Path.Combine(repoDir, "shared.csx");
        File.WriteAllText(repoFile, "// repo macro");

        try
        {
            var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
            mock.Setup(s => s.IsValidName("shared")).Returns(true);
            mock.Setup(s => s.GetMacroPath("shared", MacroScope.Global))
                .Returns(Path.Combine(globalDir, "shared.csx"));   // does NOT exist
            mock.Setup(s => s.GetMacroPath("shared", MacroScope.Repo))
                .Returns(repoFile);                                  // DOES exist

            var vm = new SaveAsDialogViewModel(AnySource, mock.Object, hasRepo: true);
            vm.Name = "shared";

            // Global scope: no conflict
            vm.Scope = MacroScope.Global;
            Assert.False(vm.ExistsInTargetScope);
            Assert.True(vm.CanSave);

            // Switch to Repo scope: conflict detected
            vm.Scope = MacroScope.Repo;
            Assert.True(vm.ExistsInTargetScope);
            Assert.True(vm.CanSave);  // overwrite warning, not error
            Assert.Contains("overwritten", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(globalDir, recursive: true); } catch { }
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsGlobalScope_IsRepoScope_ReflectScope()
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
        mock.Setup(s => s.IsValidName(It.IsAny<string>())).Returns(true);
        mock.Setup(s => s.GetMacroPath(It.IsAny<string>(), It.IsAny<MacroScope>()))
            .Returns<string, MacroScope>((n, _) =>
                Path.Combine(Path.GetTempPath(), n + ".csx"));

        var vm = new SaveAsDialogViewModel(AnySource, mock.Object, hasRepo: true);
        vm.Name = "test";

        vm.Scope = MacroScope.Global;
        Assert.True(vm.IsGlobalScope);
        Assert.False(vm.IsRepoScope);

        vm.Scope = MacroScope.Repo;
        Assert.False(vm.IsGlobalScope);
        Assert.True(vm.IsRepoScope);
    }

    [Fact]
    public void PropertyChanged_FiresForRelevantProperties()
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
        // "bad..name" is invalid → triggers the error path (ValidationMessage goes "" → error)
        mock.Setup(s => s.IsValidName("bad..name")).Returns(false);
        // "newname" is valid → clears the error (ValidationMessage goes error → "")
        mock.Setup(s => s.IsValidName("newname")).Returns(true);
        mock.Setup(s => s.GetMacroPath("newname", MacroScope.Global))
            .Returns(Path.Combine(Path.GetTempPath(), "newname.csx"));

        var vm = new SaveAsDialogViewModel(AnySource, mock.Object, hasRepo: false);

        var fired = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        // Step 1: invalid name → Name fires, ValidationMessage fires (""→error), CanSave stays false
        vm.Name = "bad..name";
        // Step 2: valid name → Name fires again, ValidationMessage fires (error→""), CanSave fires (false→true)
        vm.Name = "newname";

        Assert.Contains(nameof(vm.Name), fired);
        Assert.Contains(nameof(vm.CanSave), fired);
        Assert.Contains(nameof(vm.ValidationMessage), fired);
    }
}
