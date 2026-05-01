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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
