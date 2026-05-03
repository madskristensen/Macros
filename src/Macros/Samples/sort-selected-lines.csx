// Sorts the selected lines alphabetically.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "No text selected");
    return;
}

var lines = sel.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
Array.Sort(lines);
sel.Insert(string.Join("\n", lines), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
