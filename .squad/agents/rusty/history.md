# Rusty — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30T21:37:32.796Z

## Learnings

### 2026-04-30 — UX Architecture pass

**Project context:**
- VS 2022 Macros extension; VSIX Community Toolkit (in-process), .NET Framework 4.8
- v1 MVP: record text edits + VS commands; replay; save named macros as JSON
- No DTE macro engine dependency — pure VS SDK / MEF approach

**Locked UX decisions:**
- Toolbar: `type="Toolbar"` in .vsct, named "Macros", `DefaultDocked`
- Hotkeys: Ctrl+Shift+R (Record/Stop toggle) and Ctrl+Shift+P (Play Last), `guidVSStd97` global scope
  - Conflicts with `View.RefreshRemoteReferences` (Ctrl+Shift+R) and `Edit.PasteSpecial` (Ctrl+Shift+P, designer-scoped only) — both low-frequency, acceptable trade-off; document for users
- Tool window: `BaseToolWindow<MacrosToolWindow>` async-init pattern; WPF UserControl with search box, ListView (Name/Steps/LastModified), bottom action bar
- Context menus: VSCT type="Context" + `IVsUIShell.ShowContextMenu` — NOT WPF ContextMenu
- State visibility: two UIContexts (`RecordingContext` / `IdleContext`) toggled via `IVsMonitorSelection.SetCmdUIContext`

**UI patterns chosen:**
1. `BaseCommand<T>` for all command handlers; `BeforeQueryStatus` for dynamic enable/disable
2. `BaseOptionModel<MacrosOptions>` for Tools → Options page with StorageFolder, MaxRecordingSteps, WarnAfterSteps, RecordTextEdits, RecordCommands, PromptBeforeOverwrite, FirstRunCompleted
3. VS theming via `DynamicResource` on `TreeViewColors`, `CommonControlsColors`, `EnvironmentColors` brush keys — ensures automatic high-contrast and theme adaptation

---

### 2026-04-30 — TEAM UPDATE: C# Scripting Pivot (Canonical Storage Decision)

**Context:** User directive captured by Copilot; mid-flight architectural pivot from JSON to C# scripting.

**Decision:** Engine architect pivoted to C# scripting (`.csx` files via Roslyn). Macro storage format is now `.csx` in `%APPDATA%\Macros\` (not JSON). Users can hand-edit macros in VS with full IntelliSense — far more powerful than static JSON events.

**Implications for your UX design:**
- Your toolbar, hotkeys, tool window, and context menu designs are **unchanged and still locked**.
- The tool window ListView will now display `.csx` files instead of JSON — cosmetically no difference, but the underlying storage is C# script.
- "Edit Macro" command (future enhancement; not v1) will open the `.csx` file directly in VS for editing with IntelliSense.
- The "Save As" dialog will save to `.csx` (not JSON).

**Your role in M4 (UI Arc):**
Implement the toolbar, tool window, hotkeys, and context menus as designed. The storage layer is handled by Danny's engine. Your UX surface remains pure SDK/MEF — no Roslyn dependencies.

---

### 2026-04-30 — Triggers & Scope UX pass (directives #2 + #3)

**Scope-aware tool window (grouped Repo/Global with shadowing):**
- Tool window ListView uses `CollectionViewSource` with `GroupDescriptions` + WPF `GroupStyle` `Expander` to render two collapsible groups: "📁 Repo: SolutionName" (top) and "🌐 Global" (bottom).
- `MacroScope.Repo = 0` / `MacroScope.Global = 1` — ascending sort puts Repo first automatically, no custom comparer needed.
- Shadowed global macros (overridden by a same-name repo macro) render via `DataTrigger` on `IsShadowed`: grey `Foreground` (`EnvironmentColors.SystemGrayTextBrushKey`) + `TextDecorations.Strikethrough` + tooltip.
- Scope filter chips are WPF `ToggleButton` strip bound to a `ScopeFilter` enum — all within the UserControl, not VSCT.
- Triggers column uses `TriggerSummary` (comma-separated event names or "Manual") and `TriggerFullDescription` as cell tooltip.

**Manage Triggers DialogWindow rewrites .csx `@trigger` header atomically:**
- `ManageTriggersDialog` is a `Microsoft.VisualStudio.PlatformUI.DialogWindow` with a `ListBox` of current triggers (each row has a `KnownMonikers.Cancel` Remove button) and an "Add trigger" panel (event `ComboBox` + optional filter `TextBox`).
- Filter validation: `key=value` per-line regex; inline error shown via `DataTrigger` on `FilterError`.
- On OK: engine writes to `{path}.tmp` then `File.Replace` (atomic rename) — prevents corruption on crash.
- Empty state: "No triggers — this macro runs only when invoked manually." shown via `DataTrigger` on `Triggers.Count == 0`.

**Per-solution trust prompt via `IVsInfoBarUIFactory`:**
- `RepoTrustInfoBarManager` shows an InfoBar on first solution open when any `.macros/*.csx` has `@trigger` and the solution is not in `TrustedSolutions`/`BlockedSolutions`.
- Icon: `KnownMonikers.StatusInformation`. Three actions: [Review] / [Allow] / [Block]. X (close) = defer (re-prompts next open).
- Prefer main window `IVsInfoBarHost` (survives document switches); fall back to active document frame host.
- Suppress when `MacrosOptions.TrustNewSolutionsAutomatically == true`.
- Trust state persisted in `MacrosOptions.TrustedSolutions` / `BlockedSolutions` (List&lt;string&gt; of solution paths).
- `TrustedSolutionsPage` is a custom `DialogPage` sub-page under "Macros" in Tools → Options, with separate Remove-able lists for Trusted and Blocked paths plus master kill switch checkboxes.

**Other new surfaces:**
- Save As scope picker: `RadioButton` group (Global/Repo) in `DialogWindow`; Repo disabled with tooltip when no solution open; resolved path preview below.
- Triggered execution status bar: `IVsStatusbar.SetText("Macros: {EventName} → {MacroName}")`; red foreground via `IVsStatusbar2.SetForegroundColor` for failures (5s then clear).
- Tool window trigger hint: inline `Border` using `InfoBarColors` brush keys; one-shot per session; shown after first Save As.
- New Macro button: `KnownMonikers.NewItem` → `VS.Documents.OpenAsync` on skeleton .csx.
- Refresh button: `KnownMonikers.Refresh` → `ICompositeMacroStore.ForceRescanAsync`.
- Repo empty state: inline panel in empty Repo group + "Create .macros folder" command that also writes a README.md.

## Round 3 outcome

**Rusty-1's trigger UX extension is the locked UI spec for M4 Triggers/Scope, with one coordinator-authored delta.**

Details (see `.squad/decisions.md` and `rusty-triggers-and-scope.md`):
- Grouped tool window (Repo/Global) with strikethrough shadowing for overridden macros
- Triggers column with summary text + full tooltip; Manage Triggers DialogWindow
- Atomic `.csx` rewrite on trigger save (write to `.tmp`, then `File.Replace`)
- Per-solution trust prompt via InfoBar with [Review] / [Allow] / [Block]
- Save As scope picker (Global/Repo); Repo disabled when no solution open

**Coordinator delta:** Added Trigger Type selector to Manage Triggers dialog (dropdown: Event / BeforeCommand / AfterCommand) to make macro intent explicit and improve discoverability.

---

## Learnings

### 2026-05-01 — Wave 1: emit `#load` + add `SkipIntelliSenseShimSourceResolver`

**Codegen change (`CSharpCodeGenerator.cs`):**
- Added `private const string IntelliSenseLoadDirective = "#load \".intellisense/Macros.Intellisense.csx\""` — single source of truth for the shim filename.
- `EmitReferenceDirectives` now emits the `#load` block (with an explanatory comment) _before_ the `#r` directives. The `#r` comment was updated to note they are for external dotnet-script consumers only.
- Class doc-comment updated: 4-item output contract expanded to 5 items, with (2) describing the new `#load` shim directive and its runtime-stripping guarantee.

**Golden file (`tests/Macros.Tests/Codegen/golden/sample.csx`):**
- Updated to match the new `EmitReferenceDirectives` output — both the `#load` line and the revised `#r` comment block.

**Player change (`MacroPlayer.cs`):**
- Added `SkipIntelliSenseShimSourceResolver : SourceReferenceResolver` — a private sealed nested class that:
  - Matches by **filename only** (case-insensitive, `OrdinalIgnoreCase`), tolerates any path prefix.
  - Returns a sentinel string from `ResolveReference`; `OpenRead` on that sentinel returns an empty `MemoryStream`.
  - Delegates all non-shim paths to `SourceFileResolver(ImmutableArray<string>.Empty, null)` so user `#load "other.csx"` continues to work.
  - Handles null `path` defensively (returns null rather than forwarding null to `SourceFileResolver`).
- Wired into `BuildScriptOptions` via `.WithSourceResolver(SkipIntelliSenseShimSourceResolver.Instance)`.

**Tests (`MacroPlayerShimSkipTests.cs`):**
- 5 new tests covering: shim absent from disk still succeeds, real globals not shadowed, non-shim load fails on missing file, arbitrary prefix path short-circuited, mixed-case filename short-circuited.
- All 25 codegen + shim skip tests green.

**Key invariant:** The shim filename `Macros.Intellisense.csx` must stay in sync between `CSharpCodeGenerator.IntelliSenseLoadDirective` and `SkipIntelliSenseShimSourceResolver.ShimFileName`. Danny's `IntelliSenseShimWriter` holds a parallel constant for the writer side.
