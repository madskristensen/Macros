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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
