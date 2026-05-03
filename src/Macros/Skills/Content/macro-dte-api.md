---
name: macro-dte-api
description: Use the EnvDTE / EnvDTE80 automation surface from inside a Macros for Visual Studio .csx macro. Use when a macro needs the active document, text selection, solution state, project items, debugger state, windows, or direct DTE.ExecuteCommand calls — anything beyond the helper verbs in Macros.Engine.Scripting.Helpers. Do NOT use for VSIX authoring; this skill is exclusively about consuming the already-injected DTE global from a .csx macro.
---

# DTE2 inside macros
Use `DTE` inside a macro when helper methods are not enough and you need direct Visual Studio automation state.

## When to use this skill
Use this skill when the macro needs the active document, text selection, solution state, windows, debugger state, or direct command execution through the DTE automation model.

Do **not** use this skill for building a VS extension. This is only about using the already-injected `DTE` object from inside a `.csx` macro.

## The `DTE` global
Inside a macro, `DTE` is the host Visual Studio automation root:

```csharp
EnvDTE80.DTE2 dte = DTE;
```

Reference: https://learn.microsoft.com/en-us/dotnet/api/envdte80.dte2

## Active document access
Start here when a macro acts on the current file:

```csharp
var doc = DTE.ActiveDocument;
if (doc == null)
{
    await VS.StatusBar.ShowMessageAsync("No active document");
    return;
}
```

## Get or replace text with `TextSelection`
Cast `Selection` to `EnvDTE.TextSelection`:

```csharp
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel == null)
{
    await VS.StatusBar.ShowMessageAsync("No text editor is active");
    return;
}

sel.Insert(sel.Text.ToUpperInvariant(), (int)EnvDTE.vsInsertFlags.vsInsertFlagsInsertAtStart);
```

If you only need typing, caret movement, or selection, prefer helpers like `TypeAsync`, `MoveCaretAsync`, and `SelectAsync`.

## Solution information
Use DTE for solution-wide context:

```csharp
if (DTE.Solution?.IsOpen == true)
{
    string solutionName = DTE.Solution.FullName;
    int projectCount = DTE.Solution.Projects?.Count ?? 0;
    await VS.StatusBar.ShowMessageAsync($"{solutionName} has {projectCount} project(s)");
}
```

## Build state checks
```csharp
var state = DTE.Solution.SolutionBuild.BuildState;
if (state == EnvDTE.vsBuildState.vsBuildStateInProgress)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Build still running");
    return;
}
```

## Run a command through DTE
```csharp
DTE.ExecuteCommand("View.ErrorList");
DTE.ExecuteCommand("Edit.FormatDocument");
```

If you want the macro vocabulary to stay consistent, `await ExecuteCommandAsync("Edit.FormatDocument")` is also correct.

## Windows and tool panes
```csharp
var activeWindow = DTE.ActiveWindow;
if (activeWindow != null)
{
    await VS.StatusBar.ShowMessageAsync($"Active window: {activeWindow.Caption}");
}
```

## Debugger state
```csharp
var mode = DTE.Debugger.CurrentMode;
if (mode == EnvDTE.dbgDebugMode.dbgBreakMode)
{
    await VS.StatusBar.ShowMessageAsync("Debugger is in break mode");
}
```

## UI-thread rule
Treat DTE as a UI-thread API.

Helper methods such as `TypeAsync`, `MoveCaretAsync`, `SelectAsync`, `ExecuteCommandAsync`, `RunCommandAsync`, and `OpenFileAsync` switch to the UI thread for you before touching DTE or shell services.

Practical guidance:
- Prefer helpers first
- Use direct `DTE` access for reads or advanced editor automation
- Do not push DTE work into `Task.Run`
- Guard against `null` for documents, selections, and windows

## Common patterns
### Read selected text
```csharp
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel?.IsEmpty == false)
{
    await VS.StatusBar.ShowMessageAsync($"Selection length: {sel.Text.Length}");
}
```

### Save only when a document is open
```csharp
if (DTE.ActiveDocument != null)
{
    DTE.ExecuteCommand("File.SaveSelectedItems");
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

## Good defaults for Copilot
- Start with `DTE.ActiveDocument` or `DTE.ActiveDocument?.Selection as EnvDTE.TextSelection`
- Null-check before acting
- Prefer helper methods for typing, selection, file opening, and named commands
- Use `DTE.Solution.SolutionBuild.BuildState` for build-aware branching
- Use `VS.StatusBar` or `VS.MessageBox` to explain why the macro did nothing
