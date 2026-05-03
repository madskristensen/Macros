// Wraps the current selection in a named #region block.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.StatusBar.ShowMessageAsync("Macros: No selection to wrap");
    return;
}

string name = await PromptAsync("Region name:", "Custom Region");
var wrapped = $"#region {name}\n{sel.Text}\n#endregion";
sel.Insert(wrapped, (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
