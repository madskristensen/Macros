---
name: macro-dte-api
description: Use the EnvDTE / EnvDTE80 automation surface from inside a Macros for Visual Studio .csx macro. Use when the macro needs the active document, text selection (`EnvDTE.TextSelection`), solution state (`DTE.Solution`), build state (`DTE.Solution.SolutionBuild.BuildState`), project items, debugger state (`DTE.Debugger`), windows (`DTE.ActiveWindow`), or direct `DTE.ExecuteCommand` calls — anything beyond the helper verbs in `Macros.Engine.Scripting.Helpers`. Do NOT use for VSIX authoring; this skill is exclusively about consuming the already-injected `DTE` global from a `.csx` macro.
---

# Using the DTE2 automation surface from a macro

`DTE` (Development Tools Environment) is Visual Studio's classic automation root. Inside a macro it's pre-injected as `EnvDTE80.DTE2` — no `GetService` call required. Use it when the helper verbs in [`writing-macros`](../writing-macros/SKILL.md) don't cover what you need: reading the active selection, inspecting the solution, querying build/debugger state, walking project items, or activating tool windows.

For UI feedback after a DTE-driven action (status messages, message boxes, info bars), see [`macro-toolkit-api`](../macro-toolkit-api/SKILL.md).

## Decision table — helper or DTE?

If you find yourself reaching for DTE, check this table first. The helpers handle UI-thread marshalling, cancellation, and most pitfalls automatically.

| Goal | Use the helper | Use DTE only if |
|---|---|---|
| Insert text at the caret | `await TypeAsync("...")` | You need to insert via `EnvDTE.vsInsertFlags.vsInsertFlagsContainNewText` etc. |
| Move the caret | `await MoveCaretAsync(line, col)` | You need to move relative to the current selection (`sel.LineDown(...)`). |
| Make a selection | `await SelectAsync(sl, sc, el, ec)` | You need word- or line-mode selection (`sel.SelectLine()`). |
| Run a named command | `await ExecuteCommandAsync("Edit.FormatDocument")` | You need the command to run synchronously and inspect its return — `DTE.ExecuteCommand` is sync. |
| Open a file | `await OpenFileAsync(@"C:\path\file.cs")` | You need to specify a non-default editor view (`vsViewKindCode` etc.). |
| Read selected text | `var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;` (DTE) | Always — there's no helper for reads. |
| Solution / project / debugger state | DTE | Always — that's what DTE is for. |

The pattern is: **prefer helpers for writes, fall back to DTE for reads and for state inspection that no helper covers**.

## Active document and text selection

`DTE.ActiveDocument` is the most-used entry point. It's `null` when no document tab has focus (e.g. focus is on a tool window, or VS just started).

```csharp
var doc = DTE.ActiveDocument;
if (doc is null)
{
    await VS.StatusBar.ShowMessageAsync("No active document");
    return;
}
```

To work with the editor cursor, cast `doc.Selection` to `EnvDTE.TextSelection`. The cast is `as` — non-text editors (designers, web previews) return `null`.

```csharp
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel is null)
{
    await VS.StatusBar.ShowMessageAsync("No active text editor");
    return;
}

if (sel.IsEmpty)
{
    await VS.StatusBar.ShowMessageAsync("Nothing selected");
    return;
}

string original = sel.Text;
sel.Insert(original.ToUpperInvariant(), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
```

`TextSelection` has dozens of members; the high-leverage ones are:

| Member | Purpose |
|---|---|
| `Text` | The currently-selected text (read or replace via `Insert`). |
| `IsEmpty` | `true` when the selection is a zero-width caret. |
| `Insert(string, vsInsertFlags)` | Replace the selection with new text. |
| `MoveToLineAndOffset(int, int, bool extend)` | Move/extend the caret to a 1-based position. |
| `SelectLine()` / `SelectAll()` | Common selection presets. |
| `ActivePoint`, `AnchorPoint` | The two ends of the selection as `EditPoint` objects. |

## Solution state

`DTE.Solution` is `null` if no solution is loaded yet, but is non-null with `IsOpen == false` between `File.CloseSolution` and the next solution open. Defensive checks both ways:

```csharp
if (DTE.Solution?.IsOpen == true)
{
    string fullName = DTE.Solution.FullName;
    int projectCount = DTE.Solution.Projects?.Count ?? 0;
    await VS.StatusBar.ShowMessageAsync($"{fullName} ({projectCount} project(s))");
}
```

Iterating projects:

```csharp
foreach (EnvDTE.Project project in DTE.Solution.Projects)
{
    await Log.InfoAsync($"Project: {project.Name} ({project.Kind})");
}
```

## Build state

The build state is the single most useful read for triggered macros. Read it before doing anything that should not run during a build:

```csharp
var state = DTE.Solution.SolutionBuild.BuildState;
if (state == EnvDTE.vsBuildState.vsBuildStateInProgress)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Build still running");
    return;
}
```

Values: `vsBuildStateNotStarted`, `vsBuildStateInProgress`, `vsBuildStateDone`.

## Debugger state

```csharp
var mode = DTE.Debugger.CurrentMode;
if (mode == EnvDTE.dbgDebugMode.dbgBreakMode)
{
    await VS.StatusBar.ShowMessageAsync("Debugger is in break mode");
}
```

Values: `dbgDesignMode`, `dbgRunMode`, `dbgBreakMode`. Useful in conjunction with the `Debugger.EnterBreakMode` / `Debugger.ExceptionThrown` triggers — see [`macro-triggers`](../macro-triggers/SKILL.md).

## Active window and tool panes

```csharp
var active = DTE.ActiveWindow;
if (active is not null)
{
    await VS.StatusBar.ShowMessageAsync($"Active: {active.Caption} ({active.Kind})");
}
```

To activate a specific tool window by GUID, prefer `await ExecuteCommandAsync("View.ErrorList")` — the command names are far easier to discover than `Constants.vsWindowKindErrorList`.

## Running a command via DTE

`DTE.ExecuteCommand` is the synchronous, fire-and-forget version of the helper:

```csharp
DTE.ExecuteCommand("View.ErrorList");
DTE.ExecuteCommand("Edit.FormatDocument");
```

Use it when you specifically need synchronous behaviour. For everything else, `await ExecuteCommandAsync(...)` is the correct choice — it's awaitable, marshals to the UI thread, and respects cancellation.

## Common patterns

### Save only when there's an open document

```csharp
if (DTE.ActiveDocument is not null)
{
    await ExecuteCommandAsync("File.SaveSelectedItems");
}
```

### Inspect the active document path

```csharp
string? path = DTE.ActiveDocument?.FullName;
if (!string.IsNullOrEmpty(path))
{
    await VS.StatusBar.ShowMessageAsync(path);
}
```

### Branch on file type

```csharp
if (DTE.ActiveDocument?.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true)
{
    await ExecuteCommandAsync("Edit.FormatDocument");
}
```

### Check that a project is loaded before referring to it

```csharp
foreach (EnvDTE.Project project in DTE.Solution.Projects)
{
    if (project.Name == "MyApp.Tests")
    {
        await ExecuteCommandAsync("Project.UnloadProject");
        break;
    }
}
```

## UI-thread rule

Treat `DTE` and every `EnvDTE.*` interface as **UI-thread only**. The macro player runs your script on a background context but the helper verbs internally switch to the UI thread before touching any DTE state. When you call DTE *directly*, you're on whichever thread the helpers most recently parked you on.

In practice:

- DTE access from the top-level script body or after an `await TypeAsync(...)` (which leaves you on the UI thread) is fine.
- Wrapping DTE access in `Task.Run(() => ...)` is wrong — `Task.Run` always uses the threadpool, and DTE will throw or marshal under the covers in surprising ways.
- If you spawned background work yourself (e.g. for HTTP I/O), `await VS.StatusBar.ShowMessageAsync(...)` will get you back on the UI thread before the next DTE call.

## Anti-patterns — don't do these

| Don't | Why | Do instead |
|---|---|---|
| `Task.Run(() => DTE.ActiveDocument)` | Runs on threadpool; DTE throws or behaves erratically. | Read `DTE.ActiveDocument` directly from the script body. |
| `DTE.ActiveDocument.Selection.Text` (no null-check) | `ActiveDocument` is null when focus is off-editor; `Selection` is null for non-text editors. | Use `DTE.ActiveDocument?.Selection as EnvDTE.TextSelection` and null-check. |
| `(EnvDTE.TextSelection)doc.Selection` (hard cast) | Throws `InvalidCastException` on a designer / web preview. | Use `as EnvDTE.TextSelection` and null-check. |
| `DTE.ExecuteCommand("...")` and assume it succeeded | Returns `void`; failure surfaces as a thrown exception with a generic message. | `await ExecuteCommandAsync(...)` (helper handles cancellation cleanly), or wrap in try/catch. |
| Iterate `DTE.Solution.Projects` when `Solution.IsOpen == false` | Throws `InvalidOperationException`. | Guard with `DTE.Solution?.IsOpen == true`. |

## Reference

- Live API browser: <https://learn.microsoft.com/en-us/dotnet/api/envdte80.dte2>
- Helper verbs: `src/Macros.Engine/Scripting/Helpers.cs`
- The `MacroGlobals.DTE` injection point: `src/Macros.Engine/Scripting/MacroGlobals.cs`
