# Rusty — History

## Core Context

- **Project:** A Visual Studio extension for recording and playing back editor macros
- **Role:** Engineer
- **Joined:** 2026-04-30T21:37:32.796Z

## Learnings

### 2026-04-30 — UX Architecture pass

**Locked decisions:** Toolbar + hotkeys (Ctrl+Shift+R/P) + tool window ListView + VSCT context menus + UIContexts. BaseCommand<T> + BaseOptionModel<MacrosOptions> + DynamicResource VS theming.

---

### 2026-04-30 — C# Scripting Pivot

Storage switched from JSON to .csx via Roslyn. UX surface unchanged (toolbar/hotkeys/tool window/context menus); underlying storage now C# scripts. Users can hand-edit with IntelliSense.

---

### 2026-04-30 — Triggers & Scope UX

**Tool window:** CollectionViewSource grouped (Repo/Global); shadowed globals via strikethrough + tooltip. Triggers column (event summary + tooltip). **Manage Triggers DialogWindow:** ListBox of triggers + Add panel (ComboBox + filter TextBox). Atomic .csx rewrite on OK. **Per-solution trust:** InfoBar [Review]/[Allow]/[Block] on first solution open. **Other surfaces:** Save As scope picker (Radio), status bar triggered-exec message, tool window refresh button + repo empty state.

**Locked for M4 Triggers/Scope** per coordinator + Rusty-triggers-and-scope.md decision.

---

### 2026-05-01 — Wave 1: IntelliSense shim (#load + resolver skip)

**Codegen (CSharpCodeGenerator):** Emits #load ".intellisense/Macros.Intellisense.csx" before #r directives (new const IntelliSenseLoadDirective).

**Player (MacroPlayer):** SkipIntelliSenseShimSourceResolver matches shim filename (case-insensitive), returns empty stream. Delegates other #load paths to SourceFileResolver.

**Tests:** 5 new (shim absent, real globals, non-shim missing, prefix path, mixed case). 25 total green. Golden file updated.

---

### 2026-05-01 — Wave 3: docs refresh

docs/csx-reference.md, docs/architecture.md, docs/ship-readiness-v1.0.0.md, docs/manual-smoke.md updated. IntelliSense feature marked shipped (no longer v1.1 backlog).

---

### 2026-05-01 — Wave 4: migrator + recursion fix

**Migrator (MacroFileLoadDirectiveMigrator):** Scans *.csx files; injects 6-line shim block before first non-comment line if absent. Idempotent. Preserves CRLF/LF line endings. Atomic write via .tmp + File.Replace. Per-file error handling.

**Recursion fix:** Switched SearchOption.TopDirectoryOnly → AllDirectories. Added IsInIntelliSenseFolder (segment-based exclusion; prevents false skip of my.intellisense.csx). .intellisense\ files excluded before scanned++ counter.

**Tests:** 7 new + 1 replaced. Global named macros layout verified (root + Macros\ both migrated). .intellisense case-insensitive exclusion verified. Trigger directive header handling verified.

**Result:** 984 tests pass (979 baseline). Migrator finds all named macros in global store. Release build clean. VSIX ready (5:42 PM 5/1/2026).

