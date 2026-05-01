using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Macros.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.Commands;

/// <summary>
/// Handler for the <c>View &gt; Other Windows &gt; Macros</c> command. Shows (creating if
/// necessary) the <see cref="MacrosToolWindow"/> dock pane.
/// </summary>
[Command(PackageGuids.CommandSetGuidString, PackageIds.cmdidMacrosShowWindow)]
internal sealed class ShowToolWindowCommand : BaseCommand<ShowToolWindowCommand>
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await MacrosToolWindow.ShowAsync();
    }
}
