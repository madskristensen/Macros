# Danny — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30T21:37:32.793Z

## Learnings

<!-- Append learnings below -->

### 2026-04-30 — v2 Trigger Architecture (VS.Events + BeforeCommand/AfterCommand)

**Event surface:** `Community.VisualStudio.Toolkit.VS.Events` is the canonical trigger source — NOT a hand-curated list. `IMacroEventBus.KnownEvents` is populated at startup by reflecting over `VS.Events.*` subcategory types. Flattened naming convention: `{Category}.{HandlerName}` (e.g., `Build.SolutionBuildDone`, `Document.Saved`, `Debugger.EnterBreakMode`). Subscriptions are lazy and reference-counted per event name; no overhead unless a macro binds to that event.

**BeforeCommand/AfterCommand via priority command target:** `CommandObserver` (`IVsRegisterPriorityCommandTarget`) now serves three purposes: (1) recording capture (existing), (2) BeforeCommand dispatch before forwarding, (3) AfterCommand enqueue after forwarding. Command name resolution via `Dictionary<string,(Guid,uint)>` built from `DTE.Commands` at startup; reverse lookup via `DTE.Commands.Item` on cache miss. Cache refresh on `Shell.EnvironmentColorChanged` (v1 proxy; improve in v1.x).

**BeforeCommand can cancel via `Trigger.CancelCommand()`:** Sets cancel flag; `CommandObserver.Exec` returns `OLECMDERR_E_CANCELED` instead of forwarding to next target. Only valid for `BeforeCommand` triggers — throws `InvalidOperationException` otherwise. Fail-safe: exception or timeout (default 2s, `MacrosOptions.BeforeCommandTimeoutMs`) → log to Output, command proceeds regardless. Short-circuit: `HashSet<string> RegisteredBeforeCommandNames` / `RegisteredAfterCommandNames` on `IMacroTriggerRegistry` — O(1) per-command check; zero dispatch overhead when no macros are bound.

**Depth cap 3 + per-event/per-command suppression:** `[ThreadStatic] int _triggerDepth` on UI thread. At depth ≥ 3: drop run, log, single InfoBar. Per-event: `HashSet<string> _activeTriggers` in `MacroEventBus`. Per-command: `HashSet<string> _activeBefore` / `_activeAfter` in `CommandTriggerDispatcher`. Manual invocations do NOT increment depth.

**CompositeMacroStore (Global + Repo) with repo-wins:** `IMacroStore` (renamed from `IMacroStorage`) aggregates `GlobalMacroStore` (`%APPDATA%\Macros\macros\`) and `RepoMacroStore` (`<solutionDir>\.macros\`). Name collision → repo entry wins. `RepoMacroStore` rebinds on `Solution.OnAfterOpenSolution` / `Solution.OnBeforeCloseSolution` via the same `IMacroEventBus`. `MacroEntry` carries `MacroScope`, `IReadOnlyList<TriggerBinding>`, `StepCount`, `Modified`.

**Per-solution trust allowlist:** On first open of a solution with repo `.csx` files containing any `@trigger` directive (VS.Events, BeforeCommand, or AfterCommand): `IVsInfoBarUIFactory` InfoBar. InfoBar text calls out BeforeCommand count separately ("N macros, M of which can intercept VS commands"). Choice persisted in `MacrosOptions.TrustedSolutions` / `MacrosOptions.BlockedSolutions`. Blocked repos: triggers skip registration; manual invocation still allowed.

**Auto-disable on failure:** After 3 consecutive triggered-run failures, macro triggers are unregistered via `IMacroTriggerRegistry` and an InfoBar surfaces. Failure count resets on successful triggered run.

**Trigger queue:** BeforeCommand runs synchronously on UI thread (not queued). VS.Events triggers + AfterCommand enter `AsyncQueue<T>` (Microsoft.VisualStudio.Threading), drained serially on UI thread. Manual invocations share the same queue.

### 2026-04-30 — Roslyn C# Script Architecture

**Project context:** VS 2022 VSIX (SDK-style, .NET Framework 4.8) that records editor/command events and replays them. Community Toolkit 17 (in-process). Three-project solution: `Macros.csproj` (VSIX), `Macros.Engine.csproj` (recorder/codegen/player), `Macros.Tests.csproj`.

**Locked tech stack:**
- `Community.VisualStudio.Toolkit.17` + `Microsoft.VisualStudio.SDK` v17
- `Microsoft.CodeAnalysis.CSharp.Scripting` (Roslyn scripting host)
- `Microsoft.VisualStudio.Threading.Analyzers` (VSTHRD enforcement)
- `EnvDTE` / `EnvDTE80` for DTE globals surface

**Roslyn-CSX architecture chosen:**
- Macros stored as `.csx` files in `%APPDATA%\Macros\`. Current macro = `current.csx`; named macros = `macros/{slug}.csx`.
- Recorder: `IWpfTextViewCreationListener` (MEF) captures `ITextBuffer.Changed`; `IVsRegisterPriorityCommandTarget` captures commands. Both guard on `MacroState.IsReplaying` (thread-local flag) to ignore playback-generated events.
- Code generator: `CSharpCodeGenerator` + `StepAggregator` → idiomatic C# top-level statements. Helper vocab: `Type()`, `MoveCaret()`, `Select()`, `ExecuteCommand()`, `RunCommand()`.
- Player: `CSharpScript.RunAsync(source, options, globals)` with `MacroGlobals { DTE, VS, Context }`. Compilation cached in `ScriptCompilationCache` (ConcurrentDictionary keyed on SHA256 of source). Compilation happens on background thread; `RunAsync` on UI thread via `JoinableTaskFactory.SwitchToMainThreadAsync()`.
- Storage: `FileSystemMacroStorage` with atomic-write (`.tmp` rename) pattern.
- `MacroService` is a MEF singleton with Idle/Recording/Playing state machine; guards against nested record/play.

---

### 2026-04-30 — TEAM UPDATE: C# Scripting Pivot (Canonical Storage Decision) — ARCHITECTURE CONFIRMED

**Context:** User directive captured by Copilot; mid-flight architectural pivot from JSON to C# scripting.

**Decision:** YOU ARE THE ARCHITECTURE AUTHOR. Your CSX design is now team-locked as the canonical engine architecture. Macro storage format is `.csx` (Roslyn C# script files) stored in `%APPDATA%\Macros\`. Playback via `CSharpScript.RunAsync` with `DTE` and `VS.` injected as globals. Compilation cached by SHA256. Recording observers guarded by `MacroState.IsReplaying` (thread-local flag).

**What this means for your work:**
- All subsequent implementation work builds on your architecture — no further pivots.
- The storage format, playback mechanism, compilation caching, and code generation patterns are canonical.
- The code generator helper vocabulary (5 helpers covering ≥ 95% of actions) is the implementation contract.

**Implementation sequence for M1-M4:**
1. **M1:** Core Roslyn engine (basic script execution, compilation caching, state machine).
2. **M2:** Recording observer + code generator (capture text edits and commands; emit idiomatic C#).
3. **M3:** Playback (macro player, anchor resolution, error handling).
4. **M4:** UI (toolbar, tool window, context menus).

## Round 3 outcome

**Danny-3's v2 architecture (VS.Events + BeforeCommand/AfterCommand) is the locked engine spec for M4 Triggers/Scope.**

Details (see `.squad/decisions.md` and `danny-triggers-and-scope.md`):
- VS.Events from Community Toolkit as the full event surface (lazy subscription, reference-counted teardown)
- BeforeCommand/AfterCommand triggers via `IVsRegisterPriorityCommandTarget` reuse; BeforeCommand can cancel via `Trigger.CancelCommand()`
- Depth cap 3 + per-event/per-command suppression to prevent infinite loops
- CompositeMacroStore (Global + Repo, repo-wins) with per-solution trust allowlist (InfoBar prompt)
- Auto-disable on repeated failure (3 consecutive); failure count resets on success

