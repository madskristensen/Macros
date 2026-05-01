# Team Decisions — Macros for Visual Studio

**Last updated:** 2026-04-30 21:50 UTC  
**Status:** Design phase complete; M1-M4 master plan assembled.

---

## Active Decisions

### **[CRITICAL] 2026-04-30 21:50 UTC: User Directive — C# Scripts as Macro Storage**

**By:** Mads Kristensen (via Copilot)  
**Status:** Team-locked; canonical storage format.

**Decision:**  
Macro storage and replay pivots from static JSON event lists to executable C# scripts. The recorder generates C# code that calls `DTE` and the Community Toolkit's `VS` static accessor; the player compiles and runs that code via Roslyn scripting. Users can hand-edit the generated C# (with IntelliSense, in VS itself) for far more power than a static JSON list of steps would allow.

**Implications:**
- Storage format: `.csx` (or `.cs`) files in `%APPDATA%\Macros\` — NOT JSON.
- Replay: Roslyn `CSharpScript` host with `DTE` and `VS.` injected as globals.
- Recording: code generator translates captured editor/command events into idiomatic DTE/VS calls.
- Editing UX: "Edit Macro" command opens the .csx in VS itself — first-class editing with IntelliSense.
- Trust model: macros are user code; importing 3rd-party macros requires a "review before run" gate.

**Rationale:**  
User explicitly preferred this model over static JSON: "users can make code changes instead of static json commands." Matches the historical VS macro feature (VBA-style scripting) modernized for VS 2022 + Roslyn.

---

## Supporting Decisions

### 2026-04-30: CSX Architecture for Macros Extension

**Author:** Danny (Engine Architect)  
**Status:** Accepted; details locked.

**Summary:**  
Macros are stored and executed as C# script files (`.csx`) via Roslyn `CSharpScript`. The recorder translates captured editor/command events into idiomatic C# top-level statements. The player compiles and runs those scripts with `DTE` and the Community Toolkit `VS` static accessor injected as globals.

**Key Architecture Points:**

- **Storage:** `%APPDATA%\Macros\current.csx` (current), `%APPDATA%\Macros\macros\{slug}.csx` (named). Atomic write: `.tmp` → `File.Move(..., overwrite: true)`. Optional `.csx.meta.json` sidecar for metadata.
- **Playback:** `CSharpScript.RunAsync` with `ScriptOptions` referencing `EnvDTE`, `Community.VisualStudio.Toolkit`, and `Macros.Engine`. Globals type: `MacroGlobals { DTE2 DTE, VS VS, IMacroContext Context }`.
- **Compilation caching:** `ConcurrentDictionary<string, Script<object>>` keyed on `SHA256(source)`.
- **Recording observers:** `MacroState.IsReplaying` [ThreadStatic] flag prevents re-recording during playback.
- **Code generation helpers:** Five helpers cover ≥ 95% of recorded actions: `Type(string)`, `MoveCaret(int, int)`, `Select(int, int, int, int)`, `ExecuteCommand(string, string?)`, `RunCommand(Guid, uint)`.

---

### 2026-04-30: Macros v1 MVP Scope is Locked

**Author:** Livingston (PM)  
**Status:** Proposed — pending team acknowledgment.

**Decision:**  
The Macros v1 MVP scope is locked to **record, replay, save, and manage** macros.

**Explicitly Deferred:**
- Parameterized macros (user input during replay)
- Conditional branching and loops
- Repeat-N execution
- Loop-over-selection
- Export / import `.vsmacro` file
- Community sharing / Marketplace macro library
- Recording git, build, and debugger actions
- Macro script editing (code editor for macro steps)
- Per-macro custom hotkey assignment
- Run-on-trigger automation (file open, project load)

**Rationale:**  
Tight v1 scope ensures the team ships a reliable, performant, well-tested extension without scripting runtime complexity. Deferred features added incrementally once capture and replay engine is proven stable.

---

### 2026-04-30: Macros UX Surface

**Author:** Rusty (UX Architect)  
**Status:** Proposed — awaiting team review.

**Decision:**  
The Macros extension UX surface for v1:

1. **Toolbar** — `.vsct` `type="Toolbar"`, `DefaultDocked`, with Record, Stop, Play Last, Save As, and Open Tool Window buttons. Surfaces via View → Toolbars → Macros.
2. **Hotkeys** — `Ctrl+Shift+R` (Record/Stop toggle) and `Ctrl+Shift+P` (Play Last), bound globally via `guidVSStd97`. Minor conflicts with `View.RefreshRemoteReferences` and `Edit.PasteSpecial` accepted for classic macro muscle memory.
3. **Tool window** — `BaseToolWindow<MacrosToolWindow>` (Community Toolkit async-init) with search/filter box, `ListView` of saved macros (Name, Steps, Last Modified), bottom action bar (Play / Rename / Delete), and empty-state message.
4. **Context menu** — VSCT `type="Context"` menu shown via `IVsUIShell.ShowContextMenu` on right-click in ListView. Contains Play, Rename, Delete.
5. **Command visibility** — Record/Stop toggle via two UIContexts (`guidMacrosRecordingContext` / `guidMacrosIdleContext`) set through `IVsMonitorSelection.SetCmdUIContext`.

**Rationale:**  
Classic hotkeys preserve muscle memory. `BaseToolWindow` avoids UI thread blocking. VSCT context menus match VS theming. Two UIContexts simplify `.vsct` VisibilityConstraints.

---

### 2026-04-30: v1 Test Pyramid & Release Gating Strategy

**Decided by:** Linus (Test Engineer)  
**Status:** Proposed for team review.

**Decision:**  
Three-tier test pyramid with manual smoke checklist as v1 release gate.

**Tiers:**

1. **Unit Tests (xUnit)** — Base; macro serialization, anchor resolution, file storage, state machine. Gate: Block all PRs if any fail. Run: Every push.
2. **Integration Tests (Microsoft.VisualStudio.Sdk.TestFramework)** — Middle; recording + replay against mocked ITextBuffer / IVsCommandTarget. Gate: Non-blocking on PR; blocks merge to `main` and release branches. Run: Nightly + release branches.
3. **Manual Smoke Checklist (Scripted)** — Top; 12-point checklist (install, record, replay, save, delete, restart, uninstall). Gate: Blocking before every Marketplace publish. Duration: ~30 minutes on clean Windows VM.

**Anti-Flakiness Commitments:**
- No timing dependencies (no `Thread.Sleep()`)
- No real VS services (all mocked)
- Storage via temp folders
- Test isolation with teardown
- Deterministic assertions

**Risk Mitigation:**
- Anchor strategy: Golden-file regression tests; edge-case file suite
- Hotkey collisions: CI test on default VS config
- Recording-during-replay loop: Hard guard in code + unit test
- Corrupt macro loss: Backup to `.backup` folder
- Performance degradation: Replay timeout (10s); warning at > 500KB

---

## Archive

*None (decisions.md created on 2026-04-30; no archival required).*
