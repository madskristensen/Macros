// Collapses outlining in newly opened documents that contain #region directives.
// @trigger Document.Opened
#load ".intellisense/Macros.Intellisense.csx"

// step 1:inspect the opened document for #region directives
var textDocument = DTE.ActiveDocument?.Object("TextDocument") as EnvDTE.TextDocument;
if (textDocument is null)
{
    return;
}

var start = textDocument.StartPoint.CreateEditPoint();
string documentText = start.GetText(textDocument.EndPoint);
if (documentText.IndexOf("#region", StringComparison.Ordinal) < 0)
{
    return;
}

// step 2: collapse outlining once the file is known to contain regions
await ExecuteCommandAsync("Edit.CollapseAllOutlining");
