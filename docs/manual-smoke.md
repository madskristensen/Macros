# Macros — Manual Smoke Test Checklist (v1.0.0)

This checklist verifies shipping quality in a real Visual Studio 2022 instance.
Each item should take 1-3 minutes. Total runtime: ~30 minutes.

**Prerequisites:** Visual Studio 2022 (17.10+), Macros.vsix built (Release config), Experimental hive available (`/rootsuffix Exp`).

## How to use this checklist
1. Press F5 in the Macros solution to launch the Experimental instance.
2. Open ANY solution (e.g., create a blank Console App).
3. Work through the items in order. Check each `[ ]` once verified.
4. If an item FAILS, file an issue with: which step, what happened vs expected.

---

## Items

### [ ] 1. Extension loads without errors
**Steps:**
1. Open the Experimental instance (F5 from Macros.slnx).
2. Open Output > Show output from > "Macros" pane.
3. Open Tools > Get Extensions and Updates > Installed and confirm "Macros for Visual Studio" is present.

**Expected:**
- Extension is installed and enabled.
- No errors in ActivityLog (`%APPDATA%\Microsoft\VisualStudio\17.0_<hash>Exp\ActivityLog.xml`).
- Output pane "Macros" exists (may be empty).

---

### [ ] 2. Toolbar appears and buttons render correctly
**Steps:**
1. Open View > Toolbars > Macros.
2. Observe the toolbar.

**Expected:**
- Toolbar displays with: Record button (Record icon), Stop button (initially hidden), Play Last (Play icon), Show Macros tool window.
- Hover each button — tooltip text appears.

---

### [ ] 3. Recording basic actions
**Steps:**
1. Open any C# file.
2. Press Ctrl+Shift+R (or click Record toolbar button).
3. Verify status bar shows "Macros: Recording...".
4. Type "Hello world" + Enter.
5. Press Ctrl+Shift+R again to stop.
6. Verify status bar reverts to "Macros: Idle".

**Expected:**
- Recording starts, Stop button replaces Record on toolbar.
- After stop, the recorded macro is held as `current.csx`.

---

### [ ] 4. Replaying recorded actions
**Steps:**
1. Place caret in a different location.
2. Press Ctrl+Shift+P.

**Expected:**
- Status bar shows "Macros: Playing..." briefly.
- The same actions replay (typing "Hello world" + Enter).
- Status bar reverts to Idle.
- No errors in Output pane.

---

### [ ] 5. Save As dialog
**Steps:**
1. After recording, click "Save As..." in tool window or via command palette.
2. Verify dialog opens.
3. Type "test-macro" as name. Try invalid characters like `<`. Verify validation message appears.
4. Choose Global radio. Verify path preview shows `%APPDATA%\Macros\Macros\test-macro.csx`.
5. Switch to Repo radio (if solution open). Verify path shows `<solution>\.vs\Macros\test-macro.csx`.
6. Click Save.

**Expected:**
- Dialog opens centered on owner.
- Validation works inline.
- Path preview updates as you type.
- File saved to disk at the indicated path.

---

### [ ] 6. Tool window shows saved macros
**Steps:**
1. Click "Show Macros" toolbar button (or View > Other Windows > Macros).
2. Observe the macro you just saved.
3. Filter by typing part of the name.
4. Verify the macro appears in the appropriate group (Repo or Global).

**Expected:**
- Tool window opens (default docked left or wherever VS placed it).
- Saved macro shows in correct group.
- Filter narrows the list.
- Theming matches VS theme (try Tools > Options > Environment > General > Color theme: dark / light / blue and verify panel re-themes).

---

### [ ] 7. Right-click context menu
**Steps:**
1. Right-click a macro in the tool window.
2. Verify menu appears with: Play, Edit Macro, Rename..., Delete, Open Folder in Explorer, Move to Repo (or Global), Manage Triggers...

**Expected:**
- Menu is themed natively (matches VS context menus).
- Move to Repo only appears for Global macros (and only when solution open).
- Move to Global only appears for Repo macros.

---

### [ ] 8. Edit Macro opens .csx in editor
**Steps:**
1. Right-click a macro > Edit Macro.

**Expected:**
- File opens in VS editor with C# colorization.
- A blue InfoBar appears the FIRST time only: "Tip: This macro can run automatically on VS events..."
- Status bar shows "Macros: Editing {name}".

---

### [ ] 9. Manage Triggers dialog
**Steps:**
1. Right-click a macro > Manage Triggers.
2. Add an event trigger: Kind=VS Event, Name="Build.SolutionBuildDone".
3. Click Add.
4. Verify the trigger appears in the list.
5. Click Save.
6. Re-open the .csx file and verify `// @trigger Build.SolutionBuildDone` is now in the header.

**Expected:**
- Dialog has Kind dropdown, Name input, Filters input, Add button, list of current bindings, Remove button per row.
- Saving rewrites only the header — body bytes unchanged.

---

### [ ] 10. Trigger fires
**Steps:**
1. With the macro from step 9 saved (Global scope is fine — Repo would need trust), trigger a build (Build > Build Solution or Ctrl+Shift+B).
2. Watch the status bar.

**Expected:**
- After build completes, status bar briefly shows "Macros: [Trigger:VsEvent] {macroName}" then reverts.
- Macro body executes.
- Output pane "Macros" may show diagnostic messages.

---

### [ ] 11. Trust gate for Repo macros
**Steps:**
1. Add a Repo macro with a trigger (use the Manage Triggers dialog).
2. Close the solution.
3. Re-open the solution.

**Expected:**
- Yellow InfoBar appears at top of editor: "This solution contains N macro(s) with auto-run triggers. They are blocked until you trust this solution. [Trust] [Block] [Manage] [Dismiss]".
- Trigger does NOT auto-run on subsequent build (because untrusted).
- Click Trust → InfoBar closes, the trigger fires on next build.

---

### [ ] 12. Esc cancels replay
**Steps:**
1. Save a macro that types a long string (record yourself typing 50+ characters).
2. Play it via Ctrl+Shift+P.
3. While playing, press Esc.

**Expected:**
- Replay stops mid-execution.
- Status bar shows "Macros: Replay cancelled" briefly.
- No InfoBar (cancel is silent).

---

### [ ] 13. Recording cap
**Steps:**
1. Tools > Options > Macros > General. Set MaxRecordingSteps to 10. Click OK.
2. Start recording. Type rapidly to exceed 10 steps.
3. Observe behavior.

**Expected:**
- After ~10 steps, recording auto-stops.
- InfoBar appears: "Recording stopped — reached MaxRecordingSteps. [Open Settings]".
- Status bar reverts to Idle.
- The captured macro is still saveable.

---

### [ ] 14. Auto-disable
**Steps:**
1. Edit a macro to deliberately throw: `throw new System.Exception("forced");`
2. Save it. Add a trigger via Manage Triggers (e.g., BeforeCommand File.Save).
3. Save any file 3 times in a row.

**Expected:**
- After the 3rd failure, the macro is auto-disabled.
- 4th save does NOT invoke the macro.
- An InfoBar (or notification) indicates auto-disable.
- Re-enabling via Manage Triggers (or other UI path) restores it.

---

### [ ] 15. Kill switch
**Steps:**
1. Click "Toggle All Triggers" button on the toolbar (or Tools > Options > Macros > General > Disable all triggers checkbox).
2. Observe status bar.
3. Trigger a build. Verify NO trigger macros run.
4. Toggle off. Verify triggers fire again.

**Expected:**
- Status bar shows "Macros: Triggers disabled" while kill switch is on.
- No macros auto-run.
- Toggling off resumes triggers immediately (no restart needed).

---

## Final notes

After completing all items:
- All 15 boxes checked → ✅ READY TO SHIP.
- Any failures → file an issue, do NOT publish.

Note: items 14 (auto-disable) and 15 (kill switch toggle) require working Tools > Options interactions with checkbox/save; verify these UI paths work end-to-end.

Tip: Capture screenshots during the run; use them in Marketplace listing.

---

**Tester:** _________________  **Date:** _________________  **VS Build:** _________________  **Pass/Fail:** _________________
