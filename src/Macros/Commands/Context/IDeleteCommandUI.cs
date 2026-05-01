using System;
using System.Threading.Tasks;

namespace Macros.Commands.Context;

/// <summary>
/// Abstraction over the four VS-surface calls that <see cref="DeleteContextCommand.DeleteCoreAsync"/>
/// needs. Extracted so the deletion logic can be unit-tested without a VS host by injecting a
/// test double via Moq.
/// </summary>
internal interface IDeleteCommandUI
{
    /// <summary>Shows a confirmation dialog. Returns <see langword="true"/> when the user clicks OK/Yes.</summary>
    Task<bool> ConfirmDeleteAsync(string macroName);

    /// <summary>Notifies the user that the macro file no longer existed (idempotent delete).</summary>
    Task ShowAlreadyDeletedAsync(string macroName);

    /// <summary>Writes a success message to the VS status bar.</summary>
    Task ShowSuccessAsync(string macroName);

    /// <summary>Shows an error dialog and writes the full exception to the Macros Output pane.</summary>
    Task ShowErrorAsync(string macroName, Exception ex);
}
