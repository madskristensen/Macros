# Danny — Macro Engine & Prompt Service

**Role:** Implementing helper utilities and service patterns for macro runtime

## Summary

Danny's work focuses on macro runtime helpers and service integration:
- Implemented PromptAsync service with IMacroPromptService seam pattern
- Added 5 unit tests for service validation
- Build passes with 1019 tests
- Service keeps Macros.Engine free of WPF dependencies

## Key Contribution
Service seam pattern allows engine code to remain reusable while enabling runtime UI prompts through VSIX implementation layer.

## Learnings

### Architecture decisions

- `using static` directives for `Helpers` and `VS` belong ONLY in the IntelliSense shim (`.intellisense/Macros.Intellisense.csx`), not emitted inline in generated macro files. `IntelliSenseShim.cs` already includes both `using static` lines.
- `CSharpCodeGenerator.EmitReferenceDirectives()` should only emit: comment line + `#load` + blank line. No `using static` lines.
- `MacroFileLoadDirectiveMigrator.StandardUsingLines` includes `using static` entries so old macro files that have them get stripped on migration.
- PDB/debug info emission (`WithEmitDebugInformation`, `WithOptimizationLevel(Debug)`, `WithFilePath`) was removed from `MacroPlayer.BuildScriptOptions()`. `BuildScriptOptions` takes no parameters now. `BuildCacheKey` simplified to return just `source`.
- `BreakIntoDebugger()` was a public API on `Helpers` — it has been removed. The public surface is exactly 8 verbs: TypeAsync, MoveCaretAsync, SelectAsync, ExecuteCommandAsync, RunCommandAsync, WaitAsync, OpenFileAsync, PromptAsync.
- Debug Macro VSCT entries: `cmdidMacrosCtxDebug` (0x2119) and the Button were in `MacrosContextMenuGroup1`. Both removed.
- `MacroEventBus` must cache toolkit event-category instances by `Type` and keep the resolved instance on each bus entry. That lets background-thread re-syncs reuse the UI-thread-created category object instead of re-running COM-backed property getters off the UI thread.
- `MacrosPackage` should pre-warm the event-category cache when constructing `MacroEventBus` on the UI thread, so later trigger-registry rebuilds can subscribe categories like `Build.SolutionBuildDone` from the file-watcher path without needing a JTF dependency in `Macros.Engine`.

### Key file paths
- `src/Macros.Engine/Codegen/CSharpCodeGenerator.cs` — pure transform, no I/O
- `src/Macros.Engine/Storage/MacroFileLoadDirectiveMigrator.cs` — one-time idempotent file migrator
- `src/Macros.Engine/Scripting/IntelliSenseShim.cs` — shim generator (has `using static`)
- `src/Macros.Engine/Player/MacroPlayer.cs` — script compilation + execution
- `src/Macros/Commands/Context/` — VSCT context-menu command handlers
- `src/Macros.Engine/Triggers/MacroEventBus.cs` — VS event subscription bridge; caches toolkit category instances for attach/detach
- `src/Macros/MacrosPackage.cs` — constructs `MacroEventBus` on the UI thread and can pre-warm event categories
- `tests/Macros.Tests/Triggers/MacroEventBusTests.cs` — pins category-instance caching and detach behavior
- `tests/Macros.Tests/Codegen/golden/sample.csx` — codegen snapshot golden file

### User preferences
- Mads wants debugging features removed cleanly — no PDB emission, no BreakIntoDebugger, no Debug Macro command
- `using static` goes in the shim only, not in individual macro files
- Mads wants VS event triggers fixed at the subscription layer so events like `Build.SolutionBuildDone` fire regardless of build success/failure semantics; the bug was in attaching the handler, not in event filtering.

### Tool window learnings
- The Samples gallery is more reliable when its header stays rendered even if the nested `SamplesGroup.IsVisible` binding misbehaves at tool-window startup; filtering can still empty the list content without hiding the section.
- Tool-window drag/drop should route through view-model methods: macro rows move between Repo and Global scopes, while sample rows copy by instantiating a template into the target scope folder.

## Recent Activity (2026-05-02T15:11:29-07:00)
- Removed debugging feature: DebugContextCommand, BreakIntoDebugger, PDB emission, VSCT entries, docs section
- Reverted `using static` inline emission from CSharpCodeGenerator
- Updated MacroFileLoadDirectiveMigrator to strip (not inject) `using static` from macro files
- All 1025 tests pass; pushed commit `3f39e6b`

