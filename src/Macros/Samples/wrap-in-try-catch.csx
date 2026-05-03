// Wraps the current selection in a try/catch block.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.StatusBar.ShowMessageAsync("Macros: No selection to wrap");
    return;
}

var indent = "";
var selectedText = sel.Text;
await TypeAsync($"try\n{{\n{selectedText}\n}}\ncatch (Exception ex)\n{{\n    // Handle exception\n}}");
