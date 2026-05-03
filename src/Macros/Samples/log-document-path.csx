// Writes the full path of the active document to the Debug Output window.
#load ".intellisense/Macros.Intellisense.csx"

var doc = DTE.ActiveDocument;
if (doc?.FullName != null)
{
    var outputWindow = DTE.ToolWindows.OutputWindow;
    var pane = outputWindow.OutputWindowPanes.Item("Debug");
    pane.Activate();
    pane.OutputString($"Active document: {doc.FullName}\n");
}
