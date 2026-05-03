# Macros for Visual Studio 2022 — Marketplace Listing

## Hero

Bring back Visual Studio Macros — modern, scripted, and powerful. Record sequences of text edits and IDE commands, replay them with a single keystroke, or trigger them automatically on build, save, or any IDE event. Every macro is an editable C# script with full IntelliSense — no black boxes, no JSON, pure automation you can read, debug, and version-control.

---

## What It Does

🎬 **Record any sequence of IDE actions** — text edits, command invocations, even cursor movements captured in real time.

🔁 **Replay with a single keystroke** — Ctrl+Shift+P to play your last recorded macro, or call it by name from the toolbar, command palette, or tool window.

📜 **Macros are editable C# scripts** — stored as `.csx` files with full syntax highlighting, IntelliSense, and debugging in the editor itself.

⚡ **Auto-run on VS events** — bind macros to IDE events (`Build.SolutionBuildDone`, `Document.Saved`, etc.) or run them before/after named commands with the trigger system.

---

## Features

**Recording Engine**

- Captures text edits, command invocations, and cursor operations in real time
- Debounced typing steps for readable, minimal code generation
- Thread-safe replay detection prevents infinite loops

**Two Storage Scopes**

- **Global macros** — per-user, stored in `%APPDATA%\Macros\` (yours alone)
- **Repo macros** — per-solution, committed to `.vs\Macros\` (team-shared, version-controlled)
- Repo macros shadow global ones with the same name

**Trigger System**

- **Event triggers** — `// @trigger Build.SolutionBuildDone`, `// @trigger Document.Saved`, etc.
  - Supports ~40 built-in VS events, auto-discovered via Community Toolkit
  - Full IntelliSense in the trigger comment
- **Command triggers** — `// @trigger BeforeCommand Edit.Copy`, `// @trigger AfterCommand File.Save`
  - `BeforeCommand` runs synchronously and can cancel the command via `Trigger.CancelCommand()`
  - `AfterCommand` runs asynchronously and observes success/failure
- **Trigger management UI** — Manage Triggers dialog for visual add/remove workflow

**Security & Stability**

- **Trust gate** — repo macros with triggers require per-solution approval before first run (InfoBar prompt)
- **Manual invocation always allowed** — only auto-trigger registration is gated
- **Auto-disable on failure** — 3 consecutive failures → macro quarantined (requires manual re-enable)
- **Re-entrance protection** — depth cap of 3 prevents circular trigger cascades
- **Kill switch** — instant disable all triggers from the toolbar button

**Tool Window**

- Grouped list view (Global / Repo sections) with step count and last-modified date
- Triggers column shows at-a-glance which macros are event-triggered
- Right-click context menu: Play, Edit, Delete, Move to Repo, etc.
- Filter by name; repo macros shadow globals (strikethrough)
- Empty-state prompts for quick-start onboarding

**Macro Editor Integration**

- Edit macros directly in the VS editor with C# syntax highlighting and IntelliSense
- Macro globals: `DTE`, `VS` (Community Toolkit), `Context`, `Trigger?` (if triggered)
- Helper functions: `Type()`, `MoveCaret()`, `Select()`, `ExecuteCommand()`, `RunCommand()`
- Includes preamble comments showing trigger syntax and examples

**Quick Record & Replay**

- **Ctrl+Shift+R** — Start/stop recording
- **Ctrl+Shift+P** — Play last recorded macro
- **Esc** — Cancel active replay instantly
- Temporary "current macro" slot for one-off recordings (promote to named macro to save)
- Status bar updates during recording and playback

---

## How To Use

### 1. **Record a Macro**

Press **Ctrl+Shift+R** to start recording. Type text, run commands (via keyboard or click), move the cursor, and watch the status bar show "Recording…". Press **Ctrl+Shift+R** again to stop. The macro is now in the temporary slot, ready to replay.

```text
// Example: recorded macro for sorting a #region block
using System.Linq;
VS.StatusBar.ShowMessage("Sorting region…");
Type("#region");
MoveCaret(CaretPosition.LineEnd);
ExecuteCommand("Edit.SelectAll");
ExecuteCommand("Edit.ToggleOutliningExpansion");
```

### 2. **Replay On Demand**

Press **Ctrl+Shift+P** to replay the last macro. Or open the **Macros** tool window (View > Other Windows > Macros), select any macro from the list, and click **Play**. Status bar shows "Playing…" until complete. Press **Esc** to cancel mid-playback.

### 3. **Save It For Later**

Right-click the temporary macro in the tool window or use File > Save Macro As. Choose **Global** (your macros, always available) or **Repo** (commit to `.vs\Macros\`, shared with teammates). Name it something memorable.

### 4. **Add Triggers (Optional)**

Open the macro `.csx` file in the editor and add trigger comments at the top:

```csharp
// @trigger Build.SolutionBuildDone
// @trigger BeforeCommand File.Save

// Your macro code below…
```

Or use **Manage Triggers** from the tool window's context menu to add triggers via UI. Repo macros with triggers prompt for trust on first run (click **[Allow]** in the InfoBar).

---

## Screenshots

- **Macros Tool Window** — Grouped list of Global and Repo macros with Triggers column
- **Recording in Progress** — Toolbar button lit, status bar shows "Recording…"
- **Manage Triggers Dialog** — UI for adding Event, BeforeCommand, or AfterCommand triggers
- **Trust Gate InfoBar** — "This solution has repo macros with triggers. [Review] [Allow] [Block]"
- **Macro Editor** — `.csx` file open with C# syntax highlighting, IntelliSense, and preamble comments

---

## Requirements

- **Visual Studio 2022** (17.0 or later)
- **.NET Framework 4.8** (built-in on Windows 10/11)
- **Windows** operating system

---

## Pricing & License

**Free.** Macros is open-source under the **MIT License** — use it in any project, commercial or personal, without restrictions.

---

## Links & Support

- **GitHub Repository:** [MadsKristensen/Macros](https://github.com/MadsKristensen/Macros)
- **Issues & Feedback:** [GitHub Issues](https://github.com/MadsKristensen/Macros/issues)
- **Sponsor This Project:** [GitHub Sponsors](https://github.com/sponsors/MadsKristensen)

---

## FAQ

**Q: Can I edit recorded macros?**  
A: Yes! Every macro is a `.csx` file. Open it in the editor and modify the C# code directly. Add loops, conditionals, helper functions — the Roslyn scripting host executes it as-is.

**Q: Are my repo macros safe?**  
A: You control when they run. Repo macros with triggers prompt for trust the first time you open the solution (InfoBar). Decline and they won't auto-trigger. Manual invocation is always allowed.

**Q: What if a macro breaks?**  
A: If it fails 3 times in a row, it auto-disables (visible in the tool window). Fix the code and manually re-enable it from the context menu.

**Q: Can I use VS events and command triggers together?**  
A: Yes. A single macro can have both `// @trigger` directives. All triggers that fire are queued on a serial async queue, so no overlaps.

**Q: Does Macros work with Visual Studio 2019?**  
A: No, this version targets VS 2022 only. The Community Toolkit and VS APIs are 2022+ only.
