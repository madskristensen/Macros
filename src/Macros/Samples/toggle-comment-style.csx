// Toggles the selected text between // line comments and /* */ block comments.
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.StatusBar.ShowMessageAsync("Macros: No text selected");
    return;
}

var text = sel.Text;
if (text.StartsWith("//"))
{
    var commented = text.Substring(2).Trim();
    sel.Insert($"/* {commented} */", (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
}
else if (text.StartsWith("/*") && text.EndsWith("*/"))
{
    var uncommented = text.Substring(2, text.Length - 4).Trim();
    sel.Insert($"// {uncommented}", (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
}
