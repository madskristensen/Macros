using System;
using System.Threading.Tasks;
using Macros.Commands.Context;
using Macros.Engine.Storage;
using Moq;
using Xunit;

namespace Macros.Tests.Commands.Context;

/// <summary>
/// Unit tests for the testable <see cref="DeleteContextCommand.DeleteCoreAsync"/> helper.
/// All four paths of the deletion flow are exercised by injecting Moq doubles for
/// <see cref="IDeleteCommandUI"/> and <see cref="IMacroStore"/> — no VS host required.
/// </summary>
public sealed class DeleteContextCommandTests
{
    private static MacroEntry MakeDescriptor(string name = "MyMacro") =>
        new(name, MacroScope.Global, $@"C:\macros\{name}.csx", 0,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 100,
            System.Array.Empty<Macros.Engine.Triggers.TriggerBinding>());

    // ──────────────────────────────────────────────────────────────────────────────────
    // 1. User cancels confirmation
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelledConfirmation_DoesNotCallDeleteAsync()
    {
        var desc = MakeDescriptor();
        var storage = new Mock<IMacroStore>(MockBehavior.Strict);
        var ui = new Mock<IDeleteCommandUI>(MockBehavior.Strict);

        ui.Setup(u => u.ConfirmDeleteAsync(desc.Name)).ReturnsAsync(false);

        await DeleteContextCommand.DeleteCoreAsync(desc, storage.Object, ui.Object);

        // storage.DeleteAsync must never be called when the user cancels
        storage.VerifyNoOtherCalls();
        ui.Verify(u => u.ConfirmDeleteAsync(desc.Name), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // 2. Confirmed + DeleteAsync returns true → success status bar message
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmedAndDeleted_CallsShowSuccess()
    {
        var desc = MakeDescriptor();
        var storage = new Mock<IMacroStore>(MockBehavior.Strict);
        var ui = new Mock<IDeleteCommandUI>(MockBehavior.Strict);

        ui.Setup(u => u.ConfirmDeleteAsync(desc.Name)).ReturnsAsync(true);
        storage.Setup(s => s.DeleteAsync(desc.Name, desc.Scope, default)).ReturnsAsync(true);
        ui.Setup(u => u.ShowSuccessAsync(desc.Name)).Returns(Task.CompletedTask);

        await DeleteContextCommand.DeleteCoreAsync(desc, storage.Object, ui.Object);

        ui.Verify(u => u.ShowSuccessAsync(desc.Name), Times.Once);
        ui.Verify(u => u.ShowAlreadyDeletedAsync(It.IsAny<string>()), Times.Never);
        ui.Verify(u => u.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // 3. Confirmed + DeleteAsync returns false → "already deleted" informational path
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmedButAlreadyGone_CallsShowAlreadyDeleted()
    {
        var desc = MakeDescriptor();
        var storage = new Mock<IMacroStore>(MockBehavior.Strict);
        var ui = new Mock<IDeleteCommandUI>(MockBehavior.Strict);

        ui.Setup(u => u.ConfirmDeleteAsync(desc.Name)).ReturnsAsync(true);
        storage.Setup(s => s.DeleteAsync(desc.Name, desc.Scope, default)).ReturnsAsync(false);
        ui.Setup(u => u.ShowAlreadyDeletedAsync(desc.Name)).Returns(Task.CompletedTask);

        await DeleteContextCommand.DeleteCoreAsync(desc, storage.Object, ui.Object);

        ui.Verify(u => u.ShowAlreadyDeletedAsync(desc.Name), Times.Once);
        ui.Verify(u => u.ShowSuccessAsync(It.IsAny<string>()), Times.Never);
        ui.Verify(u => u.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────────────
    // 4. Confirmed + DeleteAsync throws → error surface, no success or already-deleted
    // ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmedAndDeleteThrows_CallsShowError()
    {
        var desc = MakeDescriptor();
        var boom = new InvalidOperationException("disk full");
        var storage = new Mock<IMacroStore>(MockBehavior.Strict);
        var ui = new Mock<IDeleteCommandUI>(MockBehavior.Strict);

        ui.Setup(u => u.ConfirmDeleteAsync(desc.Name)).ReturnsAsync(true);
        storage.Setup(s => s.DeleteAsync(desc.Name, desc.Scope, default)).ThrowsAsync(boom);
        ui.Setup(u => u.ShowErrorAsync(desc.Name, boom)).Returns(Task.CompletedTask);

        await DeleteContextCommand.DeleteCoreAsync(desc, storage.Object, ui.Object);

        ui.Verify(u => u.ShowErrorAsync(desc.Name, boom), Times.Once);
        ui.Verify(u => u.ShowSuccessAsync(It.IsAny<string>()), Times.Never);
        ui.Verify(u => u.ShowAlreadyDeletedAsync(It.IsAny<string>()), Times.Never);
    }
}
