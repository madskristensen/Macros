# Replaying Macros

## Play Last

The quickest way to replay your most recent recording:

1. Move your caret to a new location (or the same position in a different file).
2. Press **`Ctrl+Shift+P`**.
3. The macro executes instantly. The status bar briefly shows *"Macros: Playing…"* and reverts to *"Idle"* when done.

Press `Escape` during playback to cancel instantly — no hunting for a stop button.

## The Macros tool window

Open it from **View → Other Windows → Macros** (or the *Show Tool Window* toolbar button).

![Tool window screenshot](../art/toolwindow.png)

The tool window groups your macros by scope:

- **Repo** — macros from the open solution's `.vs/Macros/` folder (if any).
- **Global** — your per-user macros.
- **Shadowed Global** *(italic, dimmed)* — global macros hidden by a same-named repo macro. The repo version wins on collision; the shadowed entry is still listed so you know it's there.

Each row shows **Name**, **Triggers** (compact summary like *"Build"* or *"Before File.Save"*), **Steps**, **Modified**, and a one-click **Run** button. The filter box at the top narrows the list as you type.

### Right-click context menu

**Right-click** any macro for a native VS context menu:

| Action                                | Description                                                |
| ------------------------------------- | ---------------------------------------------------------- |
| **Play**                              | Run the macro now (manual invocation, ignores trust gate). |
| **Edit**                              | Open the `.csx` in VS with full IntelliSense.              |
| **Rename**                            | Rename the file (validates name + checks for collisions).  |
| **Delete**                            | Remove the file (with confirmation).                       |
| **Move to Repo** / **Move to Global** | Move between scopes. Conflicts prompt to confirm.          |
| **Manage Triggers…**                  | Open the trigger editor.                                   |
| **Open Folder**                       | Reveal the containing folder in File Explorer.             |

## Executing named macros

From the tool window, click the **Run** button next to any macro, or:

1. Right-click the macro → **Play**.
2. Select the macro and press `Enter`.
3. Use **Quick Launch** (`Ctrl+Q`) and type the macro name.

All methods respect manual invocation — they work even if the macro has auto-triggers disabled by the trust gate.

## Error handling

If a macro fails during playback:

1. An error message appears in the **Output** pane (*Macros* channel).
2. Errors are also logged to the **Error List** with clickable links to the `.csx` line numbers.
3. An InfoBar surfaces with a *"View Output"* link to jump to the error.

If a macro fails 3 times in a row during **triggered** execution, it auto-disables — see [Security](security.md#auto-disable-on-failure).

Manual replays are never auto-disabled, even if they fail multiple times.
