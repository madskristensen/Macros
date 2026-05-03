# Macro Samples

A collection of ready-to-use macros showing what's possible. Copy any sample into a `.csx` file in your macros folder.

> **New to macros?** See [Getting Started](getting-started.md) and the [C# Scripting Reference](csx-reference.md).

## Samples

### 1. Auto-collapse regions on file open
_Demonstrates: Triggers, command execution_

```csharp
// @trigger Document.Opened
#load ".intellisense/Macros.Intellisense.csx"

await ExecuteCommandAsync("Edit.CollapseAllOutlining");
```

### 2. Sort selected lines alphabetically
_Demonstrates: Text manipulation, conditional logic, selection handling_

```csharp
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
```

### 3. Insert current timestamp at caret
_Demonstrates: DateTime, simple text insertion_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

await TypeAsync(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
```

### 4. Wrap selection in try/catch
_Demonstrates: Text insertion, conditional logic_

```csharp
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
```

### 5. Toggle between // and /* */ comment styles
_Demonstrates: Text analysis, string replacement_

```csharp
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
```

### 6. Format on save for C# files only
_Demonstrates: Trigger with filter condition_

```csharp
// @trigger Document.Saved when filename=*.cs
#load ".intellisense/Macros.Intellisense.csx"

await ExecuteCommandAsync("Edit.FormatDocument");
```

### 7. Open matching test file
_Demonstrates: Convention-based file discovery, conditional logic_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

var doc = DTE.ActiveDocument;
if (doc?.FullName == null) return;

var fileName = System.IO.Path.GetFileNameWithoutExtension(doc.Name);
var directory = System.IO.Path.GetDirectoryName(doc.FullName);
var testFileName = $"{fileName}Tests.cs";
var testPath = System.IO.Path.Combine(directory, testFileName);

if (System.IO.File.Exists(testPath))
{
    await OpenFileAsync(testPath);
}
else
{
    await VS.StatusBar.ShowMessageAsync($"Macros: Could not find {testFileName}");
}
```

### 8. Insert file header with author name
_Demonstrates: User input with PromptAsync, header generation_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

string author = await PromptAsync("Author:", "Mads Kristensen");
var header = $@"// Copyright (c) {DateTime.Now.Year} {author}
// All rights reserved.

";
await TypeAsync(header);
```

### 9. Show build error count in status bar
_Demonstrates: Trigger data access, event handling_

```csharp
// @trigger Build.SolutionBuildDone
#load ".intellisense/Macros.Intellisense.csx"

int errors = (int?)Trigger?.Data?["ErrorCount"] ?? 0;
int warnings = (int?)Trigger?.Data?["WarningCount"] ?? 0;
await VS.StatusBar.ShowMessageAsync($"Build complete: {errors} error(s), {warnings} warning(s)");
```

### 10. Collapse all regions in current document
_Demonstrates: Simple command execution_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

await ExecuteCommandAsync("Edit.CollapseAllOutlining");
```

### 11. Insert TODO with today's date
_Demonstrates: String interpolation, DateTime formatting_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

await TypeAsync($"// TODO ({DateTime.Now:yyyy-MM-dd}): ");
```

### 12. Convert selection to uppercase
_Demonstrates: Text transformation, conditional check_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == true)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "No text selected");
    return;
}

sel.Insert(sel.Text.ToUpper(), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
```

### 13. Delete blank lines in selection
_Demonstrates: Text filtering and processing_

```csharp
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
```

### 14. Surround with #region
_Demonstrates: User input, text wrapping with PromptAsync_

```csharp
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
```

### 15. Log active document path to Output Window
_Demonstrates: DTE Output Window access, debugging_

```csharp
#load ".intellisense/Macros.Intellisense.csx"

var doc = DTE.ActiveDocument;
if (doc?.FullName != null)
{
    var outputWindow = DTE.ToolWindows.OutputWindow;
    var pane = outputWindow.OutputWindowPanes.Item("Debug");
    pane.Activate();
    pane.OutputString($"Active document: {doc.FullName}\n");
}
```

## In-Product Samples

These fifteen macros ship with the extension and appear in the Macros tool window:

1. **Auto-collapse regions on file open** — Trigger: `Document.Opened`
2. **Sort selected lines alphabetically** — Reorders the selected lines in place
3. **Insert current timestamp at caret** — Types a formatted timestamp wherever the caret is
4. **Wrap selection in try/catch** — Wraps the selected code in a basic exception handler
5. **Toggle between // and /* */ comment styles** — Switches comment styles for the current selection
6. **Format on save for C# files only** — Trigger: `Document.Saved when filename=*.cs`
7. **Open matching test file** — Opens a sibling `*Tests.cs` file when it exists
8. **Insert file header with author name** — Demonstrates `PromptAsync` for user input
9. **Show build error count in status bar** — Trigger: `Build.SolutionBuildDone`
10. **Collapse all regions in current document** — Runs the collapse-outlining command on demand
11. **Insert TODO with today's date** — Creates a dated TODO comment stub
12. **Convert selection to uppercase** — Uppercases the current selection
13. **Delete blank lines in selection** — Removes empty lines from the selected block
14. **Surround with #region** — Prompts for a region name and wraps the selection
15. **Log active document path to Output Window** — Writes the active document path to the Debug pane

## What's Next?

- [Triggers Reference](triggers.md) — Make macros run automatically
- [C# Scripting Reference](csx-reference.md) — Full API documentation
- [Visual Studio Commands](https://learn.microsoft.com/en-us/visualstudio/ide/reference/visual-studio-commands) — Official list of all built-in VS commands
