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

> 💡 **IntelliSense in the `.csx` editor.** Open any macro in VS itself (right-click → **Edit** in the tool window) and the C# language service provides full IntelliSense:
> - Helper verbs (`TypeAsync`, `ExecuteCommandAsync`, etc.)
> - The entire `DTE` automation surface
> - The `VS` static facade (status bar, info bars, dialogs, document services…)
> - The `Context` and `Trigger` globals
> - Syntax colorization, refactorings, and squigglies
>
> This works via an auto-managed `.intellisense/Macros.Intellisense.csx` shim file that lives alongside your macros. The shim is safe to ignore in source control — each contributor's VSIX regenerates it with machine-local DLL paths. (See [**How it works**](#how-it-works) below for the full picture.)

> 💡 You can write macros from scratch without recording. Right-click the tool window → **New Macro**, or use **File → New → Macro** to start a template.

### How it works

When a macro is opened in the editor, the C# language service automatically loads the IntelliSense shim via a `#load` directive. The shim contains:
- `#r` references to the necessary interop and extension DLLs (resolved to their machine-local absolute paths by the VSIX when the shim is written).
- `using` directives for the same namespaces as the generated macro code.
- Top-level field stubs for `DTE`, `Context`, and `Trigger` so the editor knows their types.

At *runtime* (when the macro plays), the player automatically skips loading the shim (so the real `MacroGlobals` take precedence), making the shim a purely editor-time artifact. For a deeper technical walk-through, see the [Architecture](architecture.md#intellisense-in-the-csx-editor) doc.
