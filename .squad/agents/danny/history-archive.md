# Danny — History Archive (Summarized 2026-05-02)

## Core Context
- **Project:** Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30

## Key Architectural Decisions (Locked)

### Roslyn C# Script Architecture (Canonical)
- Macros stored as `.csx` files in `%APPDATA%\Macros\`
- Playback via `CSharpScript.RunAsync` with `DTE` and `VS.` globals
- Compilation cached by SHA256 in `ScriptCompilationCache`
- Recording observers guarded by `MacroState.IsReplaying` (thread-local flag)
- Tech stack: Community Toolkit 17, Roslyn C# Scripting, EnvDTE/EnvDTE80

### v2 Trigger Architecture (Locked for M4)
- VS.Events from Community Toolkit as canonical event surface (lazy subscription, ref-counted teardown)
- BeforeCommand/AfterCommand triggers via `IVsRegisterPriorityCommandTarget`
- BeforeCommand can cancel via `Trigger.CancelCommand()`
- Depth cap ≥3 + per-event/per-command suppression to prevent infinite loops
- CompositeMacroStore (Global + Repo, repo-wins) with per-solution trust allowlist
- Auto-disable on 3 consecutive failures; resets on success

## Implementation Learnings

### Documentation
- Beginners need platform terms (`.csx`, `DTE`, `VS`, triggers, command discovery) explained upfront
- External links to Roslyn, EnvDTE2, Community.VisualStudio.Toolkit, VS command docs keep local docs concise

### IntelliSense Shim Dual-Write
- Named macros in `Macros/` subfolder need sibling `.intellisense/` shim
- `RefreshGlobal()` writes to both global root and `Path.Combine(root, GlobalNamedSubfolder)`
- Both writes idempotent (SHA-256 hash guard)
- `#load` path constant works for all files when sibling shim exists

### IntelliSense Shim Generator
- Emits 5-line header, `#r` directives (no C# escape processing), `using` blocks, 3 global stubs
- `#r` syntax: raw filesystem paths between quotes; backslashes literal (no verbatim)
- Atomic write via tmp + File.Replace; `.intellisense/` folder auto-created
- Deduplicate helper: `IReadOnlyList<string> Deduplicate(paths)` — case-insensitive, order-preserving

### global:: Prefix Fix
- `using EnvDTE;` brings deprecated `EnvDTE.Macros` into scope, shadowing generated namespaces
- Solution: prefix `Context` and `Trigger` stubs with `global::` to bypass using-imports

### Drop Unresolved Commands
- Unresolved commands emit as `RunCommandAsync(Guid, Id)` — unreadable, non-replayable
- New policy: codegen is single filter point; `IsEmittable()` pre-filters all steps
- Un-emittable commands dropped silently; `// Steps: N` reflects emitted count only

### Wake Repo Watcher + Trigger Registry
- **Bug:** Repo watcher and trigger registry stale if no solution open at package init
- **Fix:** Promote `EnsureWatchersStarted()` to `internal`, add `RepoMacroStore.NotifySolutionChanged()` seam, wire `SolutionChanged` handler in `MacrosPackage`
- Ordering critical: `_shimRefresher` creates repo folder, then new handler starts watcher
- Known deferral: watcher not restarted when solution changes (m5-watcher-restart-on-solution-change)

## Test Coverage
- Total suite: 1,014 tests (all green after latest changes)
- Key test patterns:
  - Pure helper testing (e.g., `Deduplicate` with synthetic input)
  - Fixture lifecycle tests (watcher, trigger registry refresh)
  - Error callback + non-critical exception swallowing
  - Nullable analysis (null-forgiving `!` operator usage)

## Status
✓ Architecture locked for M1-M4 pipeline
✓ No further pivots expected
✓ All subsequent work builds on CSX foundation
