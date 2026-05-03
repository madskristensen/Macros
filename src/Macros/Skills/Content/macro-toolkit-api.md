---
name: macro-toolkit-api
description: Use the Community.VisualStudio.Toolkit `VS` facade from inside a Macros for Visual Studio .csx macro. Use when a macro should show a status bar message, message box, info bar, or use VS.Documents / VS.Solutions / VS.Windows. Covers VS.StatusBar.ShowMessageAsync, VS.MessageBox.ShowAsync, VS.InfoBar, VS.Documents.GetActiveDocumentViewAsync, VS.Windows.CreateOutputWindowPaneAsync. Do NOT use for writing a Toolkit-based VSIX (commands, tool windows, package services); this skill is exclusively for consuming the toolkit from a .csx macro.
---

# Community Toolkit API in macros
Use the `VS` facade inside a macro for friendly shell UI, document services, and lightweight Visual Studio interactions.

## When to use this skill
Use this skill when the macro should show status, warn the user, display an info bar, or query the active document view through Community.VisualStudio.Toolkit.

Do **not** use this skill for writing Toolkit-based VSIX commands, tool windows, or package services. In this repo, the toolkit is consumed from inside `.csx` macros only.

## The `VS` facade
Inside a macro, `VS` refers to the static Community.VisualStudio.Toolkit facade.

Reference: https://github.com/VsixCommunity/Community.VisualStudio.Toolkit/wiki

Typical calls look like this:

```csharp
await VS.StatusBar.ShowMessageAsync("Macro started");
await VS.MessageBox.ShowWarningAsync("Macros", "Nothing is selected");
```

## Status bar
Use the status bar for lightweight diagnostics and progress breadcrumbs:

```csharp
await VS.StatusBar.ShowMessageAsync("Formatting current document...");
await ExecuteCommandAsync("Edit.FormatDocument");
await VS.StatusBar.ShowMessageAsync("Format complete");
```

Choose the status bar when the message is informative and non-blocking.

## Message boxes
Use a message box when the user must notice the problem:

```csharp
if (DTE.ActiveDocument == null)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "Open a document first.");
    return;
}
```

Good uses:
- Blocking destructive actions
- Explaining why a command was cancelled
- Telling the user a required precondition is missing

## Info bars
Info bars work well for richer, dismissible notifications.

```csharp
var model = new InfoBarModel(
    textSpans: new[]
    {
        new InfoBarTextSpan("Macro finished. Review the Output window if you need details.")
    },
    actionItems: new[]
    {
        new InfoBarHyperlink("Open Output", "open-output")
    },
    image: Microsoft.VisualStudio.Imaging.KnownMonikers.StatusInformation,
    isCloseButtonVisible: true);

InfoBar? bar = await VS.InfoBar.CreateAsync(model);
if (bar != null)
{
    await bar.TryShowInfoBarUIAsync();
}
```

Use an info bar when a status-bar message is too easy to miss but a modal dialog would be too heavy.

## Active document view
Use the document service when the macro needs the active editor surface, not just `DTE.ActiveDocument` metadata:

```csharp
DocumentView? view = await VS.Documents.GetActiveDocumentViewAsync();
if (view?.TextView == null)
{
    await VS.StatusBar.ShowMessageAsync("No active text view");
    return;
}

await VS.StatusBar.ShowMessageAsync($"Text view ready for {view.FilePath}");
```

## Toolkit + helpers is a strong combo
A common macro pattern is:
1. Read state through `DTE` or `VS.Documents`
2. Act through a helper like `ExecuteCommandAsync` or `TypeAsync`
3. Report through `VS.StatusBar`, `VS.MessageBox`, or `VS.InfoBar`

Example:

```csharp
DocumentView? view = await VS.Documents.GetActiveDocumentViewAsync();
if (view == null)
{
    await VS.MessageBox.ShowWarningAsync("Macros", "No active document view.");
    return;
}

await ExecuteCommandAsync("Edit.FormatDocument");
await VS.StatusBar.ShowMessageAsync($"Formatted {view.FilePath}");
```

## Practical defaults
### Non-blocking feedback
```csharp
await VS.StatusBar.ShowMessageAsync("Macros: Done");
```

### Precondition failure
```csharp
await VS.MessageBox.ShowWarningAsync("Macros", "This macro needs an open text document.");
```

### Rich follow-up notification
```csharp
InfoBar? bar = await VS.InfoBar.CreateAsync(
    new InfoBarModel(
        textSpans: new[] { new InfoBarTextSpan("Build failed — open Error List?") },
        actionItems: new[] { new InfoBarHyperlink("Open", "open-errors") },
        image: Microsoft.VisualStudio.Imaging.KnownMonikers.StatusWarning,
        isCloseButtonVisible: true));

if (bar != null)
{
    await bar.TryShowInfoBarUIAsync();
}
```

## Good defaults for Copilot
When generating Toolkit-based macros:
- Prefer `VS.StatusBar.ShowMessageAsync` for quick feedback
- Prefer `VS.MessageBox.ShowWarningAsync` for important warnings
- Reach for `VS.InfoBar.CreateAsync` when the user needs a clickable, non-modal notification
- Use `await VS.Documents.GetActiveDocumentViewAsync()` when the macro needs the live document view
- Keep Toolkit usage inside the macro body; do not drift into VSIX/package setup guidance
