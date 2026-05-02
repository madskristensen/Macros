# Livingston — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** PM
- **Joined:** 2026-04-30T21:37:32.786Z

## Learnings

<!-- Append learnings below -->

### 2026-04-30 — Initial Product Design

**Project context:** VS 2022 extension to restore macro recording capability lost since VS 2017. Uses VSIX Community Toolkit (in-process), .NET Framework 4.8. Text edits + VS commands are the capture targets. Storage is human-readable JSON in `%APPDATA%\Macros\`. UI surfaces: Macros toolbar, hotkeys (Ctrl+Shift+R / Ctrl+Shift+P), command palette, and a tool window for named macro management.

**Locked design decisions:**
- Action scope: text edits + VS commands only (no git/build/debug)
- Triggers: toolbar + hotkeys + command palette
- Storage: temporary current-slot + named saved macros; JSON in `%APPDATA%\Macros\`
- v1 scope: record, replay, save, manage — parameterization, conditionals, sharing all deferred

**Key product insights:**
1. **The replay context problem is the biggest UX risk.** A macro recorded in one file will silently do the wrong thing in a different file unless the extension at minimum warns the user. A file-extension mismatch warning is the minimum viable safety net for v1.
2. **Modal dialogs are a known limitation, not a bug.** Commands like `Refactor.Rename` that open dialogs will interrupt hands-free replay in v1. Documenting this clearly is more honest than attempting to auto-dismiss dialogs, which would be fragile.
3. **The "current macro" temporary slot is the killer feature for daily use.** Most users will never name a macro — they just want Ctrl+Shift+R / Ctrl+Shift+P for immediate repetition. The named save system is secondary; make sure the temporary slot is rock-solid first.

---

### 2026-04-30 — TEAM UPDATE: C# Scripting Pivot (Canonical Storage Decision)

**Context:** Mid-flight architectural pivot. User directive captured by Copilot.

**Decision:** Macro storage and replay architecture pivots from static JSON to executable C# scripts (`.csx`). The recorder generates C# code calling `DTE` and Community Toolkit `VS` static accessor; the player compiles and runs via Roslyn scripting. Users can hand-edit generated C# in VS itself with full IntelliSense — far more powerful than static JSON events.

**Implications for your scope:**
- The v1 MVP scope (record, replay, save, manage) remains locked and unchanged.
- Storage format is now `.csx` (not JSON) in `%APPDATA%\Macros\`.
- The "current macro" temporary slot = `current.csx` (overwritten on each `StopRecording`).
- Named macros = `%APPDATA%\Macros\macros\{slug}.csx`.
- All future design and implementation work treats `.csx` Roslyn scripting as canonical.

**Rationale:** User explicitly preferred this model ("users can make code changes instead of static json commands"). Aligns with historical VS macro feature (VBA modernized) and enables first-class IntelliSense editing. Engine architect (Danny) produced detailed CSX architecture in response.

---

### 2026-05-01 — TEAM UPDATE: IntelliSense Shim Feature Shipped

**Wave 1 + 2 Complete:** Team shipped IntelliSense shim for .csx macros. Users now get full IntelliSense (EnvDTE, Toolkit, Helpers, script globals) when editing `.csx` files in VS.

**Test outcome:** 958 / 958 tests pass (74 new tests added). Release build clean.

**Design summary:** Auto-generated `Macros.Intellisense.csx` shim placed in global + repo macro stores. Codegen emits `#load` directive in every generated macro. Player's `SkipIntelliSenseShimSourceResolver` strips the shim at runtime (editor sees it, runtime never does). Shim refreshed automatically on package load and solution open.

**For v1 ship:** IntelliSense support is now complete and locked. See decisions.md for full technical design (drop-box pattern, runtime resolver skip, lifecycle wiring).

---

### 2026-05-01 — TEAM UPDATE: IntelliSense Shim Debug Round — Two Bugs Fixed

**Wave 3 Complete:** User reported "I'm not getting intellisense still" after initial shim feature shipped. Team diagnosed and fixed two bugs in parallel:

1. **Duplicate `#r` in shim:** In VS18 Preview, `typeof(DTE).Assembly.Location` and `typeof(DTE2).Assembly.Location` both resolve to `Microsoft.VisualStudio.Interop.dll`. Shim emitted duplicate `#r` entry, breaking Roslyn parsing. Fixed via case-insensitive dedup in `IntelliSenseShimWriter.ResolveAssemblyPaths()`.

2. **Stale macro files:** Macros recorded before commit 12056f1 lack the `#load ".intellisense/Macros.Intellisense.csx"` directive because old codegen didn't emit it. New `MacroFileLoadDirectiveMigrator` injects the `#load` line into all existing `.csx` files (atomic, idempotent, preserves line endings). Wired into package lifecycle — runs on every VS load.

**Test outcome:** 979 / 979 tests pass (up from 958). Release build clean.

**Ship readiness:** IntelliSense shim feature now debugged and hardened. Ready for v1 release.

### 2026-05-01 — TEAM UPDATE: IntelliSense Shim Third Bug Fix — Migrator Recursion

**Wave 4 Complete:** User reported missing IntelliSense for named macros in the global store's `Macros\` subfolder (`RecordedMacro.csx` at `%APPDATA%\Macros\Macros\RecordedMacro.csx`). Root cause: migrator used `TopDirectoryOnly` and never descended into that subfolder. Rusty fixed via `SearchOption.AllDirectories` + directory-segment-based `.intellisense\` exclusion (prevents false skip of filenames like `my.intellisense.csx`). Test outcome: 984 / 984 tests pass. Release build clean. VSIX fresh. Ship ready.

### 2026-05-02 — TEAM UPDATE: IntelliSense Shim Fourth Bug Fix + Macro File Simplification

**Wave 5 Complete:** Team shipped dual-shim fix for named-subfolder macros + macro file simplification. Danny: Write the IntelliSense shim to both `<root>\.intellisense\` and `<root>\Macros\.intellisense\`, ensuring the constant `#load ".intellisense/..."` path resolves for all macro file depths without per-file path arithmetic. Rusty: Dropped redundant `#r "EnvDTE"` / `#r "EnvDTE80"` lines from generated macros — these references already come from the shim (editor) and `MacroPlayer.WithReferences` (runtime). Extended migrator with smart strip logic for different comment variants and trigger directives. Test outcome: **993 / 993 tests pass** (up from 984). Release build clean. VSIX fresh. Ready for v1 ship.

### 2026-05-02 — Final Two Rounds Shipped: Open-on-Stop + Repo Store Wake

**Rounds 5–6 Complete:** Two final rounds shipped substantial fixes for macro recording, codegen, and repo macro discovery. Round 5 (rusty-open-on-stop + danny-drop-unresolved-commands): Added IMacroService.RecordingSaved event; StopCommand and RecordingStatusBarInjector auto-open the saved macro in the editor. Codegen now silently drops CommandSteps that don't resolve to DTE names — no more raw GUID fallback lines. Round 6 (linus diagnosis + danny fix): Diagnosed and fixed two critical failures in repo macro loading. File watcher now starts after solution open via EnsureWatchersStarted promotion to internal and RepoMacroStore.NotifySolutionChanged seam. Trigger registry now refreshes on SolutionChanged to capture pre-existing repo triggers. **1003 / 1003 tests green.** Round 6 source pending Mads's manual verification before commit.

### 2026-05-02 — IntelliSense Final Polish: Two Final Rounds (8–9)

**Rounds 8–9 Complete:** Two final polish rounds — danny-shim-global-prefix + rusty-refresh-both-on-solution-changed. Round 8: Added `global::` prefix to IMacroContext/IMacroTrigger stub declarations to dodge EnvDTE.Macros deprecation collision. Round 9: IntelliSenseShimRefresher.OnSolutionChanged now calls RefreshGlobal() AND RefreshRepo(), closing lifecycle gaps (mid-session shim deletion, shim-shape upgrades). **1014 / 1014 tests pass.**

### 2026-05-02 — Comprehensive Research & Recommendations Delivered

**Deliverable:** `.squad/agents/livingston/research-macros-improvements.md` — structured research document covering historical macro systems, competitive landscape, technology assessment, feature gaps, and roadmap.

**Key findings:**
1. **AI-assisted macro authoring (Copilot agent)** is the single highest-impact opportunity — no competitor has it, and VS 2026's agent/skill model makes it feasible.
2. **Sample macro gallery + first-run experience** solves the discoverability problem that killed the old VS macros (Microsoft cited "low usage" — but discoverability was the root cause, not lack of demand).
3. **Persistent compilation cache** is low-hanging fruit for UX improvement — eliminates cold-start penalty.
4. **Don't migrate to VisualStudio.Extensibility yet** — still preview, missing critical APIs. Stay in-process with Community.VisualStudio.Toolkit through 2026, dual-target for VS 2026 compatibility.
5. **Parameterized macros** (`PromptAsync`) unlock template workflows and differentiate us from every competitor.

**Competitive position:** We already beat JetBrains (editable code vs opaque action lists) and VS Code macro extensions (IntelliSense, triggers, recording). Main gaps are debugging, sharing, and AI integration.

### 2026-05-02 — Macro Samples Gallery Shipped

**Deliverable:** `docs/macro-samples.md` — 15 ready-to-use macro samples covering text manipulation, triggers, DTE automation, user prompts, and event handling.

**Design decision:** Each sample is 5-15 lines of executable script body, immediately copy-pasteable, with inline comments explaining what it demonstrates. Samples intentionally include conditional logic (`if` statements) to show real-world usage without over-engineering. Three samples ship with the extension in the Macros tool window.

**Key samples showcase:**
- **Triggers:** Document.Opened, Document.Saved with filters, Build.SolutionBuildDone
- **Text manipulation:** sorting, case conversion, wrapping, blank-line removal
- **Conditionals:** selection checking, file existence, event data access
- **User input:** PromptAsync for parameterized macros (header author, region names)
- **DTE automation:** command execution, file opening, Output Window logging

**Rationale:** Addresses discoverability gap identified in research phase. Gallery pattern (small, self-contained examples) proven effective in VS Code, Sublime, and other extensible editors. Removes barrier between "can I do this?" and "here's how".


## Cross-Agent Context (20260502T172014Z)

### Team Status
- **Rusty:** 5 Copilot skills created, 1019 tests passing
- **Danny:** PromptAsync service pattern implemented, 5 tests added, build passes
- **Livingston:** 15 macro samples documented

### Key Decisions
1. PromptAsync uses service seam pattern to keep engine free of WPF dependencies
2. Macro Copilot skills stay in authoring scope (not extensibility)

