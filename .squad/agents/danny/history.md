# Danny — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30T21:37:32.793Z

## Learnings

<!-- Append learnings below -->

### 2026-05-01 — Beginner docs should explain the platform terms up front

For Macros docs, beginners get unstuck faster when `.csx`, `DTE`, `VS`, trigger types, and command-name discovery are explained before the API tables. External links should point to Roslyn scripting, EnvDTE2, Community.VisualStudio.Toolkit, and Visual Studio command docs so the local docs stay concise without losing accuracy.

### 2026-05-01 — IntelliSense Shim Dual-Write for Named Macros

**Root cause:** Named macros live in `<global-root>\Macros\<name>.csx` (one level deeper than the root). The codegen emits a constant relative `#load ".intellisense/Macros.Intellisense.csx"` for every macro regardless of depth. A shim only in `<global-root>\.intellisense\` is invisible to macros in the `Macros\` subfolder — they resolve relative to their own directory, which was missing its own `.intellisense\` sibling.

**Fix — dual write:** `RefreshGlobal()` now calls `IntelliSenseShimWriter.Write` twice: once for the global root (for `current.csx`) and once for `Path.Combine(root, FileSystemMacroStore.GlobalNamedSubfolder)` (for all named macros). Both writes are idempotent (SHA-256 hash guard in `IntelliSenseShimWriter.Write`). No per-file path arithmetic needed — the constant `#load` path is correct for every file as long as a shim sibling folder exists.

**Visibility:** `FileSystemMacroStore.GlobalNamedSubfolder` promoted from `private const` to `internal const` with a doc comment. `InternalsVisibleTo("Macros")` was already present in `Macros.Engine.csproj` — no new project changes required.

**Tests added (3):** `RefreshGlobal_WritesShimInBothRootAndNamedSubfolder`, `RefreshGlobal_NamedSubfolderShimMatchesRootShim`, `RefreshGlobal_IsIdempotent_BothLocations`. Full suite: 987 tests, all green.



**`IntelliSenseShimWriter.Deduplicate` pattern:** Extracted as `internal static IReadOnlyList<string> Deduplicate(IEnumerable<string> paths)` — pure, case-insensitive (`StringComparer.OrdinalIgnoreCase`), order-preserving (first occurrence wins). `ResolveAssemblyPaths` collects raw paths via `TryAdd` (which handles null/empty + diagnostic logging) then calls `Deduplicate`. VS18 Preview: `typeof(DTE).Assembly.Location` and `typeof(DTE2).Assembly.Location` both resolve to `Microsoft.VisualStudio.Interop.dll` — without dedup, the shim emits the same `#r` twice and Roslyn rejects it.

**Migrator wiring in `IntelliSenseShimRefresher`:** Added `using Macros.Engine.Storage;` and called `MacroFileLoadDirectiveMigrator.Migrate(root)` immediately after `IntelliSenseShimWriter.Write(root)` in both `RefreshGlobal()` and `RefreshRepo()`. Errors are caught by the same `when (!IsCritical(ex))` filter and routed to `_onError`. Migration is idempotent — calling twice is safe.

**Migrator wiring in `MacrosPackage`:** Added dedicated `// 2a-ter` block after `_shimRefresher.RefreshGlobal()` + `RefreshRepo()` calls. Resolves `globalRoot` via `MacrosPaths.ResolveGlobalFolderOrFallback` and calls `Migrate` directly. Wrapped in `catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)` per codebase convention.

**Stub pattern for parallel work:** Created `MacroFileLoadDirectiveMigrator.cs` in `src/Macros.Engine/Storage/` as a no-op stub (returns `new MigrationResult(0, 0, 0, Array.Empty<string>())`). Rusty will replace with real implementation. Stub enables build + test green before Rusty finishes.

**Test pattern — pure helper testing:** `Deduplicate` is `internal` and tested directly with synthetic input in `IntelliSenseShimWriterTests`. No need to intercept `typeof(T).Assembly.Location` — the helper is pure and takes `IEnumerable<string>`. Three tests: dedupe when sharing paths, ordering preservation `[a,b,a,c]→[a,b,c]`, case-insensitivity `[C:\Foo.dll, c:\foo.dll]→[C:\Foo.dll]`.

**RefresherTest pattern — migration smoke:** Added `RefreshGlobal_WithPreExistingCsxFile_WritesShimAndRunsMigration` — creates a fixture `.csx` before `RefreshGlobal()`, verifies shim is written and fixture file is not corrupted/deleted. Passes with no-op stub and will pass with real implementation.



**`IntelliSenseShimRefresher` pattern:** A pure C# disposable class with no VS shell dependencies. Takes two `Func<>` providers (global folder, repo folder) and an optional `Action<string, Exception>` error callback. `RefreshGlobal()` / `RefreshRepo()` are each independently callable and swallow non-critical exceptions via the callback so a shim failure never crashes package init. `AttachToTracker(SolutionContextTracker)` subscribes to `SolutionChanged`; `Dispose()` unsubscribes. Second `AttachToTracker` call throws `InvalidOperationException`.

**Nullable safety gotcha:** `_repoFolderProvider` returns `string?` but `IntelliSenseShimWriter.Write` takes `string`. After the `IsNullOrWhiteSpace` guard, the compiler still requires `root!` (null-forgiving operator) to satisfy nullable analysis — the guard is not seen as a null-check proof for the return type of a `Func<string?>`.

**Wire-up location in `MacrosPackage`:** Inserted between `SolutionContextTracker.Current = _solutionTracker;` (line ~246) and the `TrustGateInfoBar.InitializeAsync` call. The refresher must attach to the tracker BEFORE calling `RefreshRepo()` so the subscription is in place; the initial `RefreshGlobal()` + `RefreshRepo()` calls cover both the no-solution and with-solution cold-start cases.

**Dispose order in `MacrosPackage`:** `_shimRefresher?.Dispose()` before `_solutionTracker?.Dispose()` — the refresher must unsubscribe from the tracker event before the tracker is torn down.

**Tests:** 8 tests in `tests/Macros.Tests/Lifecycle/IntelliSenseShimRefresherTests.cs`. Key patterns: `IDisposable` test class with `Path.GetTempPath()` scratch root cleaned up in `Dispose()`; `SolutionContextTracker.CreateForTests()` + `ApplySolutionPath()` to simulate solution open; file-at-shim-folder-path to make `Directory.CreateDirectory` throw for the error-callback test.

### 2026-05-01 — IntelliSense Shim Generator + Writer

**Shim format:** `IntelliSenseShim.Generate(IReadOnlyList<string> assemblyPaths)` emits:
1. Fixed 5-line header block starting with `// Macros — IntelliSense Shim`.
2. One `#r "path"` directive per supplied assembly path (in caller-supplied order).
3. `using` + `using static` directives mirroring `CSharpCodeGenerator.EmitUsings` plus `using Macros.Engine.Triggers;`.
4. Three editor-only global stubs: `EnvDTE80.DTE2 DTE = null!;`, `Macros.Engine.Scripting.IMacroContext Context = null!;`, `Macros.Engine.Triggers.IMacroTrigger Trigger = null!;`.

**`#r` syntax:** Roslyn's scripting `#r` parser does NOT accept C# verbatim `@"..."` literals. Paths must be emitted as `#r "C:\path\to\file.dll"` — the content between the quotes is treated as a raw filesystem path, not a C# string (no escape processing). Backslashes in paths are literal.

**Atomic write pattern:** `IntelliSenseShimWriter.Write(storeRoot)` uses SHA-256 hash comparison for the no-op guard, then `tmp + File.Replace/File.Move` for the atomic rename (mirrors `FileSystemMacroStore.SwapIntoPlace`). The `.intellisense/` folder is created with `Directory.CreateDirectory` before the write.

**Assembly discovery:** Five assemblies resolved via `typeof(T).Assembly.Location`: EnvDTE (`DTE`), EnvDTE80 (`DTE2`), Shell.15.0 (`Package`), Community.VisualStudio.Toolkit (`VS`), Macros.Engine (`MacroGlobals`). Empty locations are skipped with `Debug.WriteLine`.

**Files:** `src/Macros.Engine/Scripting/IntelliSenseShim.cs`, `src/Macros.Engine/Scripting/IntelliSenseShimWriter.cs` (also contains `IntelliSenseShimWriteResult` record). Tests: 32 tests across `IntelliSenseShimTests.cs` + `IntelliSenseShimWriterTests.cs`.

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

### 2026-05-01 — Wake Repo Watcher + Trigger Registry on Solution Open

**Bugs fixed:** Two related lifecycle bugs where VS starting with no solution open left both the repo file-system watcher and the trigger registry stale after a solution was later opened.

**Root cause — watcher:** `FileSystemMacroStore.EnsureWatchersStarted()` is called once (at `LibraryChanged` first-subscription during package init). If no solution is open at that moment, the repo folder doesn't exist, the watcher is skipped, and `_repoWatcher` stays null forever. No subsequent code path re-invoked it.

**Root cause — trigger registry:** `MacroTriggerRegistry` only refreshes on `LibraryChanged` (incremental) or an explicit `RefreshAsync()`. Its initial `RefreshAsync` ran against an empty repo. Without a running watcher, `LibraryChanged` never fires for pre-existing files, so repo macro @trigger directives stay dead.

**Fix — three files:**
1. `FileSystemMacroStore.EnsureWatchersStarted()` promoted from `private` to `internal` — no logic change.
2. `RepoMacroStore.NotifySolutionChanged()` added as a public delegation seam: `public void NotifySolutionChanged() => _inner.EnsureWatchersStarted();`
3. `MacrosPackage.InitializeAsync`: after `_triggerRegistry` construction (after `_shimRefresher.AttachToTracker`), wired a `SolutionChanged` handler that (a) calls `_repoMacroStore?.NotifySolutionChanged()` and (b) fire-and-forgets `_triggerRegistry.RefreshAsync()`.

**Ordering is critical:** `_shimRefresher` (subscribed first) creates the repo folder via `Directory.CreateDirectory`. The new handler (subscribed after) then finds the folder and starts the watcher. Never reorder these subscriptions.

**Tests added (3):**
- `RepoMacroStoreTests.NotifySolutionChanged_StartsWatcher_AfterFolderCreated` — start with null provider → create folder → call `NotifySolutionChanged()` → drop a `.csx` → assert `LibraryChanged` fires.
- `RepoMacroStoreTests.NotifySolutionChanged_IsIdempotent` — call twice, no exception, watcher still functional.
- `MacroTriggerRegistryTests.RefreshAsync_AfterSolutionOpen_PicksUpPreexistingRepoTriggers` — empty store → `RefreshAsync` → add repo-scoped trigger entry → explicit `RefreshAsync` again → assert registry picks up the binding.

Full suite: 1003 tests, all green.

**Known deferred scope (m5-watcher-restart-on-solution-change):** If the watcher is already running for Solution A, opening Solution B does NOT restart it on B's repo folder. `EnsureWatchersStarted` checks `_repoWatcher == null` — it won't re-bind to a different path. Fixing that requires disposing the stale watcher first. This is a follow-up task.



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

### 2026-05-01 — global:: prefix in IntelliSense shim to fix EnvDTE.Macros collision

**Problem:** `using EnvDTE;` in the generated shim brings `EnvDTE.Macros` (a deprecated VBA-macros type from the old VS macro API) into scope. The C# resolver, walking using-imports before the global namespace, resolved `Macros.Engine.Scripting.IMacroContext` and `Macros.Engine.Triggers.IMacroTrigger` to `EnvDTE.Macros.*`, producing deprecation warnings in the editor.

**Fix:** Prefixed the two declaration lines in `EmitGlobalStubs` with `global::` so the resolver skips using-imports and anchors directly at the root namespace. Added a comment block above the stubs explaining why, to prevent future "cleanup" removing the prefix.

**`using` directives:** Left without `global::` — per C# spec, `using` directives are resolved against the global namespace and do not shadow-resolve through using-imports. Only declaration-site references need the prefix.

**Tests:** Updated `Generate_ContainsContextStub` and `Generate_ContainsTriggerStub` to assert the `global::`-prefixed form. Added `Generate_GlobalStubsUseGlobalNamespacePrefix_ToAvoidEnvDTEMacrosClash` to guard against future cleanup. Parse sanity test (`Generate_ParseSanityCheck_NoSyntaxErrors`) confirmed `global::` is valid in Script `SourceCodeKind`.

**Suite:** 1012 tests, all green.

### 2026-05-01 — Drop unresolved commands from generated macros

**Problem:** When `CommandObserver.ResolveCommandName` failed to map a GUID/ID pair to a DTE-friendly name, `CSharpCodeGenerator.EmitCommandStep` fell back to emitting `await RunCommandAsync(new System.Guid("…"), Nu)` — unreadable and non-replayable noise that confused users.

**Fix — IsEmittable predicate + pre-filter in Generate:** Added `private static bool IsEmittable(RecordedStep)`. For `CommandStep`: returns `true` only when `Name` is non-null, non-whitespace, and matches `DteNameRegex`. All other step types return `true`. `Generate()` builds an `emittable` list by filtering all input steps through `IsEmittable`, iterates `emittable` for emission, and passes `emittable.Count` to the `// Steps: N` header.

**Simplified EmitCommandStep:** The GUID/ID fallback branch is gone. Only the clean `await ExecuteCommandAsync("Name.Here");` emission path remains. If called with an un-emittable command (impossible post-filter), it throws `InvalidOperationException`.

**Tests:** Rewrote two tests that previously asserted GUID/ID fallback; added 5 new tests covering: null name drop, unparseable name drop, valid name kept, step renumbering after drops, all-unemittable produces empty body. Updated golden file (`sample.csx`): Steps 5→4; the unnamed CommandStep is dropped; former step 5 (deletion) renumbered to step 4.

**Policy:** The recorder still captures everything. The codegen is the single filtering point. Un-emittable steps vanish silently; the `// Steps: N` header reflects what was emitted.

**Suite:** 1000 tests, all green.

