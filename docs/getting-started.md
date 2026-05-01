# Getting Started

## 60-second setup

1. **Install** Macros from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/) (or sideload the `.vsix` from [Releases](https://github.com/MadsKristensen/Macros/releases)) and restart Visual Studio.
2. Open any code file. Press **`Ctrl+Shift+R`** — the status bar shows *"Macros: Recording…"*.
3. Do something — type code, run a refactoring, execute a command (e.g., *Edit.FormatDocument*), save the file.
4. Press **`Ctrl+Shift+R`** again to stop. (Or click the red square on the **Macros** toolbar.)
5. Move your caret somewhere new and press **`Ctrl+Shift+P`** to **play** the recording back.

That's it. Your recording is saved as `current.csx`; press `Ctrl+Shift+P` again any time to replay it. To keep it permanently, see [Saving macros](#saving-macros) below.

> 💡 **Tip:** Press `Esc` during playback to cancel a misbehaving macro.

## Saving macros

The most recent recording is always the **current macro** (`current.csx`). It survives VS restart but is overwritten by the next recording. To keep one permanently:

1. Right-click the **Macros** toolbar (or open the tool window) and choose **Save As…**.
2. Pick a **name** and a **scope**:
   - **Global** — stored in `%APPDATA%\Macros\<name>.csx`. Available in every solution.
   - **Repo** — stored in `<solution>\.vs\Macros\<name>.csx`. Available only in this solution; commit the folder to share with your team.
3. The dialog shows the resolved path before you confirm.

### Naming rules

- Letters, digits, dashes, underscores, dots, and spaces. No path separators.
- Names are case-insensitive on Windows. `MyMacro` and `mymacro` collide.
- The name `current` is reserved (used by the recording slot).
- Renaming through the tool window does an atomic file rename — your VS-open document tab is preserved.

> 💡 **Tip:** To share repo macros with your team, commit the `.vs/Macros/` folder. By default `.vs/` is gitignored — add a targeted exception:
> ```gitignore
> # Track shared macros, ignore the rest of .vs
> !.vs/
> .vs/*
> !.vs/Macros/
> ```

## Hotkeys

The default key bindings are:

| Hotkey | Command | Action |
|--------|---------|--------|
| `Ctrl+Shift+R` | `Macros.Record` | Start / stop recording (toggles). |
| `Ctrl+Shift+P` | `Macros.PlayLast` | Play the last-recorded macro. |

### Rebinding hotkeys

1. Open **Tools → Options → Environment → Keyboard**.
2. In the **Show commands containing** box, type **`Macros.`**. Two commands appear:
   - `Macros.Record`
   - `Macros.PlayLast`
3. Select one, click into the **Press shortcut keys** box, and press your new combination.
4. Set **Use new shortcut in** to `Global` (works everywhere) or `Text Editor` (only when an editor has focus).
5. Click **Assign**, then **OK**.

If your new chord is already bound elsewhere, the **Shortcut currently used by** dropdown shows the conflict — pick a different chord or unbind the existing one.

### Alternatives to hotkeys

Every action is reachable from the UI:

- The **Macros** toolbar (View → Toolbars → Macros) has Record / Stop / Play / Show Window / Toggle Triggers buttons.
- The **Macros** tool window lets you Play / Edit / Delete / Move from a right-click menu.
- All commands are reachable via **Quick Launch** (`Ctrl+Q`) — type *"macros"* to find them.

## Hotkey conflicts

Known collisions:

- **`Ctrl+Shift+R`** shadows `View.RefreshRemoteReferences` in some VS profiles, and is used by ReSharper, GitHub CodeSpaces, and several testing extensions.
- **`Ctrl+Shift+P`** is the default for some test-runner extensions (*Run Tests in Context*) and for the command palette in VS Code-style keymaps.

Both are rebindable via Tools → Options → Keyboard (see [Rebinding hotkeys](#rebinding-hotkeys) above).
