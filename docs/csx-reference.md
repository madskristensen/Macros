# C# Scripting Reference

## Anatomy of a macro file

```csharp
// Macro: GreetTheWorld
// Recorded: 2026-06-01 14:23:11Z
// Steps: 3
// @trigger Manual

#r "EnvDTE"
#r "Microsoft.VisualStudio.Shell.Interop"

using System;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using static Macros.Helpers;

// Step 1: Type a greeting
await TypeAsync("// Hello, world!\n");

// Step 2: Format the document
await ExecuteCommandAsync("Edit.FormatDocument");

// Step 3: Save
await ExecuteCommandAsync("File.SaveSelectedItems");
```

The header block (comments and references) is parsed by the trigger system (see [Triggers](triggers.md)); everything below the first non-comment line is your script body. Top-level `await` works — this is **Roslyn C# Scripting** under the hood, the same engine that runs `dotnet-script`.

## The `Helpers` API

Import helpers with `using static Macros.Helpers`:

| Method | Purpose |
|--------|---------|
| `TypeAsync(string text)` | Insert text at the caret as if you typed it. |
| `MoveCaretAsync(int line, int column)` | Move the caret to a position (1-based). |
| `SelectAsync(int startLine, int startCol, int endLine, int endCol)` | Select a span. |
| `ExecuteCommandAsync(string name, string args = "")` | Invoke a named VS command (`File.Save`, `Edit.FormatDocument`, etc.). |
| `RunCommandAsync(Guid group, uint id, object? args = null)` | Invoke a command by GUID + ID for cases without a public name. |
| `WaitAsync(int ms)` | Pause the script. |

## The `MacroGlobals` object

Inside your script, these globals are automatically accessible:

- **`DTE`** — the EnvDTE2 object, gateway to the entire DTE automation surface.
- **`VS`** — the Community Toolkit's `VS` static facade (status bar, info bars, message boxes, document services…).
- **`Context`** — `IMacroContext` with cancellation token and current document info.
- **`Trigger`** — `IMacroTrigger?` with the firing event's data (e.g. `Trigger.Data["ErrorCount"]`); `null` for manual runs.

## Examples

### Example: block File.Save during a build

```csharp
// @trigger BeforeCommand File.Save
// Block File.Save during an active build.
if (DTE.Solution.SolutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Cannot save during a build.");
    Trigger.CancelCommand();
}
```

### Example: toast on build failure

```csharp
// @trigger Build.SolutionBuildDone when success=false
int errors = (int)Trigger.Data["ErrorCount"];
await VS.StatusBar.ShowMessageAsync($"💥 Build failed: {errors} error(s).");
```

### Example: custom C# logic

```csharp
// @trigger Document.Saved when filename=*.cs
// Auto-format saved C# files with a custom rule set.
if (Context.CurrentDocument?.Name.EndsWith(".cs") == true)
{
    await ExecuteCommandAsync("Edit.FormatDocument");
    await VS.StatusBar.ShowMessageAsync("Formatted on save");
}
```

## Tips

> 💡 Open any macro in VS itself (right-click → **Edit** in the tool window) and you get IntelliSense, refactorings, and squigglies for the script body — the same IDE you record in is also the macro editor.

> 💡 You can write macros from scratch without recording. Right-click the tool window → **New Macro**, or use **File → New → Macro** to start a template.
