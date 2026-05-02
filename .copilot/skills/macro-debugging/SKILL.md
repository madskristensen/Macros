---
name: "macro-debugging"
description: "Troubleshoot Macros .csx scripts: command names, null state, timing, and runtime vs IntelliSense"
domain: "visual-studio-macros"
confidence: "high"
source: "derived from player/shim behavior, helper contracts, and repo docs"
---

# Debugging macros
Troubleshoot failing or flaky `.csx` macros by checking command names, IDE state, async usage, and the editor-only IntelliSense shim.

## When to use this skill
Use this skill when a macro compiles but fails at runtime, when IntelliSense looks right but playback behaves differently, or when a recorded macro needs timing and guard checks.

Do **not** use this skill for attaching a debugger to a VSIX package or diagnosing extension load problems.

## Common failure: command not found
If this line fails:

```csharp
await ExecuteCommandAsync("Edit.SomeCommand");
```

The usual cause is a wrong command name.

Use command names exactly as Visual Studio knows them, such as `Edit.FormatDocument`, `File.Save`, or `Build.BuildSolution`.

```csharp
await VS.StatusBar.ShowMessageAsync("Running Edit.FormatDocument");
await ExecuteCommandAsync("Edit.FormatDocument");
```

## Common failure: `DTE.ActiveDocument` is null
A macro that assumes an open editor can fail when no document is active.

```csharp
if (DTE.ActiveDocument == null)
{
    await VS.StatusBar.ShowMessageAsync("No active document");
    return;
}
```

Do the same for selections:

```csharp
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
if (sel == null)
{
    await VS.StatusBar.ShowMessageAsync("No active text selection");
    return;
}
```

## Runtime vs IntelliSense
The `.intellisense/Macros.Intellisense.csx` file is an editor aid, not runtime logic.

Important facts:
- The shim provides `#r`, common imports, and stub globals so the editor understands the file
- At runtime, the macro player injects the real globals
- IntelliSense success does not guarantee your macro's runtime assumptions are valid

If the editor knows `DTE`, `VS`, `Context`, and `Trigger`, but playback still fails, check the live IDE state rather than the `#load` line.

## The `#load` directive at runtime
Keep this in the file:

```csharp
#load ".intellisense/Macros.Intellisense.csx"
```

At runtime, the player intercepts that path and returns empty source. That is expected.

Do **not** debug by deleting the line.

## Timing issues
Some VS actions need a short delay before the next step.

```csharp
await ExecuteCommandAsync("File.SaveSelectedItems");
await WaitAsync(250);
await ExecuteCommandAsync("Edit.FormatDocument");
```

Use small waits first. Prefer checking state when you can; use delays only when sequencing is the real issue.

## Check state before acting
Macros are more reliable when they verify prerequisites first.

```csharp
if (DTE.ActiveDocument?.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) != true)
{
    await VS.StatusBar.ShowMessageAsync("This macro expects an open C# document");
    return;
}

await ExecuteCommandAsync("Edit.FormatDocument");
```

## Use the status bar for debugging output
For quick diagnostics, emit breadcrumbs with the status bar:

```csharp
await VS.StatusBar.ShowMessageAsync("Step 1: locating selection");
var sel = DTE.ActiveDocument?.Selection as EnvDTE.TextSelection;
await VS.StatusBar.ShowMessageAsync($"Step 2: selection found = {sel != null}");
```

## Async/await gotcha
All built-in helpers are async.
Correct:

```csharp
await TypeAsync("Hello");
await OpenFileAsync(@"C:\Repos\MySolution\Program.cs");
```

Incorrect:

```csharp
TypeAsync("Hello");
OpenFileAsync(@"C:\Repos\MySolution\Program.cs");
```

If you skip `await`, the macro may race ahead or finish before the operation completes.

## Top-level code only
Macros are scripts. You normally do not need classes, package setup, or extension scaffolding.

```csharp
if (DTE.ActiveDocument != null)
{
    await ExecuteCommandAsync("Edit.FormatDocument");
}
```

## Fast debugging checklist
1. Is the command name valid?
2. Is there an active document or selection?
3. Did every helper call use `await`?
4. Does the macro need a small `WaitAsync(...)` between steps?
5. Is the macro depending on editor-only shim behavior instead of runtime state?
6. Can a `VS.StatusBar.ShowMessageAsync(...)` breadcrumb pinpoint the failure?

## Good defaults for Copilot
- Add null guards before touching `DTE.ActiveDocument` or `Selection`
- Add status-bar breadcrumbs before and after risky steps
- Correct command names before changing structure
- Add `WaitAsync` only for real timing issues
- Keep the script as simple top-level code
