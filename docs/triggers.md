# Triggers

A **trigger** auto-runs a macro on an IDE event. Triggers are declared as `//` comments at the very top of the `.csx` file, before any other non-comment line. Multiple `@trigger` lines are **OR**-combined — any one firing invokes the macro.

## The `@trigger` directive syntax

```csharp
// @trigger Manual                                   // explicit (also the default)
// @trigger Build.SolutionBuildDone                  // VS event
// @trigger Build.SolutionBuildDone when success=false
// @trigger Document.Saved when filename=*.cs        // glob filter
// @trigger Solution.OnAfterOpenSolution
// @trigger BeforeCommand File.Open                  // sync; can cancel
// @trigger AfterCommand Build.BuildSolution         // queued; observation only
```

## Trigger types

| Type              | Syntax                                     | Behavior                                                                                                                                                                                                                                                                                    |
| ----------------- | ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Manual**        | `// @trigger Manual`                       | Default. Macro runs only when invoked by the user (from tool window, hotkey, Quick Launch, etc.).                                                                                                                                                                                           |
| **VS event**      | `// @trigger {Category}.{EventName}`       | Subscribes to an event on `Community.VisualStudio.Toolkit.VS.Events`. ~40 events available. Examples: `Build.SolutionBuildDone`, `Build.ProjectBuildDone`, `Document.Saved`, `Solution.OnAfterOpenSolution`, `Debugger.EnterBreakMode`, `Selection.SelectionChanged`.                       |
| **BeforeCommand** | `// @trigger BeforeCommand {Command.Name}` | Runs **synchronously before** a named VS command executes. Can call `Trigger.CancelCommand()` to abort the command. Command names are the same ones you see in **Tools → Options → Environment → Keyboard** (e.g., `File.Save`, `File.Open`, `Build.BuildSolution`, `Edit.FormatDocument`). |
| **AfterCommand**  | `// @trigger AfterCommand {Command.Name}`  | Runs **after** the command completes. Queued — never blocks the IDE. Can observe success/failure via `Trigger.Data["Success"]` (bool).                                                                                                                                                      |

## Filters

Append `when key=value` (multiple keys are AND-combined) to narrow when a trigger fires:

| Filter key  | Type | Applies to                                                          |
| ----------- | ---- | ------------------------------------------------------------------- |
| `filename`  | glob | `Document.*` events (e.g., `Document.Saved`, `Document.Opened`)     |
| `success`   | bool | `Build.SolutionBuildDone`, `Build.ProjectBuildDone`, `AfterCommand` |
| `project`   | glob | `Build.ProjectBuildDone`                                            |
| `exception` | glob | `Debugger.ExceptionThrown`                                          |

Example: trigger on C# file saves only:

```C#
// @trigger Document.Saved when filename=*.cs
```

Example: trigger on failed builds only:

```C#
// @trigger Build.SolutionBuildDone when success=false
```

Unrecognized filter keys are ignored (logged at debug verbosity). Unknown event names cause the macro to be marked invalid in the tool window.

## The Manage Triggers dialog

Don't memorize the syntax — right-click a macro in the tool window and choose **Manage Triggers…** to get a guided editor:

![Manage Triggers dialog](img/manage-triggers.png)

The dialog:

- Autocompletes event names (from the live `VS.Events` reflection catalog) and command names (from `DTE.Commands`).
- Validates filter keys.
- Rewrites the macro's header atomically — your script body bytes are not touched, and CRLF/LF line endings are preserved.

## Trust gate

Repo macros with auto-triggers require per-solution approval before their triggers register. See [Security: Trust gate](security.md#trust-gate).

## Re-entrance protection

Triggers that invoke the same command can create infinite loops. The engine prevents this by:

- Capping recursion depth at 3 levels.
- Tracking ongoing trigger executions per macro.

If a `BeforeCommand File.Save` macro invokes `File.Save`, it won't trigger again. Manual invocation is always allowed.

## Execution order

- **VS events**, **BeforeCommand**, and **AfterCommand** macros are executed on a serial async queue — never overlapping.
- Multiple triggers on the same macro fire together (OR logic); all are queued as one unit.
- `BeforeCommand` runs synchronously with a 2-second timeout (configurable via **Tools → Options → Macros → General → BeforeCommandTimeoutMs**).
- `AfterCommand` queues asynchronously and does not block the IDE.

## Kill switch

The **Disable All Triggers** option (Tools → Options → Macros → General) instantly disables every trigger globally — handy when recording or debugging. Manual invocation still works. The toolbar **Toggle Triggers** button toggles this setting on/off.
