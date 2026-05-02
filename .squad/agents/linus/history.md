# Linus — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Tester
- **Joined:** 2026-04-30T21:37:32.799Z

## Learnings

### v1 Test Strategy: Pyramid, Determinism, Release Gate (2026-04-30)
- **Test Pyramid chosen:** Unit tests (xUnit, pure C# — most volume) + Integration tests (Microsoft.VisualStudio.Sdk.TestFramework — medium volume) + Manual smoke (scripted checklist before Marketplace — few, but blocking release)
- **Anti-flakiness principles:** No timing dependencies (no Thread.Sleep); mock all VS services (never real IVsUIShell); storage tests use temp folders; assert on state, not side effects; test isolation is mandatory
- **Key risks identified:** Anchor strategy regressions (golden-file regression tests required); hotkey collisions (test on clean VM); recording-while-replaying loop (hard guard + unit test); corrupt JSON silent failures (backup + schema validation); performance degradation on large macros (10s timeout + stress testing)
- **Release gate:** Manual smoke checklist (12 items) is the v1 gate. No automated UI tests yet — VS automation is too fragile. Proves extension installs, records, replays, persists, deletes, and survives restarts
- **Performance SLAs:** < 5ms per edit, < 10ms per command, 100-step macro in < 2s, cold tool window < 500ms, 1MB max macro file
- **Coverage target:** ≥ 80% for core services (MacroRecorderService, MacroCommand, FileSystemMacroStorage); xUnit as primary framework

---

### 2026-04-30 — TEAM UPDATE: C# Scripting Pivot (Canonical Storage Decision)

**Context:** User directive captured by Copilot; mid-flight architectural pivot from JSON to C# scripting.

**Decision:** Engine architect pivoted to C# scripting (`.csx` files via Roslyn). Macro storage format is now `.csx` (not JSON) in `%APPDATA%\Macros\`.

**Implications for your test strategy:**
- **Storage testing impact:** Replace JSON serialization tests with `.csx` file I/O tests. Mock `CSharpScript.RunAsync` for deterministic playback tests; use `Microsoft.CodeAnalysis.CSharp.Scripting` in integration tests.
- **Corruption handling:** Update your backup strategy from "corrupt JSON detection" to "`csx` file parse failures" — same pattern, but now catching Roslyn compilation errors instead of JSON schema violations.
- **Performance SLAs unchanged:** < 5ms per edit still applies (recording), < 2s for 100-step replay still applies (compilation cache speed + playback).
- **Stress test update:** Cold tool window < 500ms now means "cold compilation + first run" — Roslyn compilation adds overhead, so verify this SLA is still achievable.

**Your role in implementation:**
Unit and integration tests remain core. The manual smoke checklist is your v1 release gate — it's unchanged. Test both the recorder (emitting valid C# code) and the player (Roslyn compilation + execution) deterministically.

---

### 2026-05-01 — TEAM UPDATE: IntelliSense Shim Feature Shipped

**Wave 1 + 2 Complete:** Team shipped IntelliSense shim for .csx macros. Users now get full IntelliSense (EnvDTE, Toolkit, Helpers, script globals) when editing `.csx` files in VS.

**Test outcome:** 958 / 958 tests pass (74 new tests added). Release build clean.

**Impl summary:** Auto-generated `Macros.Intellisense.csx` shim placed in global + repo `.intellisense/` folders. Codegen emits `#load` directive. Player's `SkipIntelliSenseShimSourceResolver` strips it at runtime (returns empty stream). Shim refreshed on package load + solution open via `IntelliSenseShimRefresher`.

**For your testing:** Smoke checklist now includes verifying IntelliSense appears when user opens any `.csx` macro in the editor. See decisions.md for full design (drop-box pattern, runtime resolver skip, lifecycle refresher).

---

### 2026-05-01 — BUG INVESTIGATION: Repo Macros Don't Load on Solution Open

**Context:** Mads reported repo macros not loading when a solution is opened. Investigated all five subsystems.

**Findings:**

| Mode | Status | Detail |
|------|--------|--------|
| A — File watcher starts on solution-open | 🔴 **BUG** | `EnsureWatchersStarted()` is private, called only on `LibraryChanged` subscription. When VS starts with no solution, the folder doesn't exist → watcher not started. Later, `SolutionChanged` fires but nothing calls `EnsureWatchersStarted()` again. Watcher stays null → `LibraryChanged` never fires for repo adds → tool window and trigger registry stay stale. |
| B — Trigger registry refresh on solution-open | 🔴 **BUG** | `MacroTriggerRegistry` only listens to `LibraryChanged`, not `SolutionChanged`. Initial `RefreshAsync` ran with empty repo. No further refresh happens for pre-existing repo macros when solution opens later. |
| C — Tool window list on solution-open | ✅ CLEAR | VM subscribes to `SolutionChanged` → `ScheduleReload` → `LoadAsync` → `ListAllAsync`. Pre-existing repo macros appear correctly (direct `Directory.EnumerateFiles`, no watcher needed). |
| D — Repo folder creation on solution-open | ✅ CLEAR | `IntelliSenseShimRefresher.RefreshRepo()` on `SolutionChanged` creates the folder via `Directory.CreateDirectory` inside `IntelliSenseShimWriter.Write`. |
| E — Watcher restart on solution switch | ⚠️ KNOWN/DEFERRED | Documented as `m5-watcher-restart-on-solution-change`. `EnsureWatchersStarted()` checks `_repoWatcher == null` — won't restart for a new solution if old watcher is still running. Out of scope for this fix. |

**Fix plan:**
1. Make `FileSystemMacroStore.EnsureWatchersStarted()` internal; add `RepoMacroStore.NotifySolutionChanged()`.
2. In `MacrosPackage.InitializeAsync`, after `_triggerRegistry` construction, subscribe to `SolutionChanged` → call `_repoMacroStore?.NotifySolutionChanged()` AND fire-and-forget `_triggerRegistry?.RefreshAsync()`.
3. Subscription order matters: must be after `_shimRefresher.AttachToTracker(...)` so folder is created first.

**Tests required:** 3 new unit tests (see decision-drop file). Manual smoke checklist additions for tool window + trigger binding on solution-open.

**Decision drop:** `.squad/decisions/inbox/linus-repo-load-on-solution-open.md`

---

### 2026-05-01 — BUG INVESTIGATION: IntelliSense Not Working for Mads

**Context:** Mads reported IntelliSense not resolving `DTE`, `Context`, `Trigger` globals after the shim feature shipped. Investigated all five failure modes.

**Findings:**

| Mode | Status | Detail |
|------|--------|--------|
| A — Shim not on disk | ✅ CLEAR | Shim exists at `%APPDATA%\Macros\.intellisense\Macros.Intellisense.csx` (1554 bytes, written 2026-05-01 16:28:36). `RefreshGlobal()` ran successfully. |
| B — Shim contents broken | ⚠️ SECONDARY BUG | Shim has **duplicate** `#r "…Microsoft.VisualStudio.Interop.dll"` — `typeof(DTE).Assembly.Location` and `typeof(DTE2).Assembly.Location` resolve to the **same DLL** in VS18 Preview. `ResolveAssemblyPaths()` has no deduplication. |
| C — Macro missing `#load` | 🔴 **PRIMARY CAUSE** | `current.csx` (written 12:35:25 PM) was generated by old codegen **before** the shim feature shipped. No `#load ".intellisense/Macros.Intellisense.csx"` line. Even though the shim exists, the editor never loads it. |
| D — VS Misc Files ignores `#load` | ❓ UNKNOWN | Can't test without interactive VS. Not blocking — Mode C must be fixed first to test D. |
| E — Stale experimental hive | ✅ CLEAR | Shim was written by new code at 4:28 PM (after macro at 12:35 PM), proving new code ran in the hive. |

**Bugs confirmed:**
1. **Stale macro files** (Mode C): Existing `.csx` files recorded before 12056f1 have no `#load` line. Only newly recorded macros get it.
2. **Duplicate `#r` in shim** (Mode B): `ResolveAssemblyPaths()` doesn't deduplicate; EnvDTE + EnvDTE80 both resolve to `Microsoft.VisualStudio.Interop.dll` in VS18 Preview.

**Required test additions:**
- **Migration smoke test:** After package init, all pre-existing `.csx` files in the global store should gain `#load` if they didn't have it (unit + integration test).
- **Shim dedup test:** Unit test that `IntelliSenseShimWriter.Write()` never emits two identical `#r` paths, even when `typeof(X).Assembly.Location` and `typeof(Y).Assembly.Location` return the same path.
- **Golden file regression:** Re-record `current.csx` after fix and verify it contains `#load` on the expected line.
