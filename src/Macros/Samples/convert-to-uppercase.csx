// Converts the current selection to uppercase text.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "No text selected");
    return;
}

sel.Insert(sel.Text.ToUpper(), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
