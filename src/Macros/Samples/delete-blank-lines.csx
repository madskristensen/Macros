// Removes blank lines from the current selection.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.StatusBar.ShowMessageAsync("Macros: No selection to process");
    return;
}

var lines = sel.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
var nonBlank = lines.Where(l => !string.IsNullOrWhiteSpace(l));
sel.Insert(string.Join("\n", nonBlank), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
