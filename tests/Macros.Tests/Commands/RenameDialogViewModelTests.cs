using System;
using System.Collections.Generic;
using System.IO;
using Macros.Commands;
using Macros.Engine.Storage;
using Moq;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="RenameDialogViewModel"/>. All tests run without a VS host;
/// the <see cref="IMacroStorage"/> is mocked via Moq.
/// </summary>
public sealed class RenameDialogViewModelTests
{
    private static MacroDescriptor MakeDescriptor(string name, MacroScope scope = MacroScope.Global)
        => new(name, scope, $@"C:\macros\{name}.csx", DateTime.UtcNow, 0);

    /// <summary>
    /// Builds a mock storage where IsValidName returns true for names not containing '&lt;'
    /// and GetMacroPath returns a path that does NOT exist on disk.
    /// </summary>
    private static Mock<IMacroStorage> MakeStorage(MacroScope scope = MacroScope.Global)
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);

        mock.Setup(s => s.IsValidName(It.IsAny<string>()))
            .Returns<string>(n => !string.IsNullOrEmpty(n) && !n.Contains("<"));

        mock.Setup(s => s.GetMacroPath(It.IsAny<string>(), scope))
            .Returns<string, MacroScope>((n, _) =>
                Path.Combine(Path.GetTempPath(), "MacroRenameTests_" + Guid.NewGuid().ToString("N"), n + ".csx"));

        return mock;
    }

    [Fact]
    public void EmptyNewName_CanRename_False_NoMessage()
    {
        var storage = MakeStorage();
        var vm = new RenameDialogViewModel(MakeDescriptor("old-macro"), storage.Object);

        vm.NewName = string.Empty;

        Assert.False(vm.CanRename);
        Assert.Equal(string.Empty, vm.ValidationMessage);
    }

    [Fact]
    public void SameAsOldName_CanRename_False_SameAsCurrentMessage()
    {
        var storage = MakeStorage();
        var vm = new RenameDialogViewModel(MakeDescriptor("my-macro"), storage.Object);

        vm.NewName = "my-macro";

        Assert.False(vm.CanRename);
        Assert.Contains("same as current", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidName_ContainsAngleBracket_CanRename_False_InvalidCharactersMessage()
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
        mock.Setup(s => s.IsValidName(It.IsAny<string>()))
            .Returns<string>(n => !n.Contains("<"));

        var vm = new RenameDialogViewModel(MakeDescriptor("old-macro"), mock.Object);

        vm.NewName = "<bad>";

        Assert.False(vm.CanRename);
        Assert.Contains("invalid", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingTargetName_CanRename_False_AlreadyExistsMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "MacroRenameTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var existingFile = Path.Combine(tempDir, "existing.csx");
        File.WriteAllText(existingFile, "// existing macro");

        try
        {
            var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
            mock.Setup(s => s.IsValidName("existing")).Returns(true);
            mock.Setup(s => s.GetMacroPath("existing", MacroScope.Global)).Returns(existingFile);

            var vm = new RenameDialogViewModel(MakeDescriptor("old-macro"), mock.Object);
            vm.NewName = "existing";

            Assert.False(vm.CanRename);
            Assert.True(vm.TargetExists);
            Assert.Contains("already exists", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ValidNonExistingName_CanRename_True_NoMessage()
    {
        var storage = MakeStorage();
        var vm = new RenameDialogViewModel(MakeDescriptor("old-macro"), storage.Object);

        vm.NewName = "new-macro";

        Assert.True(vm.CanRename);
        Assert.Equal(string.Empty, vm.ValidationMessage);
        Assert.False(vm.TargetExists);
    }

    [Fact]
    public void PropertyChanged_FiresForValidationMessageAndCanRename_WhenNewNameChanges()
    {
        var mock = new Mock<IMacroStorage>(MockBehavior.Strict);
        // "<bad>" is invalid → sets ValidationMessage
        mock.Setup(s => s.IsValidName("<bad>")).Returns(false);
        // "new-macro" is valid + non-existing → clears ValidationMessage, sets CanRename=true
        mock.Setup(s => s.IsValidName("new-macro")).Returns(true);
        mock.Setup(s => s.GetMacroPath("new-macro", MacroScope.Global))
            .Returns(Path.Combine(Path.GetTempPath(), "new-macro.csx")); // does not exist

        var vm = new RenameDialogViewModel(MakeDescriptor("old-macro"), mock.Object);

        var fired = new List<string>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        // Step 1: invalid → ValidationMessage fires (""→error), CanRename stays false
        vm.NewName = "<bad>";
        // Step 2: valid → ValidationMessage fires (error→""), CanRename fires (false→true)
        vm.NewName = "new-macro";

        Assert.Contains(nameof(vm.NewName), fired);
        Assert.Contains(nameof(vm.ValidationMessage), fired);
        Assert.Contains(nameof(vm.CanRename), fired);
    }
}
