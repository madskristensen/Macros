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
