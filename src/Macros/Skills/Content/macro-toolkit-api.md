---
name: macro-toolkit-api
description: Use the Community.VisualStudio.Toolkit `VS` facade from inside a Macros for Visual Studio .csx macro. Use when the macro should show a status-bar message (`VS.StatusBar.ShowMessageAsync`), a message box (`VS.MessageBox.ShowAsync`/`ShowWarningAsync`/`ShowErrorAsync`), an info bar (`VS.InfoBar.CreateAsync`), the Output window (`VS.Windows.CreateOutputWindowPaneAsync`), the active document view (`VS.Documents.GetActiveDocumentViewAsync`), or any solution / window service through the toolkit. Do NOT use for writing a Toolkit-based VSIX (commands, tool windows, package services); this skill is exclusively for consuming the toolkit from a `.csx` macro.
---

# Using the Community Toolkit `VS` facade from a macro

The Community.VisualStudio.Toolkit's `VS` static class is a friendly wrapper over the Visual Studio SDK. Inside a macro, the script host emits `using static Community.VisualStudio.Toolkit.VS;` for you (via the IntelliSense shim — see [`writing-macros`](../writing-macros/SKILL.md)), so you can call `VS.StatusBar.ShowMessageAsync(...)` directly.

When to use which surface:

- **Status bar** — non-blocking single-line feedback. Cheapest. Easy to miss.
- **Message box** — blocking, attention-demanding. For preconditions and warnings.
- **Info bar** — non-modal but anchored to a window frame; supports clickable hyperlinks. The right choice when the user should notice but not be interrupted.
- **Output pane** — multi-line diagnostic output. For logs, error dumps, command transcripts. Prefer the `Log` static class (see [`macro-debugging`](../macro-debugging/SKILL.md)) which writes to the standard "Macros" pane with attribution.
- **Document service** — when you need the live `ITextView` / `DocumentView`, not just the `EnvDTE.Document` metadata that DTE gives you.

For DTE state reads (active document, selection, build state) see [`macro-dte-api`](../macro-dte-api/SKILL.md). For diagnosing why a `VS.*` call did nothing, see [`macro-debugging`](../macro-debugging/SKILL.md).

## Surface map

| Surface | Async method | Use it for |
|---|---|---|
| `VS.StatusBar` | `ShowMessageAsync(string)` | Single-line, non-blocking status. |
| `VS.MessageBox` | `ShowAsync` / `ShowWarningAsync` / `ShowErrorAsync` | Modal preconditions / explanations. |
| `VS.InfoBar` | `CreateAsync(IVsWindowFrame, InfoBarModel)` (preferred) or `CreateAsync(InfoBarModel)` (fallback) | Non-modal, dismissible, with optional clickable links. |
| `VS.Windows` | `CreateOutputWindowPaneAsync(string name, bool lazyCreate)` | Get / create an Output pane. |
| `VS.Documents` | `GetActiveDocumentViewAsync()` | Get the live `DocumentView` for the focused editor. |
| `VS.Solutions` | `GetCurrentSolutionAsync()` | Awaitable solution access — the toolkit's wrapper around `DTE.Solution`. |

## Status bar — quick non-blocking feedback

```csharp
await VS.StatusBar.ShowMessageAsync("Formatting current document...");
await ExecuteCommandAsync("Edit.FormatDocument");
await VS.StatusBar.ShowMessageAsync("Format complete");
```

Keep messages short. The status bar is overwritten frequently by other VS components — it's not a log.

## Message box — for must-notice events

```csharp
if (DTE.ActiveDocument is null)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Open a document first.");
    return;
}
```

The first parameter is the dialog title. Variants:

- `ShowAsync(title, message)` — `OK` only.
- `ShowConfirmAsync(title, message)` — returns `true` for OK, `false` for Cancel.
- `ShowWarningAsync(title, message)` — yellow icon.
- `ShowErrorAsync(title, message)` — red icon.

Prefer the title `"Macros"` so the user can quickly identify the source.

## Info bar — non-modal, dismissible, with actions

The recommended pattern is the **frame-anchored overload**: build the model, anchor the bar to the active document's window frame, and fall back to the shell-level info bar host only if no document is active.

```csharp
#load ".intellisense/Macros.Intellisense.csx"
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell.Interop;

var model = new InfoBarModel(
    textSpans: new[] { new InfoBarTextSpan("Build failed. Open the Error List?") },
    actionItems: new[] { new InfoBarHyperlink("Open Error List", "open-errors") },
    image: KnownMonikers.StatusWarning,
    isCloseButtonVisible: true);

InfoBar? bar = null;

DocumentView? docView = await VS.Documents.GetActiveDocumentViewAsync();
if (docView?.WindowFrame is IVsWindowFrame frame)
{
    bar = await VS.InfoBar.CreateAsync(frame, model);
}

bar ??= await VS.InfoBar.CreateAsync(model);

if (bar is not null)
{
    bar.ActionItemClicked += (s, e) =>
    {
        if (e.ActionItem.ActionContext is "open-errors")
        {
            _ = ExecuteCommandAsync("View.ErrorList");
        }
    };
    await bar.TryShowInfoBarUIAsync();
}
```

Why frame-anchored is preferred:

- It pins the bar to the document the user is looking at — the user sees it immediately.
- The bare `CreateAsync(InfoBarModel)` overload anchors to the shell's main window, which works but can be missed entirely if the user is working in a docked editor.

## Output pane — multi-line diagnostics

For most logging, prefer the `Log` static class (it writes to the same `"Macros"` pane and prefixes each line with the macro name and timestamp). When you specifically want a *separate* pane:

```csharp
var pane = await VS.Windows.CreateOutputWindowPaneAsync("My Macro Log", lazyCreate: false);
await pane.WriteLineAsync("=== Run started ===");
await pane.WriteLineAsync($"Active document: {DTE.ActiveDocument?.FullName}");
await pane.ActivateAsync();
```

`CreateOutputWindowPaneAsync` is idempotent: calling it twice with the same name returns the existing pane. `lazyCreate: false` materialises the pane immediately so the user can see it; `true` defers until the first write.

## Document view — when DTE.ActiveDocument isn't enough

`VS.Documents.GetActiveDocumentViewAsync()` returns a `DocumentView` that exposes the live `ITextView`, `ITextBuffer`, and `IVsWindowFrame`. Use it when you need editor extensibility surfaces — adornments, tag spans, view options — that DTE doesn't expose.

```csharp
DocumentView? view = await VS.Documents.GetActiveDocumentViewAsync();
if (view?.TextView is null)
{
    await VS.StatusBar.ShowMessageAsync("No active text view");
    return;
}

// view.FilePath, view.TextBuffer, view.TextView, view.WindowFrame are all available.
await VS.StatusBar.ShowMessageAsync($"Text view ready: {view.FilePath}");
```

For simple "is a file open?" checks, `DTE.ActiveDocument` is lighter.

## Common patterns

### Status → action → status pattern

```csharp
await VS.StatusBar.ShowMessageAsync("Macros: cleaning up...");
await ExecuteCommandAsync("Edit.RemoveAndSort");
await VS.StatusBar.ShowMessageAsync("Macros: done.");
```

### Confirm-then-act

```csharp
if (await VS.MessageBox.ShowConfirmAsync("Macros", "Reformat all open documents?"))
{
    foreach (EnvDTE.Document doc in DTE.Documents)
    {
        doc.Activate();
        await ExecuteCommandAsync("Edit.FormatDocument");
    }
}
```

### Click-through info bar with command dispatch

```csharp
var model = new InfoBarModel(
    textSpans: new[] { new InfoBarTextSpan("Tests passed but 3 warnings.") },
    actionItems: new[] { new InfoBarHyperlink("View output", "view-output") },
    image: KnownMonikers.StatusInformation,
    isCloseButtonVisible: true);

DocumentView? docView = await VS.Documents.GetActiveDocumentViewAsync();
InfoBar? bar = docView?.WindowFrame is IVsWindowFrame frame
    ? await VS.InfoBar.CreateAsync(frame, model)
    : await VS.InfoBar.CreateAsync(model);

if (bar is not null)
{
    bar.ActionItemClicked += (s, e) =>
    {
        if (e.ActionItem.ActionContext is "view-output")
        {
            _ = ExecuteCommandAsync("View.Output");
        }
    };
    await bar.TryShowInfoBarUIAsync();
}
```

## Anti-patterns — don't do these

| Don't | Why | Do instead |
|---|---|---|
| `await VS.InfoBar.CreateAsync(model)` as the *only* path | The bare overload can land in a host the user doesn't see (no document open, focus on a tool window). | Try the frame overload first; fall back to bare. See "Info bar" pattern above. |
| Forget to subscribe to `bar.ActionItemClicked` before `TryShowInfoBarUIAsync` | The hyperlink works but your handler isn't called. | Wire `ActionItemClicked` first, *then* show. |
| Use `VS.MessageBox.ShowAsync` from a `BeforeCommand` trigger | Modals have a 2 s window before the trigger times out and the original command runs anyway. | Do the check fast and use `Trigger.CancelCommand()` — see [`macro-triggers`](../macro-triggers/SKILL.md). |
| Open a custom Output pane named "Macros" | Collides with the standard pane that `Log.*` and the macro error renderer use. | Pick a distinct name (e.g. `"My Macro Log"`) or just use `Log.InfoAsync(...)`. |
| Spam the status bar inside a tight loop | The bar repaints synchronously; rapid updates make the IDE feel unresponsive. | Update at meaningful checkpoints, not every iteration. |

## Reference

- Toolkit wiki: <https://github.com/VsixCommunity/Community.VisualStudio.Toolkit/wiki>
- Reference implementation showing the recommended `CreateAsync(frame, model)` pattern: `src/Macros/Errors/MacroErrorRenderer.cs` and `src/Macros/Onboarding/OnboardingInfoBar.cs`
