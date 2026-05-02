# Rusty — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30T21:37:32.796Z

## Learnings

### 2026-04-30 — UX Architecture pass

**Locked decisions:** Toolbar + hotkeys (Ctrl+Shift+R/P) + tool window ListView + VSCT context menus + UIContexts. BaseCommand<T> + BaseOptionModel<MacrosOptions> + DynamicResource VS theming.

---

### 2026-04-30 — C# Scripting Pivot

Storage switched from JSON to .csx via Roslyn. UX surface unchanged (toolbar/hotkeys/tool window/context menus); underlying storage now C# scripts. Users can hand-edit with IntelliSense.

---

### 2026-04-30 — Triggers & Scope UX

**Tool window:** CollectionViewSource grouped (Repo/Global); shadowed globals via strikethrough + tooltip. Triggers column (event summary + tooltip). **Manage Triggers DialogWindow:** ListBox of triggers + Add panel (ComboBox + filter TextBox). Atomic .csx rewrite on OK. **Per-solution trust:** InfoBar [Review]/[Allow]/[Block] on first solution open. **Other surfaces:** Save As scope picker (Radio), status bar triggered-exec message, tool window refresh button + repo empty state.

**Locked for M4 Triggers/Scope** per coordinator + Rusty-triggers-and-scope.md decision.

---

### 2026-05-01 — Wave 1: IntelliSense shim (#load + resolver skip)

**Codegen (CSharpCodeGenerator):** Emits #load ".intellisense/Macros.Intellisense.csx" before #r directives (new const IntelliSenseLoadDirective).

**Player (MacroPlayer):** SkipIntelliSenseShimSourceResolver matches shim filename (case-insensitive), returns empty stream. Delegates other #load paths to SourceFileResolver.

**Tests:** 5 new (shim absent, real globals, non-shim missing, prefix path, mixed case). 25 total green. Golden file updated.

---

### 2026-05-01 — Wave 3: docs refresh

docs/csx-reference.md, docs/architecture.md, docs/ship-readiness-v1.0.0.md, docs/manual-smoke.md updated. IntelliSense feature marked shipped (no longer v1.1 backlog).

---

### 2026-05-01 — Wave 4: migrator + recursion fix

**Migrator (MacroFileLoadDirectiveMigrator):** Scans *.csx files; injects 6-line shim block before first non-comment line if absent. Idempotent. Preserves CRLF/LF line endings. Atomic write via .tmp + File.Replace. Per-file error handling.

**Recursion fix:** Switched SearchOption.TopDirectoryOnly → AllDirectories. Added IsInIntelliSenseFolder (segment-based exclusion; prevents false skip of my.intellisense.csx). .intellisense\ files excluded before scanned++ counter.

**Tests:** 7 new + 1 replaced. Global named macros layout verified (root + Macros\ both migrated). .intellisense case-insensitive exclusion verified. Trigger directive header handling verified.

**Result:** 984 tests pass (979 baseline). Migrator finds all named macros in global store. Release build clean. VSIX ready (5:42 PM 5/1/2026).

---

### 2026-05-01 — Open-on-stop: auto-open recorded .csx after Stop Recording

**Feature:** When the user clicks Stop Recording (Ctrl+Shift+R or the status-bar indicator), the just-saved `.csx` file automatically opens in the VS editor.

**Design:** Option B — event-based (`RecordingSaved`). Added `event EventHandler<RecordingSavedEventArgs>? RecordingSaved` to `IMacroService` / `MacroService`. The event fires inside the fire-and-forget block after `SaveAsAsync` completes, carrying the absolute path from `storage.GetMacroPath(name, MacroScope.Global)`. `StopCommand.ExecuteAsync` and `RecordingStatusBarInjector` subscribe before calling `StopRecordingAsync`, await a `TaskCompletionSource` (5 s timeout), then call `VS.Documents.OpenAsync`. Five fake `IMacroService` test doubles updated with the no-op event stub. Two new tests in `MacroServiceStorageTests`: path assertion and sequencing (SaveAs before event).

**Result:** 991 non-Performance tests pass (2 new). Release build clean.


---

### 2026-05-01 — Wave 5: slim macro file header

**Codegen (CSharpCodeGenerator):** Removed `// Source format:` line and trailing `//` separator from `EmitHeader`. Collapsed 4-line verbose shim comment to a single canonical line in `EmitReferenceDirectives`. Removed `EmitUsings` method and its call from `Generate` entirely — the shim's `#load` brings standard namespaces into scope for IntelliSense, and `MacroPlayer.ScriptOptions.WithImports` covers runtime. XML doc updated: point (3) now documents step emission only (using block gone).

**Migrator (MacroFileLoadDirectiveMigrator):** Added `StripSourceFormatLine`, `ReplaceVerboseShimComment`, `StripStandardUsings` (with `CollapseBlankLines` + `JoinLines` helpers). Strip order: (a) obsolete `#r EnvDTE`, (b) `// Source format:` line, (c) 4-line→1-line shim comment, (d) 8 exact standard usings (conservative: user-added usings preserved), (e) inject `#load`. `ShimBlock` updated to 3-element array (1-line comment + `#load` + blank). Skip condition widened: file skipped only when shim present and none of the 5 obsolete patterns detected.

**Tests:** 8 new tests (3 codegen + 5 migrator). Updated 4 existing tests (`Generate_EmptySteps`, `Generate_AllUnemittable`, `Migrate_FileWithExistingLoadDirective_LeavesUnchanged`, `Migrate_RoundTripIsRoslynParseable`). Golden file updated. 1011 tests green.

**Key design decisions:** Standard using stripping is conservative — exact line match only (after TrimEnd). A user-added `using System.IO;` or `using MyCompany;` is never touched. Idempotency verified: second migration pass on a fully-slimmed file is a no-op (Skipped=1).


**Migrator (MacroFileLoadDirectiveMigrator):** Added `HasObsoleteEnvDTEDirectives` and `StripObsoleteEnvDTEDirectives` (internal for tests). Strip logic: finds consecutive `#r "EnvDTE"` + `#r "EnvDTE80"` lines; if the two preceding lines are both `//` comments and one mentions `#r directives`, removes all 4 (comment + #r pair); otherwise strips only the 2 `#r` lines (defensive — handles `// @trigger` intervening). Collapses double blank lines post-strip. `Migrate` now strips first, injects shim after (so `FindBodyStart` lands on `using` block not `#r` lines). Skip condition narrowed: only skips when shim present AND no obsolete `#r`.

**Tests:** Updated tests 4 and 16 (stale `rPos < loadPos` assertion replaced with `DoesNotContain`). 6 new tests: PatternA strip, PatternB strip, trigger-intervenes strip, user-added #r preserved, idempotency of strip, exact-match guard. Golden file (`sample.csx`) updated. 993 tests green.

---

### 2026-05-01 — Lifecycle gap: refresh global shim on every solution-open

**Gap closed:** `OnSolutionChanged` previously called only `RefreshRepo()`. Two scenarios were left unaddressed: (1) if the user deleted the global shim file mid-session it stayed gone until VS restart; (2) after a shim-shape upgrade (e.g., the `global::` prefix fix) the global shim was only updated on the next full VS restart, not on the next solution open.

**Fix:** `OnSolutionChanged` now calls both `RefreshGlobal()` and `RefreshRepo()`. The SHA-256 skip inside `IntelliSenseShimWriter.Write` makes the extra call effectively free when nothing has changed.

**Tests:** 2 new tests added to `IntelliSenseShimRefresherTests`: `OnSolutionChanged_RefreshesBothGlobalAndRepoShims` and `OnSolutionChanged_RecreatesDeletedGlobalShim`. Total test count: 1014 (all green).

