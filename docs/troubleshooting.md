# Troubleshooting

## Tool window not refreshing

**Symptom:** You save or delete a macro file outside VS, but the tool window still shows the old list.

**Solutions:**
1. Close the tool window (View → Other Windows → Macros) and reopen it.
2. The file system watchers have a 200ms debounce + 500ms self-write suppression. Wait a few seconds and check again.
3. If manually editing `.csx` files on disk, ensure you're not in the middle of a VS operation (e.g., recording). Save the file and wait.

## Recording not saving

**Symptom:** You press `Ctrl+Shift+P` to play, but nothing happens or an error appears.

**Solutions:**
1. Verify a recording was made — check the status bar shows *"Macros: Recording…"* when you press `Ctrl+Shift+R`.
2. The macro is initially saved to `current.csx` — press `Ctrl+Shift+P` to replay it (even if not permanently saved).
3. If **Save As** fails, check file permissions in `%APPDATA%\Macros\` (Global scope) or `<solution>\.vs\Macros\` (Repo scope).
4. If the macro name contains invalid characters, the **Save As** dialog shows a validation message. Use only letters, digits, dashes, underscores, dots, and spaces.

## Trigger not firing

**Symptom:** You add a `// @trigger` directive to a macro, but it never runs even when the event should fire.

**Solutions:**
1. Verify the trigger syntax is correct — use the **Manage Triggers** dialog (right-click macro → **Manage Triggers…**) instead of hand-editing if unsure.
2. Check the macro's status in the tool window — if it shows a 🔒 (lock) badge, the solution is blocked by the trust gate. Click **Trust this solution** on the info bar.
3. Verify the trigger type and event/command name exist:
   - For VS events (e.g., `Build.SolutionBuildDone`), hover over the trigger in the **Manage Triggers** dialog to see if it's recognized.
   - For commands (e.g., `BeforeCommand File.Save`), the command name must match **Tools → Options → Environment → Keyboard**.
4. Check if **Disable All Triggers** is enabled (look for status bar indicator or check **Tools → Options → Macros → General**). If yes, triggers are suppressed — toggle it off.
5. For `BeforeCommand` triggers, note that they have a 2-second timeout. If your macro runs longer, it's cancelled and the command proceeds (see [Triggers: Execution order](triggers.md#execution-order)).

## Replay does nothing

**Symptom:** You press `Ctrl+Shift+P` or click **Play**, but the macro seems to execute silently with no visible effect.

**Solutions:**
1. Open the **Output** pane (**View → Output**) and select *"Macros"* from the dropdown. Check for error messages or diagnostic output.
2. If the macro executes too fast to see, add a `await WaitAsync(1000);` call to the script (right-click → **Edit Macro**) to pause before continuing. Re-save and replay.
3. If the macro is supposed to edit the current document, ensure you have a document open with a caret position. Some macros expect a specific editor context.
4. If the script references `DTE` or `Context` objects and they're `null`, the macro environment might not have initialized properly. Try closing and reopening the solution.
5. Check if **Esc** is being pressed during replay. Press it again to resume, or replay from the start.

## Macro marked invalid

**Symptom:** A macro appears in the tool window with a red ⚠️ warning badge or a message saying the trigger is invalid.

**Solutions:**
1. Right-click the macro → **Edit Macro** to open the `.csx` file.
2. Scroll to the top and check the `// @trigger` lines for typos:
   - Event triggers: `Build.SolutionBuildDone`, `Document.Saved`, etc. (check spelling)
   - BeforeCommand/AfterCommand: make sure the command name is valid (see **Tools → Options → Environment → Keyboard**)
   - Filters: check that filter keys are recognized (`filename`, `success`, `project`, `exception`)
3. Save the file. The tool window should refresh within a few seconds; if invalid, it shows an error message with the reason.
4. Use the **Manage Triggers** dialog (right-click → **Manage Triggers…**) to fix the trigger via UI instead of hand-editing.

## Extension loads but no buttons appear

**Symptom:** VS loads but the Macros toolbar is missing and commands are unavailable.

**Solutions:**
1. Open **View → Toolbars → Macros** to show the toolbar.
2. Check **View → Other Windows** for the Macros tool window; if missing, it's not registered properly.
3. Open **Tools → Get Extensions and Updates → Installed** and confirm "Macros for Visual Studio" is listed and enabled.
4. Open the **Output** pane (**View → Output**) and select *"Macros"* from the dropdown. Check for errors on initialization.
5. If errors appear, restart VS and try again. If persists, uninstall and reinstall the extension.

## Auto-disable not working as expected

**Symptom:** A macro fails multiple times but doesn't auto-disable, or auto-disables unexpectedly.

**Solutions:**
1. Auto-disable only applies to **triggered** macros (not manual invocations). Manually replaying a broken macro with `Ctrl+Shift+P` won't trigger auto-disable.
2. The counter resets when the macro succeeds once. If you fix the macro and replay it successfully, the failure count resets.
3. If a `BeforeCommand` timeout occurs (2 seconds), it counts as a failure toward auto-disable.
4. To manually re-enable a disabled macro, right-click it in the tool window and look for a **Re-enable** option, or use the **Manage Triggers** dialog.

## Trust gate appears but I trust the solution

**Symptom:** The trust gate info bar keeps appearing even though you've clicked **Trust this solution** before.

**Solutions:**
1. Trust is tied to the **solution path**. If you moved or cloned the solution to a new location, VS treats it as a different solution and re-prompts.
2. Check **Tools → Options → Macros → Trusted Solutions** to see if your solution path is in the list. If not, click **Trust this solution** again on the info bar.
3. If the path has changed (e.g., from `C:\Old\` to `C:\New\`), you'll need to re-trust from the new path. The old path remains in the trusted list.
