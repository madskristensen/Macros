# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-XX-XX

### Fixed

- BeforeCommand triggers no longer freeze Visual Studio when a macro calls `ExecuteCommandAsync` for the command it is bound to. The reentrance guard now uses thread-local state so it survives the COM/OLE pump boundary that the inner `DTE.ExecuteCommand` crosses, and the guard scope returned by `TryEnter` is no longer leaked — so depth is properly released after each top-level dispatch ([#12](https://github.com/madskristensen/Macros/issues/12)).

### Added — Recording (M1 + M2)

- Record IDE actions with `Ctrl+Shift+R` (Stop with `Ctrl+Shift+P`)
- Macros toolbar with Record, Stop, Play Last, and Show Tool Window buttons
- Automatic step debouncing: consecutive typing coalesces into readable `Type("…")` calls
- Text edits recorded with intelligent caret anchoring (caret-relative fallback to absolute coords)
- VS commands recorded and resolved by name (e.g., `Refactor.Rename`, `Edit.Copy`)
- Record/Stop/Play Last context menu commands
- Record/Stop hotkey customization via Tools → Options → Keyboard
- IDE status bar displays recording and playback state
- Esc key cancels active recording or playback
- Automatic generation of human-readable C# script files (`.csx`)
- Recording session cap (configurable via Tools → Options → Macros)
- InfoBar notification when recording step limit reached

### Added — Replay & C# Scripting (M2)

- Play last recorded macro with `Ctrl+Shift+P`
- C# script execution via Roslyn (full language support: conditionals, loops, helper functions)
- Compiled script caching (SHA-256 keyed, LRU 50 entries)
- `MacroGlobals` object injected into scripts: `DTE2`, `VS` (Community Toolkit), `Context`, `Trigger`
- Helper library for readable generated code: `Type()`, `MoveCaret()`, `Select()`, `ExecuteCommand()`, `RunCommand()`, `Wait()`
- Error reporting to Output pane (Macros channel)
- Errors in Error List with clickable links to `.csx` line numbers
- InfoBar with "View output" link when script compilation or runtime fails
- Silent cancellation without error when Esc pressed during playback
- ReplayGuard prevents record-while-playing infinite loops
- Replay completes in <500 ms for typical 20-step macros

### Added — Storage & Tool Window (M3)

- Save recorded macros with name and scope (Global or Repo)
- Save As dialog with name validation and scope picker
- Per-macro preview showing where the macro will be saved
- Tool window listing all saved macros (View → Macros tool window)
- Tool window grouping by scope: 📁 Repo section (above) and 🌐 Global section (below)
- Search / filter macros by name in the tool window
- Right-click context menu on macros: Play, Edit, Rename, Delete, Open Folder
- Play macro directly from tool window (toolbar or Enter key)
- Edit macro opens the `.csx` file in VS with full IntelliSense and syntax highlighting
- Rename macro with collision detection and confirmation
- Delete macro with confirmation dialog
- Open macro folder in File Explorer from context menu
- Macro properties displayed: Name, Triggers summary, Step count, Last modified date
- Empty state message when no macros are saved
- File system watchers track changes to macro files (auto-refresh tool window on external edits)
- Atomic file operations: temp file → rename pattern prevents corruption on crash
- Tools → Options → Macros page for storage configuration
- Max recording steps and folder location customization

### Added — Triggers (M4)

- Trigger directive syntax: `// @trigger <trigger-type> [filter-expression]` in macro headers
- Event triggers: bind macros to VS events (e.g., `Build.SolutionBuildDone`, `Document.Saved`, `Solution.OnAfterOpenSolution`)
- ~40 automatic VS.Events exposed via reflection (toolkit-discovered, not hand-curated)
- Event filters: `when filename=*.cs` (glob), `when success=false` (bool), `when project=MyApp*` (glob), `when exception=*NullRef*` (glob)
- BeforeCommand triggers: synchronously intercept named commands before execution (e.g., `// @trigger BeforeCommand File.Open`)
- BeforeCommand cancellation: `Trigger.CancelCommand()` blocks the command and prevents its handler from running
- AfterCommand triggers: queue macros after named commands complete (e.g., `// @trigger AfterCommand Build.BuildSolution`)
- AfterCommand success tracking: `Trigger.Payload["Success"]` boolean in scripts
- IMacroTrigger interface: `EventName`, `FiredAt`, `IReadOnlyDictionary<string, object?> Payload`, `CancelCommand()`, `IsCancelled`
- Manage Triggers dialog (context menu on macros): visual trigger editor with type selector and autocomplete
- Automatic trigger event summary in tool window Triggers column
- Manual trigger invocation always allowed regardless of trigger definitions
- Global macro scope: `%APPDATA%\Macros\macros\{name}.csx` per user
- Repo macro scope: `<solutionDir>\.macros\{name}.csx` (team-shared, committed to source control)
- Repo macros shadow (override) same-named global macros in tool window (shadowed entries shown with strikethrough)
- Move to Repo / Move to Global context menu commands for scope migration
- Save As scope picker: radio buttons + path preview
- Per-solution trust gate: InfoBar prompts on opening a fresh solution with repo macros containing triggers
- Trust gate buttons: [Review Macros], [Allow this solution], [Block]
- Trusted Solutions can be reviewed and revoked via Tools → Options → Macros → Trusted Solutions sub-page
- Blocked solutions: triggers do not register, manual invocation still works, macros show 🔒 badge in tool window
- Auto-disable failed macros: 3 consecutive failures → triggers unregistered + InfoBar notification
- Re-entrance protection: prevents infinite loops (e.g., a BeforeCommand handler that invokes the same command)
- Re-entrance depth cap: 3 levels of nested trigger-driven execution
- Serial trigger execution queue: VS.Events, BeforeCommand, and AfterCommand macros never overlap
- Trigger hint InfoBar (one-time) when editing a `.csx` macro file, with link to Manage Triggers dialog
- Disable All Triggers kill switch (Tools → Options → Macros → General) for emergency trigger suppression
- BeforeCommand timeout protection: 2-second (configurable) timeout per BeforeCommand macro
- Fail-safe behavior: on timeout or exception, command proceeds unblocked (never hangs or breaks IDE)
- Color-coded status bar during triggered execution: red foreground `"Triggered: {EventName}"`

### Added — General

- GitHub Actions CI pipeline: builds on every push (Windows, .NET Framework 4.8)
- VSIX artifact published to Actions workflow for testing and distribution
- SDK-style `*.csproj` format (no legacy MSBuild, modern NuGet PackageReferences)
- Comprehensive README with hotkey reference, trigger cookbook, and scope explanation
- License: MIT
- No telemetry or analytics (privacy-first)
- No code signing in v1 (reduces release friction; revisit in v1.x if Marketplace mandates)

### Notes

- **v1 scope:** Manual + triggered recording/playback, global + repo storage, event/command triggers, trust gates, edit-as-code, full C# scripting via Roslyn.
- **v1 limitations (deferred to v1.x):** Parameterized macros, conditional logic, repeat-N loops, snippet sharing, Marketplace import, macro parameterization.
- **Known hotkey conflicts:** `Ctrl+Shift+R` shadows `View.RefreshRemoteReferences` (low frequency, rebindable via Tools → Options → Keyboard).
- **Modal dialog interaction during replay:** Macros execute even if VS shows a dialog (e.g., Refactor.Rename UI); user must interact manually. Documented as known limitation.
- **Initial release.** No prior versions.
