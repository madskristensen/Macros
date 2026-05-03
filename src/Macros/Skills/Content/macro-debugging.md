---
name: macro-debugging
description: Troubleshoot a failing or flaky Macros for Visual Studio .csx macro. Use when a macro compiles but fails at playback, when IntelliSense shows symbols that aren't there at runtime, when `DTE.ActiveDocument` is unexpectedly null, when `ExecuteCommandAsync` reports a command not found, when timing or async issues need a `WaitAsync` guard, when the IntelliSense shim's editor-only behaviour is causing confusion, when triggers fire but the macro misbehaves, when a macro has been auto-disabled, or when the user needs to know where errors surface (Macros Output pane / Error List / InfoBar / Esc-to-cancel). Do NOT use for attaching a debugger to a VSIX package or diagnosing extension load problems.
---

# Debugging .csx macros

When a macro doesn't do what you expect, the cause almost always falls into a small set of categories. This skill is the triage map.

For the macro file shape (`#load`, header block, helpers), see [`writing-macros`](../writing-macros/SKILL.md). For the surfaces this skill recommends (status bar, message box, info bar, output pane), see [`macro-toolkit-api`](../macro-toolkit-api/SKILL.md). For DTE-specific reads (selection, build state), see [`macro-dte-api`](../macro-dte-api/SKILL.md). For trigger-specific issues (auto-disable, kill switch, re-entrance), see [`macro-triggers`](../macro-triggers/SKILL.md).

## Where errors surface

When a macro fails at runtime the engine routes the failure to **three** Visual Studio surfaces simultaneously, plus an in-line cancellation affordance:

| Surface | What you see | When |
|---|---|---|
| **Output pane "Macros"** | Full diagnostic text including stack traces, compile errors with `.csx` line numbers, and any `Log.InfoAsync/WarnAsync/ErrorAsync` lines the macro itself wrote. | Always populated on failure. View → Output → Macros. |
| **Error List** | One row per compile diagnostic with `(line, col): error CSxxxx` and a clickable link to the line. | Compile errors only. Runtime exceptions are not Error-List items. |
| **InfoBar** | "Macro 'X' failed — *View Output*" anchored to the active document. | Always on failure (clean cancellation excepted). |
| **`Esc` key** | The active macro is cancelled at the next cooperative-cancellation point (every helper `await`). | At any point during playback. |

If a user says "my macro just didn't do anything", the **Output pane "Macros"** is the first thing to check.

## Symptom → cause → fix table

| Symptom | Likely cause | Fix |
|---|---|---|
| `error CS0103: The name 'TypeAsync' does not exist` | Missing `#load ".intellisense/Macros.Intellisense.csx"` line. | Re-add the `#load` directive. |
| `error CS0246: The type or namespace name 'EnvDTE' could not be found` | Custom `#r` of a shim-provided assembly. | Remove the redundant `#r`; the shim already supplies it. |
| `"Macro cancelled by user"` in Output, no error | User pressed `Esc` (or another macro cancelled it). | Not an error. If the cancellation is unexpected, check trigger re-entrance — see [`macro-triggers`](../macro-triggers/SKILL.md). |
| `InvalidOperationException: Command "Edit.SomeCommand" was not found` | Wrong command name, or the command isn't loaded in the current VS context. | Use Tools → Options → Environment → Keyboard to find the correct name. Some commands only exist when a debugger is attached / a tool window is open. |
| `NullReferenceException` on `DTE.ActiveDocument.Selection.Text` | No active document, or the active "document" is a designer / web preview (no `TextSelection`). | Null-check `DTE.ActiveDocument` and use `as EnvDTE.TextSelection` rather than a hard cast. See [`macro-dte-api`](../macro-dte-api/SKILL.md). |
| Helper appears to do nothing; no error | Missing `await`. The `Task` is started but never awaited; the next line runs before it completes. | Add `await`. |
| Macro intermittently fails after a command | Async command hasn't settled by the time the next step runs. | `await WaitAsync(milliseconds)` between the two steps. Try 100–250 ms first. |
| IntelliSense knows a symbol but runtime says it doesn't exist | The shim provides editor-only stubs. Runtime uses the real `MacroGlobals`, which has a different shape. | Inspect at runtime with `await Log.InfoAsync(thing.GetType().FullName);` and adjust the script. |
| Triggered macro fires once, then stops | Auto-disable kicked in after 3 consecutive failures. | Check the InfoBar for *Re-enable*, or re-enable from the tool window. See [`macro-triggers`](../macro-triggers/SKILL.md). |
| **Nothing** triggers fire | Kill switch is on. | Tools → Options → Macros → uncheck "Disable all triggers", or click the toolbar **Toggle Triggers** button. |
| Trigger registered but never fires for a repo macro | Trust gate not granted yet. | Run any repo-trigger once; the consent prompt appears. Or grant via Tools → Options → Macros. |
| Edit to `.csx` doesn't take effect | None — the file watcher should pick it up automatically. | Confirm by triggering a manual run; if it really doesn't update, restart VS. |
| `NuGet restore failed: dotnet not found` | `.NET SDK` is not on PATH. The macro uses `#r "nuget: ..."`. | Install the .NET SDK from <https://dot.net/download> and restart VS. See [`writing-macros`](../writing-macros/SKILL.md) for NuGet specifics. |

## Defensive patterns

### Null-guard before every state read

```csharp
if (DTE.ActiveDocument is null)
{
    await VS.StatusBar.ShowMessageAsync("Open a document first");
    return;
}

var sel = DTE.ActiveDocument.Selection as EnvDTE.TextSelection;
if (sel is null || sel.IsEmpty)
{
    await VS.StatusBar.ShowMessageAsync("Select some text first");
    return;
}
```

### Add breadcrumbs with `Log` (preferred over `VS.StatusBar`)

`Log.InfoAsync` writes one line per call to the **Macros** Output pane with a timestamp and the macro name as a prefix. Multiple breadcrumbs survive in the pane history; status-bar messages overwrite each other.

```csharp
await Log.InfoAsync("Step 1: locating selection");
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
await Log.InfoAsync($"Step 2: selection found = {sel is not null}");

if (sel is null) return;
await Log.InfoAsync($"Step 3: text length = {sel.Text.Length}");
```

Use `Log.WarnAsync` for "this looks suspicious but isn't fatal" and `Log.ErrorAsync` for "something the user should investigate". The shared pane is what the macro error renderer uses too, so a user inspecting a failure naturally sees these breadcrumbs in context.

### Verify command names before changing structure

```csharp
await Log.InfoAsync("About to run Edit.FormatDocument");
await ExecuteCommandAsync("Edit.FormatDocument");
await Log.InfoAsync("Edit.FormatDocument returned successfully");
```

A missing log line tells you exactly which step threw.

### Check state before acting

```csharp
if (DTE.ActiveDocument?.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) != true)
{
    await VS.StatusBar.ShowMessageAsync("This macro expects a C# document");
    return;
}

await ExecuteCommandAsync("Edit.FormatDocument");
```

## The `#load` directive at runtime

The `#load ".intellisense/Macros.Intellisense.csx"` line is intercepted by the macro player and replaced with empty content at runtime. The shim only exists to feed the **editor**'s C# language service — it provides `#r` references and stub globals so IntelliSense knows about `DTE`, `VS`, `Context`, and `Trigger`.

What this means in practice:

- **Don't delete the `#load` line to "fix" a runtime error.** It can't be the cause; the player ignores it. If you delete it, the editor loses IntelliSense and the macro still misbehaves.
- **IntelliSense correctness ≠ runtime correctness.** The shim's stub globals have a slightly different shape from the real `MacroGlobals`. If a member shows up in completion but throws at runtime, it's a shim/runtime divergence and you should file an issue (or simply use a different API).

## Async / await checklist

Macros are top-level scripts running through Roslyn's `CSharpScript`. Top-level `await` is supported. Every helper is async.

| Correct | Incorrect | Why |
|---|---|---|
| `await TypeAsync("hi");` | `TypeAsync("hi");` | Returns a `Task` that never gets observed. |
| `await Log.InfoAsync(msg);` | `Log.InfoAsync(msg);` | Same. The log line might not appear before the macro exits. |
| `await WaitAsync(200);` | `Thread.Sleep(200);` | Blocks the calling thread, including the UI thread. |
| Run helpers from the script body. | `Task.Run(() => TypeAsync("hi"));` | `Task.Run` jumps to threadpool; helpers handle their own threading. |

## Fast triage flowchart

When a macro misbehaves, walk this list in order. Stop when you find the cause.

1. **Open the Macros Output pane.** View → Output → drop-down → "Macros". Read the last `=== <macroname> failed in N ms ===` block.
2. **Check the Error List** for compile errors — the line numbers are clickable.
3. **Did every helper call use `await`?** Visually scan the script.
4. **Is `DTE.ActiveDocument` definitely non-null when the macro runs?** Add a `Log.InfoAsync` at the top to verify.
5. **Is the command name correct?** Tools → Options → Environment → Keyboard → search.
6. **For triggered macros: is the kill switch off, the trust gate granted, and the macro not auto-disabled?** See [`macro-triggers`](../macro-triggers/SKILL.md).
7. **Add `WaitAsync(150)` between the previous step and the failing one** — only after the above are eliminated.

## Anti-patterns — don't do these

| Don't | Why | Do instead |
|---|---|---|
| Delete the `#load` line "to debug" | The player ignores it; deleting only loses editor IntelliSense. | Read the Output pane instead. |
| `try { ... } catch { }` to swallow errors | Hides root causes; the user sees a macro that does nothing. | Catch specific exceptions, log them with `await Log.ErrorAsync(...)`, and re-throw if unrecoverable. |
| Push diagnostics to a custom Output pane named `"Macros"` | Collides with the engine's pane. The user can't tell macro logs from engine logs. | Use `Log.*` (shared pane) or pick a distinct pane name like `"My Macro Log"`. |
| Use `VS.MessageBox.ShowAsync` for diagnostics | Modal; interrupts the user; can't be archived. | `Log.InfoAsync` to the Output pane. |
| Add `await Task.Delay(2000)` to "let the IDE catch up" | Slow and brittle. | If sequencing matters, `await WaitAsync(150)` and verify by checking state, not waiting. |

## Reference

- Error renderer: `src/Macros/Errors/MacroErrorRenderer.cs`
- `Log` static class: `src/Macros.Engine/Scripting/Log.cs`
- IntelliSense shim generator: `src/Macros.Engine/Scripting/IntelliSenseShim.cs`
- Auto-disable / failure tracking: `src/Macros.Engine/Triggers/MacroFailureTracker.cs`
