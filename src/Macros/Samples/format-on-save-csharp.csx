// Formats the active C# document after it is saved.
// @trigger Document.Saved when filename=*.cs
#load ".intellisense/Macros.Intellisense.csx"

// step 1:skip anything that is not an active C# document
if (Context.CurrentDocument?.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) != true)
{
    return;
}

// step 2: format the saved file and surface a brief confirmation
await ExecuteCommandAsync("Edit.FormatDocument");
await VS.StatusBar.ShowMessageAsync($"Macros: Formatted {Context.CurrentDocument?.Name}");
