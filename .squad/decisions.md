# Squad Decisions

## 2026-04-30 22:15 — User directives: drop signing, add event triggers, add global/repo scope

**By:** Mads Kristensen (via Copilot)

**Directive 1 — drop code signing.** Remove VSIX signing from the v1 scope. Marketplace publish proceeds unsigned. Removes `m4-vsix-signing` todo and its dependency on `m4-publish`.

**Directive 2 — add event-triggered macros.** Macros can be auto-invoked when VS events fire (build completed, build failed, solution closed, etc.) in addition to manual hotkey/toolbar invocation. Significant new feature surface — gets its own milestone.

Engineering implications:
- `MacroGlobals` gains `Trigger` property (null for manual, set with event details when auto-triggered)
- `IMacroTriggerRegistry` tracks macro → trigger bindings
- `IMacroEventBus` subscribes to DTE/`IVsSolutionEvents`/`IVsUpdateSolutionEvents` and dispatches to bound macros
- Triggers declared as `// @trigger EventName` header comments in the .csx (canonical) AND editable via tool window UI (which rewrites the comments)
- Re-entrance guard per trigger source to prevent infinite loops (e.g., a macro that triggers on BuildSucceeded and invokes a build)
- Kill switch in Tools → Options → Macros for "disable all triggered macros"
- Status bar feedback: "Macros: BuildSucceeded → MyMacro"

UX implications:
- Tool window grows a Triggers column (or per-macro detail pane)
- Save As dialog gains a scope picker (Global vs Repo)
- "Manage Triggers" command on each macro

**Directive 3 — global + repo macro scopes.** Two storage scopes:
- **Global:** `%APPDATA%\Macros\macros\*.csx` (existing — follows the user)
- **Repo:** `<solutionDir>\.macros\*.csx` (new — version-controlled, shared with team)

Engineering implications:
- `IMacroStorage` becomes a `CompositeMacroStore` aggregating `GlobalMacroStorage` + `RepoMacroStorage`
- `RepoMacroStorage` watches the active solution; available only when a solution is open; rebinds on solution open/close
- Name collision rule: **repo wins** (more specific overrides global). Tool window shows the override visually (e.g., greyed-out global with strike-through).
- `Slugify(name)` collision still possible within a scope — uniqueness validation still applies, scoped per source.

Trust model for repo triggered macros (since they execute arbitrary code on auto-events from someone else's repo):
- On first solution open, if the repo contains any `.macros/*.csx` files with `@trigger` directives, show an `InfoBar` at the top of the editor: "This solution defines N auto-running macros. Review and allow?" with [Review] [Allow this solution] [Block] actions.
- Choice persisted per-solution-path in MacrosOptions (allowlist).
- If [Block]: repo macros are listed in the tool window but cannot auto-run; user can still invoke manually.
- If [Allow]: triggered macros register normally.
- Power users can disable the prompt globally in Tools → Options → Macros.

This addition is significant enough to warrant a new milestone and bump polish to M5.

## 2026-04-30 22:30 — User clarification: VS.Events surface + Before/AfterCommand triggers

**By:** Mads Kristensen (via Copilot)

**Refines the trigger directive (2026-04-30 22:15) with two additions:**

**1. Event surface = Community Toolkit's `VS.Events`.** Don't enumerate a hand-picked event list. Use the events Community Toolkit already exposes (SolutionEvents, BuildEvents, DocumentEvents, ShellEvents, DebuggerEvents, WindowEvents, etc.) so users get the full set "for free" and the engine doesn't have to maintain its own taxonomy. Curate a recommended starter set in Manage Triggers UI but allow the full surface.

**2. Add Before/AfterCommand triggers.** Macros can subscribe to any named VS command (e.g., `File.Open`, `Build.BuildSolution`, `Refactor.Rename`) with two trigger types:
- `// @trigger BeforeCommand File.Open` — runs BEFORE the command executes
- `// @trigger AfterCommand Build.BuildSolution` — runs AFTER (regardless of success/failure)

**Specifics:**
- **String-based command names only** (e.g., `File.Open`). No raw GUID/ID pairs. Resolve via `DTE.Commands.Item(name)` → cached name→(guid,id) map on startup.
- All existing VS commands are automatically supported via name lookup. No allowlist.
- BeforeCommand can cancel: `Trigger.CancelCommand()` is callable in BeforeCommand context only. If any macro bound to BeforeCommand cancels, the command does not execute. Document loudly — this is interception, not just observation.
- AfterCommand fires for both success and failure. `Trigger.Data["Success"]` (bool) differentiates.
- Engine plumbing: reuse the existing `IVsRegisterPriorityCommandTarget` observer. Now serves dual purpose: (a) capture commands during recording, (b) dispatch BeforeCommand/AfterCommand triggers always.
- Re-entrance protection: same per-event suppress + depth cap (3). A macro triggered by `BeforeCommand Build.BuildSolution` that itself runs a build will be cut off at depth 3.
- Trigger.Data payloads (initial set):
  - `BeforeCommand`: `{ CommandName: "File.Open", Args: "..." }`
  - `AfterCommand`: `{ CommandName: "File.Open", Args: "...", Success: true|false }`
  - VS.Events events expose their native EventArgs as `Data["Native"]` plus any normalized fields we add (e.g., `BuildSucceeded`: `{ ErrorCount, WarningCount, Configuration }`).

## 2026-04-30 — v1 trigger architecture (locked)

**By:** Danny (Engineer)

**Decision:** v1 trigger architecture is locked as follows:

- **VS.Events as canonical event surface.** `Community.VisualStudio.Toolkit.VS.Events` subcategories (`Build.*`, `Solution.*`, `Document.*`, `Shell.*`, `Debugger.*`, `Window.*`, `Selection.*`) are the full event surface. No hand-curated list. Flattened naming: `{Category}.{HandlerName}` (e.g., `Build.SolutionBuildDone`). `IMacroEventBus.KnownEvents` enumerates by reflection over `VS.Events.*` at startup. Lazy subscription — subscribe to a `VS.Events` handler exactly once when the first macro trigger references it; reference-counted unsubscription on teardown.

- **BeforeCommand/AfterCommand triggers via `IVsRegisterPriorityCommandTarget`.** Reuses the existing `CommandObserver` (priority command target). `BeforeCommand <name>` fires synchronously before `_nextTarget.Exec`; can cancel the command via `Trigger.CancelCommand()`. `AfterCommand <name>` fires after `_nextTarget.Exec` returns; enters the serial trigger queue. Command name resolution: `Dictionary<string,(Guid,uint)>` built by enumerating `DTE.Commands` at startup; reverse-lookup fallback via `DTE.Commands.Item(guid,id).Name` for cache misses.

- **BeforeCommand can cancel via `Trigger.CancelCommand()`.** Calling this in a `BeforeCommand` context sets a cancel flag; `CommandObserver.Exec` returns `OLECMDERR_E_CANCELED` instead of forwarding. Valid ONLY for `BeforeCommand` triggers — throws `InvalidOperationException` on all other trigger kinds. Fail-safe: if a `BeforeCommand` macro throws or times out (default 2s), the command proceeds regardless.

- **Depth cap 3.** `[ThreadStatic] int _triggerDepth` on the UI thread. At depth ≥ 3, further triggered runs are dropped and logged. Manual invocations do NOT increment depth. Per-event/per-command suppression prevents same-event recursion within a single run.

- **CompositeMacroStore (Global + Repo) with repo-wins.** `IMacroStore` aggregates `GlobalMacroStore` (`%APPDATA%\Macros\macros\`) and `RepoMacroStore` (`<solutionDir>\.macros\`). On name collision, repo entry takes precedence. `RepoMacroStore` rebinds on `Solution.OnAfterOpenSolution` / `Solution.OnBeforeCloseSolution`. `MacroEntry` carries `MacroScope`, `TriggerBindings`, `StepCount`, `Modified`.

- **Per-solution trust allowlist.** On first solution open with any repo `.csx` containing `@trigger` directives: `IVsInfoBarUIFactory` InfoBar. Count of `BeforeCommand` triggers called out explicitly. Trust choice (`Allow` / `Block`) persisted in `MacrosOptions.TrustedSolutions` / `MacrosOptions.BlockedSolutions` (canonicalized solution path). Blocked repos: macros listed in tool window, manual invocation still works, auto-triggers skipped.

- **Auto-disable on repeated failure.** After 3 consecutive triggered-run failures, macro triggers are unregistered and an InfoBar surfaces. Failure count resets on successful triggered run.

## 2026-04-30 — Trigger UX extension (locked)

**By:** Rusty (UX Engineer)

**Tool window groups by scope (Repo above Global), shadowing visible via strikethrough; Manage Triggers DialogWindow rewrites .csx `@trigger` header atomically; per-solution trust prompt via `IVsInfoBarUIFactory`.**

### Detail

1. **Grouped tool window.** The Macros ListView uses `CollectionViewSource` with `GroupDescriptions` to render two collapsible sections: "📁 Repo: SolutionName" (top) and "🌐 Global" (bottom). Global macros whose name is overridden by a same-name repo macro are shown greyed with strikethrough (`DataTrigger` on `IsShadowed` → `EnvironmentColors.SystemGrayTextBrushKey` + `TextDecorations.Strikethrough`) and a tooltip explaining the override.

2. **Triggers column.** A new "Triggers" `GridViewColumn` shows a comma-separated event summary ("BuildSucceeded, DocumentSaved" or "Manual"). Cell tooltip shows full filter details. A **Manage Triggers** context menu action (VSCT `cmdidMacrosManageTriggers`, `KnownMonikers.EventLog`) opens the dialog.

3. **Manage Triggers dialog.** `Microsoft.VisualStudio.PlatformUI.DialogWindow` subclass. Middle: `ListBox` of `TriggerDescriptor` rows each with a `KnownMonikers.Cancel` Remove button. Bottom: event `ComboBox` (from `IMacroEventBus.KnownEvents`) + optional filter `TextBox` with `key=value` validation. On OK: engine rewrites only the `// @trigger` header block in the `.csx` atomically (write to `.tmp`, then `File.Replace`). On Cancel: discard.

4. **Per-solution trust prompt.** On first open of a solution containing `.macros/*.csx` files with `@trigger` directives, `IVsInfoBarUIFactory.CreateInfoBar` shows an InfoBar (icon `KnownMonikers.StatusInformation`, text "This solution defines N auto-running macros. Review before allowing them to run on VS events.") with three actions: [Review] (opens `.macros` folder) / [Allow] (adds to `MacrosOptions.TrustedSolutions`) / [Block] (adds to `MacrosOptions.BlockedSolutions`). X = defer. Suppressed when `MacrosOptions.TrustNewSolutionsAutomatically == true`. Trust state managed via a dedicated "Trusted Solutions" sub-page under Tools → Options → Macros.

### Rationale

- Scope grouping makes multi-scope state immediately legible without adding a separate panel.
- Shadowing via strikethrough is the standard VS pattern for overridden/disabled items (consistent with reference-assembly shadowing in Solution Explorer).
- Atomic `.csx` rewrite protects against data loss if VS crashes mid-write.
- InfoBar trust prompt matches VS's existing pattern for untrusted content (NuGet package warnings, `.editorconfig` prompts) — three-button Review/Allow/Block keeps the surface minimal.

## 2026-04-30 22:45 UTC — User clarification: Use SDK-style csproj for the VSIX project

**By:** Mads Kristensen (via Copilot)

**Reference:** https://devblogs.microsoft.com/visualstudio/sdk-style-support-for-extension-projects/

The VSIX project (`Macros.csproj`) MUST use the SDK-style project format introduced for VS extensions. Headline characteristics from the announcement:

- Top of file is `<Project Sdk="Microsoft.NET.Sdk">` (NOT the legacy `<Project ToolsVersion="..." xmlns="...">` form).
- Auto file globbing — no need to list every `.cs` file individually.
- `PackageReference` for all NuGet dependencies (no `packages.config`).
- Implicit imports (no manual `Microsoft.Common.props` / `Microsoft.CSharp.targets` imports).
- Required properties:
  - `<TargetFramework>net48</TargetFramework>`
  - `<UseCodebase>true</UseCodebase>` (for VSIX assembly resolution at install time)
  - `<GeneratePkgDefFile>false</GeneratePkgDefFile>` unless explicitly needed
  - `<CreateVsixContainer>true</CreateVsixContainer>`
  - `<DeployExtension>true</DeployExtension>` (for F5 experimental hive deploy)
- Modern NuGet packages required: `Microsoft.VSSDK.BuildTools` 17.x, `Microsoft.VisualStudio.SDK` 17.x, `Community.VisualStudio.Toolkit.17`.
- `source.extension.vsixmanifest` stays at the project root and is referenced via `<None Include="source.extension.vsixmanifest"><SubType>Designer</SubType></None>`.

**Apply to:**
- `Macros.csproj` — VSIX shell — must be SDK-style.
- `Macros.Engine.csproj` — already SDK-style by default for class libs (no change required, but verify).
- `Macros.Tests.csproj`, `Macros.IntegrationTests.csproj` — SDK-style xUnit / VS SDK Test Framework projects.

A project-local skill (`converting-to-sdk-style-project`) exists with detailed guidance for the implementation phase.

## 2026-05-01 15:34:34 UTC — Decision: IntelliSense shim format and file-naming convention

**Date:** 2026-05-01T15:34:34.764-07:00  
**By:** Danny (Engineer)  
**Todos:** `shim-generator`, `shim-writer`

### Shim file identity

| Property | Value |
|----------|-------|
| Folder name constant | `IntelliSenseShimWriter.ShimFolderName = ".intellisense"` |
| File name constant | `IntelliSenseShimWriter.ShimFileName = "Macros.Intellisense.csx"` |
| Relative path (used in `#load`) | `.intellisense/Macros.Intellisense.csx` |

Both scopes use the same relative path:

| Scope | Shim absolute location |
|-------|------------------------|
| Global | `%APPDATA%\Macros\.intellisense\Macros.Intellisense.csx` |
| Repo | `<sln>\.vs\Macros\.intellisense\Macros.Intellisense.csx` |

### Shim content format

Generated by `IntelliSenseShim.Generate(IReadOnlyList<string> assemblyPaths)`. Line endings are bare `\n` throughout (consistent with `CSharpCodeGenerator`).

```
// Macros — IntelliSense Shim
// Auto-generated. Do not edit. Regenerated by the Macros VSIX on package load.
// This file makes #r and using directives resolve in the C# editor when a
// .csx macro is opened in Visual Studio. The Macros runtime player ignores
// it via a custom SourceReferenceResolver — globals come from MacroGlobals.

#r "<absolute-path-1>"
#r "<absolute-path-2>"
...

using System;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Community.VisualStudio.Toolkit;
using Macros.Engine.Scripting;
using Macros.Engine.Triggers;
using static Macros.Engine.Scripting.Helpers;
using static Community.VisualStudio.Toolkit.VS;

// Editor-only stubs for script globals (MacroGlobals shape).
// At runtime these are never seen — the player swaps in a SourceReferenceResolver
// that returns empty source for this file.
EnvDTE80.DTE2 DTE = null!;
Macros.Engine.Scripting.IMacroContext Context = null!;
Macros.Engine.Triggers.IMacroTrigger Trigger = null!;
```

**Key detail — `#r` syntax:** Roslyn's scripting `#r` parser does NOT accept C# verbatim `@"..."` literals. Paths are emitted as `#r "C:\path\to\file.dll"` — the content between the outer quotes is a raw filesystem path (no C# escape sequences, no `@` prefix). Backslashes in Windows paths are literal characters in this grammar.

### Assembly paths included

Five assemblies, resolved at write time via `typeof(T).Assembly.Location`:

| Assembly | Resolved via |
|----------|-------------|
| `EnvDTE` | `typeof(EnvDTE.DTE).Assembly.Location` |
| `EnvDTE80` | `typeof(EnvDTE80.DTE2).Assembly.Location` |
| `Microsoft.VisualStudio.Shell.15.0` | `typeof(Microsoft.VisualStudio.Shell.Package).Assembly.Location` |
| `Community.VisualStudio.Toolkit` | `typeof(Community.VisualStudio.Toolkit.VS).Assembly.Location` |
| `Macros.Engine` | `typeof(Macros.Engine.Scripting.MacroGlobals).Assembly.Location` |

Assemblies with an empty `Location` (loaded from byte array) are silently skipped.

### Write semantics

`IntelliSenseShimWriter.Write(storeRoot)` returns `IntelliSenseShimWriteResult`:
- `Wrote = false` when content SHA-256 hash is unchanged (no-op; suppresses FS watcher noise).
- `Wrote = true` when file was created or updated.
- Atomic: tmp file written first, then `File.Replace` / `File.Move` into place.
- Idempotent: safe to call on every package load.

### Consumer contracts

- **Codegen (`CSharpCodeGenerator`):** Emits `#load ".intellisense/Macros.Intellisense.csx"` in each generated `.csx` file. Rusty's decision drop (`rusty-shim-load-and-resolver.md`) documents the exact emit position.
- **Player (`MacroPlayer`):** `SkipIntelliSenseShimSourceResolver` returns empty content for any `#load` whose filename (case-insensitive) matches `Macros.Intellisense.csx`. This prevents the shim's `null!` stubs from shadowing the real `MacroGlobals` at runtime.
- **Package init:** `MacrosPackage` calls `IntelliSenseShimWriter.Write(globalStoreRoot)` on load so the global shim is always up to date.
- **Repo store:** `RepoMacroStore` calls `IntelliSenseShimWriter.Write(repoStoreRoot)` when the repo macro directory is created or on solution open.

## 2026-05-01 15:34:34 UTC — Decision: emit `#load` shim directive + `SkipIntelliSenseShimSourceResolver`

**Date:** 2026-05-01T15:34:34.764-07:00  
**By:** Rusty (Engineer)  
**Todos:** `codegen-load`, `player-resolver`

### Codegen change

`CSharpCodeGenerator.EmitReferenceDirectives` now emits a `#load` directive for the
IntelliSense shim immediately before the `#r` directives:

```
// #load below pulls in the IntelliSense shim (auto-generated by the Macros VSIX)
// so the C# editor can resolve EnvDTE/EnvDTE80, the Toolkit's VS facade,
// the Macros Helpers, and script globals (DTE, Context, Trigger). The runtime
// player ignores this #load via a custom SourceReferenceResolver.
#load ".intellisense/Macros.Intellisense.csx"

// #r directives below are kept for external dotnet-script consumers; MacroPlayer
// supplies the same references via ScriptOptions at compile time.
#r "EnvDTE"
#r "EnvDTE80"
```

The relative path `.intellisense/Macros.Intellisense.csx` is a named constant:

```csharp
private const string IntelliSenseLoadDirective = "#load \".intellisense/Macros.Intellisense.csx\"";
```

This constant is the single authoritative reference for the shim path on the codegen side.
Danny's `IntelliSenseShimWriter` holds a parallel constant for the writer side. Both must agree.

### Resolver contract (`SkipIntelliSenseShimSourceResolver`)

A new private nested class in `MacroPlayer`:

| Property | Value |
|----------|-------|
| Match strategy | **Filename only** (`Path.GetFileName`) |
| Comparison | `StringComparison.OrdinalIgnoreCase` |
| Match target | `Macros.Intellisense.csx` |
| On match | Return sentinel string; `OpenRead(sentinel)` returns empty `MemoryStream` |
| On non-match | Delegate to `SourceFileResolver(ImmutableArray<string>.Empty, null)` |
| Null path | Return `null` (defensive; avoids forwarding null to `SourceFileResolver`) |

**Rationale for filename-only match:** The shim lives at different absolute paths
depending on scope:
- Global: `%APPDATA%\Macros\.intellisense\Macros.Intellisense.csx`
- Repo: `<sln>\.vs\Macros\.intellisense\Macros.Intellisense.csx`

Matching on the filename alone means the resolver works correctly for both scopes and
for any user who has the shim at a custom location, without needing to know the store
root at script-compile time.

**Why empty stream instead of null from `ResolveReference`?** Returning `null` from
`ResolveReference` would cause Roslyn to fall through to the next resolver in the chain
(or report a resolution failure). Returning a sentinel and satisfying `OpenRead` with an
empty stream is the correct Roslyn pattern for "I know this path; it has no content".

**Delegation for non-shim paths:** User-authored `#load "other.csx"` directives continue
to resolve via `SourceFileResolver` so multi-file script support is unaffected.

### Tests added

`tests/Macros.Tests/Player/MacroPlayerShimSkipTests.cs` — 5 tests:

1. Shim `#load` (no file on disk) → compile + run succeeds.
2. `Context.MacroName` returns real value (shim stubs never compiled in).
3. Non-shim `#load "/nonexistent/other.csx"` → compilation error (delegation verified).
4. Different prefix path (`/some/path/Macros.Intellisense.csx`) → also short-circuited.
5. Mixed case (`Macros.intellisense.CSX`) → also short-circuited.

All 25 codegen + shim skip tests pass.

## 2026-05-01 15:34:34 UTC — Decision: IntelliSense shim lifecycle wiring

**Date:** 2026-05-01T15:34:34.764-07:00  
**By:** Danny (Engineer)  
**Todos:** `package-init`, `repo-store-seed`

### Summary

The IntelliSense shim is seeded/refreshed in both the global and per-repo macro stores via
`IntelliSenseShimRefresher`, a new disposable class in `Macros.Lifecycle` with no VS shell
dependencies. The refresher is wired into `MacrosPackage.InitializeAsync` and subscribes to
`SolutionContextTracker.SolutionChanged` to keep the repo shim current as solutions open and close.

### `IntelliSenseShimRefresher` contract

| Aspect | Value |
|--------|-------|
| Namespace | `Macros.Lifecycle` |
| File | `src/Macros/Lifecycle/IntelliSenseShimRefresher.cs` |
| Testable without VS shell | ✅ Yes — pure C#, uses `SolutionContextTracker.CreateForTests()` |
| Error handling | Non-critical exceptions swallowed into `Action<string, Exception> onError` |
| Idempotent | ✅ Delegates to `IntelliSenseShimWriter.Write` which is itself idempotent |

**Constructor:**
```csharp
new IntelliSenseShimRefresher(
    Func<string> globalFolderProvider,
    Func<string?> repoFolderProvider,
    Action<string, Exception>? onError = null)
```

**Key methods:**
- `RefreshGlobal()` — calls `IntelliSenseShimWriter.Write(globalRoot)` unless provider returns null/whitespace
- `RefreshRepo()` — calls `IntelliSenseShimWriter.Write(repoRoot)` unless provider returns null/whitespace (no-op when no solution open)
- `AttachToTracker(SolutionContextTracker)` — subscribes `OnSolutionChanged → RefreshRepo`; throws `InvalidOperationException` if called twice
- `Dispose()` — unsubscribes from `SolutionChanged`

### Wire-up in `MacrosPackage.InitializeAsync`

Inserted **between** `SolutionContextTracker.Current = _solutionTracker;` and
`TrustGateInfoBar.InitializeAsync`. Sequence:

```csharp
_shimRefresher = new IntelliSenseShimRefresher(
    globalFolderProvider: () => MacrosPaths.ResolveGlobalFolderOrFallback(
        MacrosOptions.Instance.GlobalMacrosFolder, out _),
    repoFolderProvider: () => _solutionTracker?.GetCurrentRepoMacrosFolder(),
    onError: (which, ex) => System.Diagnostics.Trace.WriteLine(
        $"Macros: failed to refresh {which} IntelliSense shim: {ex}"));
_shimRefresher.AttachToTracker(_solutionTracker);
_shimRefresher.RefreshGlobal();
_shimRefresher.RefreshRepo(); // no-op if no solution is open
```

**Field:** `private IntelliSenseShimRefresher? _shimRefresher;` alongside `_solutionTracker`.

**Dispose order:** `_shimRefresher?.Dispose()` before `_solutionTracker?.Dispose()` so the
unsubscribe happens while the tracker is still alive.

### Rationale

- **No direct call to `RepoMacroStore`** — the refresher is independent of storage startup.
  The repo-folder provider is a lambda that reads `_solutionTracker?.GetCurrentRepoMacrosFolder()`,
  which is the same pattern used by `RepoMacroStore` itself. Both consumers converge on the
  same path computation without coupling.
- **`SolutionChanged` subscription** covers the repo-store-seed requirement: whenever a solution
  opens, `RefreshRepo()` runs and places the shim in `<sln>\.vs\Macros\.intellisense\` before
  the user can open any `.csx` file for editing.
- **Error isolation** — a malformed install (no write permission, DLL loaded from byte array)
  produces a `Trace.WriteLine` and nothing else. Package load is never blocked.

### Tests

8 tests in `tests/Macros.Tests/Lifecycle/IntelliSenseShimRefresherTests.cs`:

1. `RefreshGlobal_WritesShimToGlobalFolder` — shim exists after call
2. `RefreshGlobal_EmptyFolder_IsNoOp` — no throw for empty provider
3. `RefreshGlobal_NullReturnFromProvider_IsNoOp` — no throw for whitespace provider
4. `RefreshRepo_NullRepoFolder_IsNoOp` — no write, no throw
5. `RefreshRepo_NonNullRepoFolder_WritesShim` — shim exists after call
6. `AttachToTracker_SolutionChanged_TriggersRefreshRepo` — end-to-end via `CreateForTests()` + `ApplySolutionPath()`
7. `AttachToTracker_CalledTwice_ThrowsInvalidOperationException`
8. `Dispose_UnsubscribesFromTracker_NoFurtherWritesAfterDispose`

## 2026-05-01 16:30:25 UTC — Linus: IntelliSense debug diagnostics

**Date:** 2026-05-01T16:30:25.083-07:00
**Author:** Linus (Tester)
**Requested by:** Mads Kristensen
**Symptom:** "I'm not getting intellisense still" — `DTE`, `Context`, `Trigger` globals and `EnvDTE`/Toolkit types not resolving when editing a `.csx` macro in VS.

### Diagnostics Run

All five failure modes investigated via PowerShell diagnostics against the live machine.

**Mode A — Shim file not on disk:** ✅ CLEAR. Shim exists at `C:\Users\madsk\AppData\Roaming\Macros\.intellisense\Macros.Intellisense.csx` (1554 bytes, written 5/1/2026 4:28:36 PM).

**Mode B — Shim contents broken:** ⚠️ SECONDARY BUG CONFIRMED. Duplicate `#r` directive for `Microsoft.VisualStudio.Interop.dll` — both `typeof(DTE).Assembly.Location` and `typeof(DTE2).Assembly.Location` resolve to the same DLL in VS18 Preview. Roslyn may reject duplicate `#r` entries.

**Mode C — Macro `.csx` file missing `#load` line:** 🔴 PRIMARY CAUSE. `current.csx` recorded at 12:35 PM, before shim feature deployed (4:28 PM). Old `CSharpCodeGenerator` did not emit `#load ".intellisense/Macros.Intellisense.csx"` directive. Without it, C# editor never reads shim; globals and `#r` references never enter IntelliSense model.

**Mode D — VS Misc Files doesn't process `#load` for IntelliSense:** ❓ CANNOT TEST YET. Testable interactively after Mode C fixed.

**Mode E — Stale experimental hive:** ✅ CLEAR. New VSIX running; shim written by new code.

### Fix Plan

**Fix 1 — Mode C (PRIMARY): One-time migration of existing `.csx` files.** Enumerate all `*.csx` files in global + repo macro stores. For each missing `#load ".intellisense/Macros.Intellisense.csx"` directive, inject it after the header block. Idempotent. Atomic write via `.tmp` + `File.Replace`.

**Fix 2 — Mode B (SECONDARY): Deduplicate assembly paths in `IntelliSenseShimWriter.ResolveAssemblyPaths()`.** Use case-insensitive uniqueness check (HashSet) before adding paths to list. Forces shim regeneration on next package load.

## 2026-05-01 16:30:25 UTC — Rusty: `MacroFileLoadDirectiveMigrator` specification

**Date:** 2026-05-01T16:30:25.083-07:00
**Author:** Rusty (Engineer)
**Status:** Implemented — pending wiring by Danny

### API Contract

```csharp
namespace Macros.Engine.Storage;

public static class MacroFileLoadDirectiveMigrator
{
    public static MigrationResult Migrate(string storeRoot);
}

public sealed record MigrationResult(
    int Scanned,
    int Updated,
    int Skipped,
    IReadOnlyList<string> Errors);
```

### Behaviour

- **Argument validation:** `string.IsNullOrWhiteSpace(storeRoot)` → `ArgumentException`.
- **Missing folder:** return `(0, 0, 0, empty)`.
- **Scan:** `Directory.EnumerateFiles(storeRoot, "*.csx", SearchOption.TopDirectoryOnly)` — no recursion.
- **Shim file skip:** Skip `Macros.Intellisense.csx` (case-insensitive).
- **Idempotency:** If file already contains `Macros.Intellisense.csx` (case-insensitive) → skip.
- **Injection:** Insert shim block after last `//` comment line (header), before code.
- **Line-ending preservation:** Detect `\r\n` (CRLF) or `\n` (LF) in file; use same on write.
- **Atomic write:** `{filePath}.{guid}.tmp` → `File.Replace` or `File.Move`.
- **Per-file error handling:** Catch exceptions; append to `Errors` list; continue to next file.

### Tests (17, all green)

Covers null/empty/whitespace validation, missing folder, empty folder, injection positioning, existing `#load` detection, CRLF/LF preservation, atomic write, shim skip, recursion prevention, per-file error capture, idempotency, Roslyn parseability, and edge cases.

## 2026-05-01 16:30:25 UTC — Danny: Shim dedupe fix + Migrator wiring

**Date:** 2026-05-01T16:30:25.083-07:00
**Author:** Danny (Engineer)

### Fix 1 — Duplicate `#r` in shim (Bug #2 per Linus's diagnosis)

**File:** `src/Macros.Engine/Scripting/IntelliSenseShimWriter.cs`

Extracted new pure helper `Deduplicate(IEnumerable<string> paths)`:
- Case-insensitive (`StringComparer.OrdinalIgnoreCase`)
- Order-preserving — first occurrence wins
- Null/empty entries silently dropped

`ResolveAssemblyPaths` now calls `Deduplicate(raw)` after `TryAdd` collects paths.

### Fix 2 — Wire `MacroFileLoadDirectiveMigrator`

**Files modified:**
- `src/Macros/Lifecycle/IntelliSenseShimRefresher.cs` — Added `MacroFileLoadDirectiveMigrator.Migrate(root)` calls in `RefreshGlobal()` and `RefreshRepo()` after shim write.
- `src/Macros/MacrosPackage.cs` — Added migration call in package init after `_shimRefresher.RefreshGlobal()`.

**Stub:** `src/Macros.Engine/Storage/MacroFileLoadDirectiveMigrator.cs` — no-op stub until Rusty's real implementation landed.

### Tests added

- `IntelliSenseShimWriterTests.cs`: `ResolveAssemblyPaths_DeduplicatesIdenticalPaths_WhenInteropAssembliesShareDll`, `Deduplicate_PreservesOriginalOrdering`, `Deduplicate_IsCaseInsensitive`
- `IntelliSenseShimRefresherTests.cs`: `RefreshGlobal_WithPreExistingCsxFile_WritesShimAndRunsMigration`

### Result

Build succeeded. 45 tests pass (pre-existing + new). All green.

## 2026-05-01 17:29 — MacroFileLoadDirectiveMigrator — recursive scan with `.intellisense\` exclusion

**Date:** 2026-05-01T17:29:22.587-07:00  
**Author:** Rusty (Engineer)  
**Status:** Shipped

---

### Problem

`MacroFileLoadDirectiveMigrator.Migrate(storeRoot)` used `SearchOption.TopDirectoryOnly`.
When called with the global store root (`%APPDATA%\Macros\`), it correctly migrated
`current.csx` in the root but never descended into `%APPDATA%\Macros\Macros\` — the
`GlobalNamedSubfolder` where all named macros actually live.

Result: any macro the user opens from the tool window (`RecordedMacro.csx`, etc.) still
lacked the `#load ".intellisense/Macros.Intellisense.csx"` directive → no IntelliSense.

The repo store layout (`<sln>\.vs\Macros\*.csx`) was unaffected because named macros sit
directly in the root there — `TopDirectoryOnly` worked by coincidence.

---

### Fix

#### `src/Macros.Engine/Storage/MacroFileLoadDirectiveMigrator.cs`

1. **`SearchOption.TopDirectoryOnly` → `SearchOption.AllDirectories`** in `Directory.EnumerateFiles`.

2. **Added `IsInIntelliSenseFolder(string filePath)`** — private static helper that splits
   the path on both `\` and `/` separators and checks every *directory* segment (not the
   filename) for exact equality with `.intellisense` (OrdinalIgnoreCase).  
   This ensures:
   - `<root>\.intellisense\Macros.Intellisense.csx` — excluded ✓
   - `<root>\.INTELLISENSE\extra.csx` — excluded ✓  
   - `<root>\Macros\my.intellisense.csx` — **not** excluded ✓ (segment equality, not substring)

3. **`.intellisense\` files are filtered before `scanned++`** — they do not appear in
   `Scanned`, `Updated`, or `Skipped` counters.

4. **Belt-and-suspenders shim-filename skip retained** — if a copy of `Macros.Intellisense.csx`
   ever lands outside `.intellisense\` it is still skipped by the filename check.

5. **`Migrate` doc-comment updated** with the recursive/exclusion semantics and rationale:
   "The global store keeps named macros under a subfolder (`<root>\Macros\<name>.csx`);
   recursive scanning ensures both layouts (global and repo) are covered without the caller
   having to special-case the global named subfolder."

---

### Tests

File: `tests/Macros.Tests/Storage/MacroFileLoadDirectiveMigratorTests.cs`

| Test | Disposition |
|------|-------------|
| `Migrate_DoesNotRecurseIntoSubfolders` | **Replaced** |
| `Migrate_RecursesIntoSubfolders_LikeGlobalNamedMacroLayout` | **New** — root + `Macros\` subfolder both migrated |
| `Migrate_SkipsIntelliSenseSubfolder_EvenWhenItContainsCsx` | **New** — non-shim `.csx` in `.intellisense\`; Scanned=0 |
| `Migrate_SkipsIntelliSenseSubfolderCaseInsensitively` | **New** — `.INTELLISENSE` folder still excluded |
| `Migrate_HandlesGlobalLayoutCorrectly` | **New** — full global layout; Scanned=3, Updated=3, Skipped=0, shim untouched |
| `Migrate_FilenameContainingDotIntellisense_IsNotSkipped` | **New** — segment equality guard; `my.intellisense.csx` is migrated |
| `Migrate_HandlesTriggerDirectiveInComments_StillInjectsBeforeFirstRDirective` | **New** — `// @trigger` comment in header; `#load` injected before first `#r` |

All 984 tests pass.

---

### Why segment equality, not substring

Using `filePath.Contains(".intellisense")` would accidentally skip `my.intellisense.csx`.
Using `Path.Split` + `string.Equals(segment, ".intellisense", OrdinalIgnoreCase)` restricts
the exclusion to directory segments only, which is the correct semantic.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction


## 2026-05-01 17:51 — Write IntelliSense Shim to Both Global Root and Named-Macro Subfolder

**Date:** 2026-05-01T17:51:09.083-07:00  
**Author:** Danny  
**Status:** Implemented

## Context

The global macro store has two levels:

| Path | File | Depth relative to root |
|------|------|------------------------|
| `<global-root>\current.csx` | Ad-hoc macro | 0 (root) |
| `<global-root>\Macros\<name>.csx` | Named macros | 1 (subfolder) |

Every macro file — regardless of depth — is emitted with the same constant `#load` directive:

```csharp
#load ".intellisense/Macros.Intellisense.csx"
```

This path is relative to the file that contains it, not to the global root.

## Problem

Before this fix, `RefreshGlobal()` wrote the IntelliSense shim only to `<global-root>\.intellisense\`. That resolved correctly for `current.csx` (same directory), but named macros in `<global-root>\Macros\<name>.csx` look for `.intellisense\Macros.Intellisense.csx` relative to `<global-root>\Macros\` — which didn't exist. IntelliSense was broken for all named macros.

Mads confirmed this by manually changing `#load ".intellisense/..."` to `#load "../.intellisense/..."` in one named macro — it worked. That validated the shim approach; only the placement was wrong.

## Decision: Constant Path + Dual Write (not per-file path arithmetic)

### Option A — Per-file relative path computation (rejected)
Change the codegen to emit a path that is relative to the actual file location, e.g., `../.intellisense/...` for named macros. 

**Rejected because:**
- Creates two code paths (root vs. subfolder) in the code generator.
- The migrator would also need branching.
- Any future store layout change (e.g., deeper nesting) would require updating multiple places.
- Introduces risk of getting relative path arithmetic wrong across OS path separators.

### Option B — Constant path + dual write (chosen) ✅
Keep the codegen, migrator, and every `.csx` file emitting exactly `.intellisense/Macros.Intellisense.csx`. Instead, ensure every directory that contains macro files has a sibling `.intellisense\` folder with the shim.

**`RefreshGlobal()` now calls `IntelliSenseShimWriter.Write` twice:**
1. `Write(<global-root>)` — covers `current.csx`.
2. `Write(<global-root>\Macros)` — covers all named macros.

**Why this is better:**
- Zero changes to codegen, migrator, or any existing `.csx` file format.
- `IntelliSenseShimWriter.Write` is already idempotent (SHA-256 content guard).
- The shim content is identical in both locations — no divergence risk.
- Adding a new storage level in the future just means adding one more `Write` call.

## Implementation Notes

- `FileSystemMacroStore.GlobalNamedSubfolder` promoted from `private const` to `internal const` so `IntelliSenseShimRefresher` (in the `Macros` VSIX assembly) can reference the canonical string value. `InternalsVisibleTo("Macros")` already existed in `Macros.Engine.csproj`.
- `using System.IO` added to `IntelliSenseShimRefresher.cs` for `Path.Combine`.
- `RefreshRepo()` is unchanged — repo macros live directly under the repo root with no nested subfolder.

## Verification

- 3 new tests added: `RefreshGlobal_WritesShimInBothRootAndNamedSubfolder`, `RefreshGlobal_NamedSubfolderShimMatchesRootShim`, `RefreshGlobal_IsIdempotent_BothLocations`.
- Full suite: **987 tests, all green**.


## 2026-05-01 17:54 — Drop redundant #r "EnvDTE"/"EnvDTE80" from macro files

**Date:** 2026-05-01T17:54:53.924-07:00  
**Author:** Rusty  
**Status:** Implemented

---

## Why the `#r "EnvDTE"` lines were dead weight

Every generated `.csx` macro file previously contained:

```csx
// #r directives below are kept for external dotnet-script consumers; MacroPlayer
// supplies the same references via ScriptOptions at compile time.
#r "EnvDTE"
#r "EnvDTE80"
```

### For the editor (IntelliSense)

The IntelliSense shim (`Macros.Intellisense.csx`) already contains absolute-path `#r`
directives to the installed `Interop.EnvDTE.dll` and `Interop.EnvDTE80.dll` files.
The `#load ".intellisense/Macros.Intellisense.csx"` directive (present since commit
12056f1) pulls those in automatically.  The additional `#r "EnvDTE"` / `#r "EnvDTE80"`
lines are therefore redundant — Roslyn can resolve the same types through the shim's
absolute-path references.

### For the runtime player

`MacroPlayer.BuildScriptOptions` calls
`.WithReferences(typeof(DTE).Assembly, typeof(DTE2).Assembly)` and supplies an
`InteropAwareMetadataResolver`.  The `#r` lines in the script source are therefore
resolved a second time to the same assemblies that `ScriptOptions` already added.

### For "external dotnet-script consumers" (the comment's stated rationale)

A standalone `dotnet-script` process cannot connect to a running Visual Studio instance
to obtain a live `DTE` object.  The macro API is intrinsically coupled to the VS host.
Carrying `#r "EnvDTE"` to support a usage that cannot work is misleading and clutters
every macro file a user edits.

---

## Changes made

### `CSharpCodeGenerator.EmitReferenceDirectives`

Removed the four trailing lines (comment + two `#r` directives + blank separator).
The method now emits only the `#load` comment block and the `#load` directive itself.
XML doc updated: `(3)` → `using` block; `(4)` → step body; old `(3)` (#r item) deleted.

### `MacroFileLoadDirectiveMigrator`

Added two helpers — `HasObsoleteEnvDTEDirectives` and `StripObsoleteEnvDTEDirectives`
(both internal so tests can exercise them) — and updated `Migrate` to invoke them on
every file that still carries the old block.

**Strip semantics:**

| Scenario | Lines removed |
|---|---|
| Two preceding lines are both `//` comments AND one mentions `#r directives` | 4 lines: 2-comment block + `#r "EnvDTE"` + `#r "EnvDTE80"` |
| `// @trigger` or other comment intervenes (triggers precede `#r` lines) | 2 lines: only `#r "EnvDTE"` + `#r "EnvDTE80"` |
| `#r "EnvDTEFake"`, `#r "EnvDTE.Custom"`, or any other non-exact string | not stripped |

After removal, consecutive blank lines are collapsed to one (prevents whitespace
accumulation across repeated migration passes).

**Order of operations inside `Migrate`:** strip first → inject shim second.  This
ensures `FindBodyStart` (which drives shim injection) sees the `using` block as the
first non-comment line rather than the now-removed `#r` lines.

**Skip condition:** a file is skipped only when it already has the shim reference AND
has no obsolete `#r` block.  A file that was previously migrated (has `#load`) but
still carries the old `#r` block (generated between commit 12056f1 and this change) is
updated.

**Idempotency:** after one pass a file has the shim and no `#r "EnvDTE"`/`#r "EnvDTE80"`
lines; a second pass finds nothing to do and counts the file as Skipped.

---

## Test coverage added

- `Migrate_StripsObsoleteREnvDTEBlock_PatternA` — file with `#load` already + Pattern A block
- `Migrate_StripsObsoleteREnvDTEBlock_PatternB` — file without `#load` + Pattern B block
- `Migrate_StripsObsoleteRBlock_EvenWhenTriggerLineIntervenes` — trigger line between comment and `#r`; 2-line strip; trigger survives
- `Migrate_DoesNotStripUserAddedRDirectives` — `#r "MyCustomLib"` survives; only exact lines removed
- `Migrate_StripsObsoleteRBlock_IsIdempotent` — second pass = Skipped
- `Migrate_OnlyStripsExactEnvDTEReferences` — `#r "EnvDTEFake"` and `#r "EnvDTE.Custom"` unchanged

Existing tests 4 and 16 updated to replace the obsolete `rPos < loadPos` assertion
with `DoesNotContain("#r \"EnvDTE\"")`.
