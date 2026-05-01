# Recording Macros

## What gets captured

When you record a macro, the recorder captures:

- **Text edits** — characters typed, deletions, selections (caret position is recorded with intelligent fallback logic).
- **Command invocations** — every named VS command executed (e.g., `Edit.FormatDocument`, `File.Save`, `Refactor.Rename`).
- **Cursor movements** — line and column positions for caret repositioning.

## How recording works

Press **`Ctrl+Shift+R`** to start recording. The status bar shows *"Macros: Recording…"* and the Stop button appears on the toolbar. All IDE actions are logged until you press **`Ctrl+Shift+R`** again (or click Stop). The recorded sequence is emitted as readable C# code (`.csx` script) and stored temporarily as `current.csx`.

### Step aggregation

Consecutive single-character typings are automatically coalesced into readable `await TypeAsync("…")` calls instead of separate logging per character. This keeps generated code clean and compact.

## Current macro slot (`current.csx`)

The most recent recording always occupies the **current** slot. It survives VS restart but is overwritten by the next recording. To preserve a recording, see [Getting Started: Saving macros](getting-started.md#saving-macros).

## Recording cap

By default, recordings are capped at **5000 steps** (configurable via **Tools → Options → Macros → General → MaxRecordingSteps**). When the limit is reached, recording stops automatically and an InfoBar surfaces. Adjust this setting if you record long sessions, or lower it to fail fast on runaway recordings.

## Scope choice on save

When you save a recording, you must choose a scope via the **Save As** dialog:

- **Global** — macros stored per-user in `%APPDATA%\Macros\`. Available in every solution you open.
- **Repo** — macros stored per-solution in `<solution>\.vs\Macros\`. Committable to source control; team-shared.

See [Scopes](scopes.md) for more detail on scope semantics and storage.
