---
name: macro-triggers
description: Make a Macros for Visual Studio .csx macro run automatically on IDE events. Use when the user wants to add a `// @trigger` directive, run a macro on build / save / solution open / debugger events, intercept a VS command with `BeforeCommand` (and optionally cancel it with `Trigger.CancelCommand()`), observe a command with `AfterCommand`, narrow firing with `when key=value` filters (`filename`, `success`, `project`, `exception`), or understand the trust gate / kill switch / auto-disable safety net for triggered macros. Do NOT use for VSIX event handlers, MEF listeners, IVsUpdateSolutionEvents, or other extensibility plumbing — this is exclusively the macro user surface.
---

# Triggers — making a macro run automatically

A trigger is a `// @trigger` line in the macro's header comment block that tells the engine when to fire the macro automatically. The macro can still be invoked manually at any time; triggers add automatic invocations on top.

For the canonical macro file shape that hosts these directives, see [`writing-macros`](../writing-macros/SKILL.md). For diagnosing a trigger that fires but the macro misbehaves, see [`macro-debugging`](../macro-debugging/SKILL.md).

## Anatomy of a trigger directive

```csharp
// @trigger Manual                                  // explicit no-trigger (also the default)
// @trigger Build.SolutionBuildDone                 // VS event
// @trigger Build.SolutionBuildDone when success=false
// @trigger Document.Saved when filename=*.cs       // glob filter
// @trigger Solution.OnAfterOpenSolution
// @trigger BeforeCommand File.Save                 // sync; can cancel
// @trigger AfterCommand Build.BuildSolution        // queued; observation only
```

Rules:

- Trigger lines live in the **header comment block** at the top of the file, before `#load` and before any executable code.
- **Multiple `@trigger` lines OR-combine.** Any one firing invokes the macro.
- A macro with **no** `@trigger` lines is manual-only — it runs only when invoked from the tool window, hotkey, or Quick Launch.

## Trigger kinds

| Kind | Syntax | Behaviour |
|---|---|---|
| **Manual** | `// @trigger Manual` | Default. Runs only on explicit user invocation. |
| **VS event** | `// @trigger {Category}.{EventName}` | Subscribes to an event on `VS.Events`. ~40 events available — see catalogue below. |
| **BeforeCommand** | `// @trigger BeforeCommand {Command.Name}` | Runs **synchronously, before** the named command executes. Can call `Trigger.CancelCommand()` to abort the command. Times out after 2000 ms (configurable). |
| **AfterCommand** | `// @trigger AfterCommand {Command.Name}` | Runs **after** the command. Queued asynchronously — never blocks the IDE. Can observe success via `Trigger.Payload["Success"]` (bool). |

## Common VS events

| Category | Events | Typical use |
|---|---|---|
| `Build` | `SolutionBuildDone`, `ProjectBuildDone` | React to build pass/fail, summarise errors |
| `Document` | `Saved`, `Opened`, `Closed`, `Renamed` | Format on save, register file taxonomy |
| `Solution` | `OnAfterOpenSolution`, `OnBeforeCloseSolution`, `OnAfterOpenProject` | Initialise workspace, warm caches |
| `Debugger` | `EnterBreakMode`, `EnterDesignMode`, `ExceptionThrown` | Show diagnostics on a hit |
| `Selection` | `SelectionChanged` | React to active document/window changes |
| `Window` | `ActiveFrameChanged`, `Created`, `Destroy` | Track window lifecycle |
| `Shell` | `ShutdownStarted` | Cleanup before VS exits |

The full live catalogue is reflected from `Community.VisualStudio.Toolkit.VS.Events` at runtime. Right-click a macro in the tool window → **Manage Triggers…** for autocompletion against the live list.

## `when` filter compatibility matrix

`when key=value` clauses appended to a trigger narrow when it fires. Multiple `key=value` pairs AND-combine.

| Filter key | Type | Applies to |
|---|---|---|
| `filename` | glob | All `Document.*` events (`Document.Saved`, `Document.Opened`, …) |
| `success` | bool | `Build.SolutionBuildDone`, `Build.ProjectBuildDone`, `AfterCommand` |
| `project` | glob | `Build.ProjectBuildDone` |
| `exception` | glob | `Debugger.ExceptionThrown` |

```csharp
// @trigger Document.Saved when filename=*.cs
// @trigger Build.SolutionBuildDone when success=false
// @trigger Build.ProjectBuildDone when project=*Tests* success=true
// @trigger Debugger.ExceptionThrown when exception=*ArgumentException*
```

A filter key that doesn't apply to its event is silently ignored (logged at debug verbosity) — `when filename=*.cs` on a `Build.*` event is a harmless no-op rather than an error.

## BeforeCommand: intercepting a command

`BeforeCommand` runs synchronously *before* the named command. It can:

- Inspect IDE state (`DTE`, `VS.*`, `Trigger.Payload`).
- Call `Trigger.CancelCommand()` to suppress the command. The user's keystroke / menu click does nothing.
- Run for up to 2 000 ms (configurable via Tools → Options → Macros → BeforeCommandTimeoutMs). After timeout, the command proceeds.

```csharp
// @trigger BeforeCommand File.Save
#load ".intellisense/Macros.Intellisense.csx"

if (DTE.Solution.SolutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Cannot save while a build is in progress.");
    Trigger.CancelCommand();
}
```

## AfterCommand: observing a command

`AfterCommand` is queued — the IDE never blocks waiting for it. It receives a payload describing what happened:

```csharp
// @trigger AfterCommand Build.BuildSolution
#load ".intellisense/Macros.Intellisense.csx"

bool success = Trigger.Payload.TryGetValue("Success", out var s) && s is bool b && b;
await VS.StatusBar.ShowMessageAsync(success ? "✅ Build succeeded" : "💥 Build failed");
```

## Adding a trigger to an existing macro

The trigger line goes inside the existing header block, before `#load`:

```csharp
// Macro: Format on Save
// Recorded: 2026-05-02T17:10:39Z
// Generated by Macros — edit freely.
// @trigger Document.Saved when filename=*.cs

#load ".intellisense/Macros.Intellisense.csx"
using static Macros.Engine.Scripting.Helpers;

await ExecuteCommandAsync("Edit.FormatDocument");
```

Don't put the trigger below `#load` or below executable code — it won't be parsed.

The generator emits commented-out examples in fresh macros:

```csharp
// EXAMPLE: // @trigger Build.SolutionBuildDone
// EXAMPLE: // @trigger Document.Saved when filename=*.cs
```

To activate one, remove only the `// EXAMPLE: ` prefix.

## Safety: trust gate, kill switch, auto-disable

The trigger system is wired with three independent safety mechanisms — all relevant when an automated macro misbehaves.

| Mechanism | What it does | When it fires |
|---|---|---|
| **Trust gate** | Asks the user once per solution whether to allow auto-triggers in repo-scoped macros. | First time a repo macro's trigger fires in an untrusted solution. |
| **Kill switch** | Globally disables every trigger. Manual invocation still works. | Toggled via the toolbar **Toggle Triggers** button or Tools → Options → Macros → Disable all triggers. |
| **Auto-disable** | Disables a single macro's triggers after 3 consecutive failures. Surfaces an InfoBar with **Re-enable**. | A *triggered* macro fails 3 times in a row. Manual failures don't count. |
| **Re-entrance guard** | Caps trigger nesting at depth 3 and prevents direct self-fire (a `BeforeCommand File.Save` macro that itself calls `File.Save`). | Always on. Silent — the inner trigger just doesn't fire. |

When writing a new auto-triggered macro, assume any one of these may be active. A macro that prints to the status bar is friendlier than one that pops a modal — modals during a build dialogue can deadlock the user against the kill switch.

## Trigger recipes

```csharp
// React to failed builds
// @trigger Build.SolutionBuildDone when success=false

// Format only C# files on save
// @trigger Document.Saved when filename=*.cs

// Notify when tests project finishes building
// @trigger Build.ProjectBuildDone when project=*Tests*

// Block File.Save during a build
// @trigger BeforeCommand File.Save

// Track ExceptionThrown for a specific type
// @trigger Debugger.ExceptionThrown when exception=*NullReferenceException*

// Run after a refactoring command
// @trigger AfterCommand Refactor.Rename
```

## Anti-patterns — don't do these

| Don't | Why | Do instead |
|---|---|---|
| Put `// @trigger` lines below `#load` or after executable code. | The header parser stops at the first non-comment, non-blank line. Triggers below it are invisible. | Header block first, then `#load`, then code. |
| Use `BeforeCommand` for purely observational logic. | `BeforeCommand` blocks the user's command for up to 2 s. If the macro is just logging, you're paying a UX tax for nothing. | Use `AfterCommand` (queued, non-blocking) instead. |
| Call `await Task.Delay(...)` from a `BeforeCommand` macro. | The 2 s timeout is wall-clock; the user sees a frozen IDE. | Keep `BeforeCommand` synchronous and short. Push slow work to `AfterCommand`. |
| Have a `BeforeCommand File.Save` macro that itself calls `File.Save`. | Re-entrance guard suppresses the inner trigger silently. The macro doesn't loop, but its second `File.Save` simply doesn't run. | Re-architect: don't `File.Save` from a save trigger. |
| Rely on `Trigger.Payload["Success"]` for a `Document.Saved` event. | `Success` is only populated for `Build.*` and `AfterCommand` triggers. Other events have their own keys. | Check the event's own payload schema, or just react unconditionally. |

## Reference

- Trigger parser: `src/Macros.Engine/Triggers/TriggerDirectiveParser.cs`
- Event catalogue: `src/Macros.Engine/Triggers/KnownEvents.cs`
- Re-entrance guard: `src/Macros.Engine/Triggers/TriggerReentranceGuard.cs`
- Failure tracker: `src/Macros.Engine/Triggers/MacroFailureTracker.cs`
- Full reference: `docs/triggers.md`
