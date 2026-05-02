# Macros Extension — Research & Recommendations

**Author:** Livingston (PM)  
**Date:** 2026-05-02  
**Status:** Research Complete — Ready for Review

---

## 1. Executive Summary

The Macros extension is already strong in its core loop (record → edit .csx → replay) but has significant growth potential in five areas: **(1) AI-assisted macro authoring** via GitHub Copilot agents/skills — the single highest-impact opportunity given VS 2026's AI-native architecture; **(2) A "macro gallery" sharing model** (gist export/import + curated starter pack) to solve discoverability and onboarding; **(3) Compilation performance** via persistent script caching and Roslyn incremental improvements; **(4) Debugging support** — even basic "step-through" via Roslyn scripting diagnostics would leapfrog every competitor; **(5) Parameterized macros** that accept user input at runtime, unlocking template-like workflows without full extension development.

---

## 2. Historical Systems Analysis

### 2.1 Excel VBA Macros — What Worked

| Factor | Detail |
|--------|--------|
| **Zero-knowledge entry** | Users recorded actions without writing code. Generated VBA was *educational* — people learned programming by reading their own recordings. |
| **Edit-after-record** | The "record → inspect → tweak" loop gave non-programmers a progressive on-ramp to scripting. |
| **Embedded environment** | The VBA editor lived inside Excel — no context switch. Object Browser let users explore the API visually. |
| **Trigger attachment** | Macros bound to buttons, menus, and keyboard shortcuts directly in the document. |
| **Sharing via workbooks** | Macros traveled with the file — zero friction distribution. |

**Pain points:** Security nightmares (macro viruses), no version control, brittle absolute references, limited debugging.

### 2.2 Visual Studio Macros (VS 2010 and Earlier)

| Factor | Detail |
|--------|--------|
| **Macros IDE** | Separate VB.NET editor window with IntelliSense, project references, debugging — essentially a mini-IDE inside VS. |
| **DTE object model** | Full programmatic access to the IDE: editor, solution, debugger, build. |
| **Macro Explorer** | Tree view listing all macros with run/edit/delete. |
| **Recording** | Captured text edits + commands, generated VB.NET calling DTE. |
| **Keyboard binding** | Any macro could be bound to any shortcut via Tools > Options > Keyboard. |
| **Samples** | Shipped with ~50 example macros covering common tasks. |

**Why removed (VS 2012):** Low telemetry usage (power-user skew), high maintenance cost of the COM-based VSA engine, security concerns from arbitrary code execution, strategic bet on VSIX extensibility model.

**What users missed most:** Quick automation without creating a full extension project. The "record something once, bind to a key, use forever" workflow had no replacement for 10+ years.

### 2.3 Key Takeaways

| Adopt ✓ | Avoid ✗ |
|---------|---------|
| Record-then-edit progressive disclosure | Separate editor window (our .csx-in-VS approach is better) |
| Rich sample library for onboarding | Macro security theater (trust the developer) |
| One-click shortcut binding | Absolute references that break across files |
| Object model browsing (IntelliSense shim ✓) | Heavyweight scripting runtime (VBA/VSA) |
| Sharing built into the format | Hidden binary storage formats |

---

## 3. Competitive Landscape

### 3.1 VS Code Extensions

| Tool | Installs | Key Features | Strength vs. Us | Weakness vs. Us |
|------|----------|--------------|-----------------|-----------------|
| **VSCode Macros** (EXCEEDSYSTEM) | ~19,200 | JavaScript scripting, Node.js modules, VS Code API access, debuggable | Full JS ecosystem access | No recording, no IntelliSense shim, manual-only |
| **Macro Recorder** (C10udburst) | ~5,300 | Record file modifications, replay, Alt+F9/F10 | Simpler UX | Text-only, no commands, no editing of recorded macros |
| **Recordable KB Macro** (tshino) | ~3,000+ | Keystroke recording/playback, Ctrl+Alt+R/P | Lightweight, fast | No command recording, no scripting, no customization |
| **Macros for VS Code** (damolinx) | ~400 | JS/TS scripting, Node sandbox | Sandboxed safety | Low adoption, no recording |

### 3.2 JetBrains (IntelliJ/Rider)

| Aspect | Detail |
|--------|--------|
| **Recording** | Edit > Macros > Start. Editor actions only (no dialogs/tool windows). |
| **Storage** | Named macros in IDE settings. Exportable via settings sync. |
| **Shortcuts** | Bind via Settings > Keymap. |
| **Editing** | Can remove individual steps, rename. Cannot add steps or write code. |
| **Sharing** | Export/import via IDE settings archive. No marketplace. |

**Our advantage:** Editable .csx scripts are far more powerful than JetBrains' opaque action lists. We can do conditional logic, loops, API calls — they cannot.

### 3.3 OS-Level & Editor Automation

| Tool | Model | Strength | Weakness |
|------|-------|----------|----------|
| **Vim macros** | Register-based keystroke recording. Composable (`@a` calls `@b`). Repeat with count (`5@a`). | Blazing fast, composable, zero overhead | No IDE awareness, no API access, text-only |
| **Emacs keyboard macros** | Record/replay + full elisp scripting for complex logic | Infinite power via elisp | Steep learning curve, alien to VS users |
| **AutoHotKey** | OS-level input simulation | Works anywhere | Fragile against UI changes, no semantic understanding |

### 3.4 Modern Scripting Tools

| Tool | Relevance |
|------|-----------|
| **dotnet-script 2.0** (Nov 2025) | .NET 10 + C# 14 support, isolated assembly load contexts, improved caching. We could adopt their cache-path model. |
| **.NET 10 single-file scripts** (`dotnet run app.cs`) | New native scripting — potential long-term replacement for .csx? Uses `#:package` syntax. Not yet viable for our in-process DTE scenario. |
| **LINQPad** | Premium C# scratchpad. Rich output formatting, dump visualization. Inspiration for "macro output" visualization. |

---

## 4. Technology Assessment

### 4.1 Current Stack Evaluation

| Component | Current | Assessment | Action |
|-----------|---------|------------|--------|
| **Roslyn Scripting** | Microsoft.CodeAnalysis.CSharp.Scripting (latest stable) | Solid. Incremental compilation improvements flow in automatically. | ✅ Keep. Ensure we're on latest NuGet. |
| **Script Caching** | `ScriptCompilationCache` (in-memory per session) | Good but ephemeral. Cold-start penalty on VS restart. | 🔶 Enhance: Add persistent disk cache (hash .csx content → compiled assembly). |
| **IntelliSense Shim** | Auto-generated .csx with `#r` directives | Working well after Waves 1-5 fixes. | ✅ Keep. Consider source-generator approach long-term. |
| **Extension Framework** | Community.VisualStudio.Toolkit (in-process, .NET 4.8) | Mature, actively maintained (v17.0.545+). | ✅ Keep for VS 2022. Plan dual-target for VS 2026. |
| **VS.Extensibility OOP** | Not adopted | Still in preview; missing many APIs we need (DTE, editor buffers). | ⏳ Monitor. Don't migrate until stable + full API coverage (2027+). |
| **Target Framework** | .NET Framework 4.8 | Required for in-process VSIX in VS 2022. | ✅ Keep. VS 2026 may allow .NET 8+ for in-process — watch for announcements. |

### 4.2 Recommended Technology Changes

#### Short-term (next release)
1. **Persistent compilation cache** — Hash .csx content + referenced assemblies → store compiled IL on disk in `%APPDATA%\Macros\.cache\`. Eliminates cold-start compilation latency.
2. **Update Roslyn scripting packages** to latest stable for incremental perf gains.
3. **Evaluate `dotnet-script`'s isolated load context pattern** for assembly conflict prevention.

#### Medium-term (3-6 months)
4. **Copilot agent/skill integration** — Register a `@Macros` Copilot agent that can generate, explain, and fix .csx macros. Leverage VS 2026's `.agent.md` + skills model.
5. **Source generator for IntelliSense shim** — Replace runtime codegen with a build-time source generator that emits the shim. Faster, more reliable, version-locked.

#### Long-term (6-12 months)
6. **Dual-target VS 2022 + VS 2026** — Prepare csproj for API-version-based compatibility model.
7. **Evaluate migration to VisualStudio.Extensibility** once it reaches stable + covers our API needs.

### 4.3 AI Integration Opportunity

VS 2026 introduces custom Copilot agents with skills. The Macros extension should:
- Ship a `macros.agent.md` that teaches Copilot about macro authoring, the DTE API, and helper methods.
- Expose a **"Generate Macro"** command: user describes intent in natural language → Copilot generates .csx.
- Expose a **"Fix Macro"** command: user selects error → Copilot explains + suggests fix.
- This is the single biggest differentiator we can ship — no competitor has it.

---

## 5. Feature Gap Matrix

| Feature | Us (Macros) | VSCode Macros | JetBrains | Vim | Priority |
|---------|-------------|---------------|-----------|-----|----------|
| Record editor actions | ✅ | ❌ | ✅ | ✅ | — |
| Record commands | ✅ | ❌ | ✅ (limited) | ❌ | — |
| Edit recorded macro as code | ✅ (.csx) | ✅ (JS) | ❌ | ❌ (registers) | — |
| IntelliSense while editing | ✅ | Partial | N/A | N/A | — |
| Keyboard shortcut binding | ✅ (triggers) | ✅ | ✅ | ✅ | — |
| Event-based triggers | ✅ | ❌ | ❌ | ❌ | — |
| **Debugging/stepping** | ❌ | ✅ (JS debugger) | ❌ | ❌ | 🔴 High |
| **AI-assisted authoring** | ❌ | ❌ | ❌ | ❌ | 🔴 High |
| **Parameterized macros** | ❌ | Partial (via prompts) | ❌ | ❌ | 🔴 High |
| **Sharing/marketplace** | ❌ | ❌ | Export only | ❌ | 🟡 Medium |
| **Sample macro library** | ❌ | ❌ | ❌ | N/A | 🟡 Medium |
| **Undo integration** | ❌ | ❌ | ❌ | ✅ (`.` undo) | 🟡 Medium |
| Composable macros (call others) | ❌ | ❌ | ❌ | ✅ | 🟢 Low |
| Multi-file macros | ✅ (#load) | ✅ (require) | ❌ | ❌ | — |
| Repeat count | ❌ | ❌ | ❌ | ✅ (`5@a`) | 🟢 Low |
| Conditional recording | ❌ | ❌ | ❌ | ❌ | 🟢 Low |

---

## 6. UX/Workflow Recommendations

### Priority 1 — High Impact, Moderate Effort

| # | Recommendation | Impact | Effort |
|---|---------------|--------|--------|
| 1 | **"Describe what you want" AI macro generation** — Copilot agent generates .csx from natural language description | Transforms accessibility; attracts non-scripters | Medium (agent.md + skill authoring) |
| 2 | **Sample macro gallery** — Ship 10-15 curated macros (duplicate line, sort selection, insert timestamp, wrap in try/catch, toggle comment style, etc.) | Solves discoverability, teaches by example | Low |
| 3 | **Persistent compilation cache** — Eliminate cold-start delay | Immediate UX improvement for power users | Low-Medium |
| 4 | **Parameterized macros** — `IMacroContext.PromptAsync("Find:", defaultValue)` helper that shows VS input box at runtime | Unlocks template workflows | Low |

### Priority 2 — Medium Impact, Medium Effort

| # | Recommendation | Impact | Effort |
|---|---------------|--------|--------|
| 5 | **Macro debugging** — Roslyn scripting supports emitting PDBs. Wire up VS debugger attachment for .csx execution | Huge for complex macros; unique among competitors | Medium-High |
| 6 | **Gist export/import** — One-click publish macro as GitHub Gist; import from URL | Lightweight sharing without marketplace infra | Medium |
| 7 | **Undo-as-single-transaction** — Wrap macro execution in `ITextUndoTransaction` so Ctrl+Z reverts the entire macro | Expected behavior; currently missing | Medium |
| 8 | **First-run experience** — On first install, show info bar: "Record your first macro with Ctrl+Shift+R" + link to samples | Critical for discoverability | Low |

### Priority 3 — Lower Impact or Higher Effort

| # | Recommendation | Impact | Effort |
|---|---------------|--------|--------|
| 9 | **Macro marketplace** (VS extension gallery integration or community repo) | Long-term growth | High |
| 10 | **Repeat count** — "Play macro N times" prompt or command | Nice-to-have; Vim users expect it | Low |
| 11 | **Composable macros** — Allow `#load "other-macro.csx"` to call another macro's logic | Power user feature | Low |
| 12 | **Snippet ↔ Macro bridge** — Convert snippet to macro (adds cursor movement) or macro to snippet | Novel integration | Medium |

### Discoverability Strategy

Current problem: Users don't know the extension exists or what it can do.

Recommendations:
1. **First-run info bar** with call-to-action
2. **Status bar indicator** during recording (already done ✅)
3. **"Record Macro" in right-click editor context menu** (high visibility)
4. **Copilot agent** that suggests "Would you like me to create a macro for this?" when it detects repetitive edits
5. **Tool window** with macro list + "New from template" button

---

## 7. Recommended Roadmap

### Short-term (Next 1-2 Releases)

| Item | Category |
|------|----------|
| Sample macro gallery (10-15 macros) | Onboarding |
| Persistent compilation cache | Performance |
| Parameterized macros (`PromptAsync`) | Feature |
| Repeat count ("Play N times") | Feature |
| First-run info bar | Discoverability |
| Undo-as-single-transaction | Polish |

### Medium-term (3-6 Months)

| Item | Category |
|------|----------|
| Copilot agent for macro generation/explanation | AI |
| Macro debugging (PDB emission + debugger attach) | Feature |
| Gist export/import sharing | Community |
| "Record Macro" in editor context menu | Discoverability |
| Composable macros (#load other macros) | Power users |
| Update Roslyn packages + adopt isolated load contexts | Tech debt |

### Long-term (6-12 Months)

| Item | Category |
|------|----------|
| Macro marketplace / community gallery | Ecosystem |
| VS 2026 dual-targeting | Platform |
| VisualStudio.Extensibility evaluation/migration | Platform |
| Snippet ↔ Macro bridge | Integration |
| AI "detect repetition → suggest macro" | AI |
| Source-generator-based IntelliSense shim | Architecture |

---

## Appendix A: Data Sources

- VS Code Marketplace extension pages (install counts as of 2026-05-02)
- JetBrains IntelliJ/Rider macro documentation
- Microsoft Learn: VisualStudio.Extensibility docs
- GitHub Blog: Copilot in Visual Studio March 2026 update
- dotnet-script GitHub releases (v2.0, Nov 2025)
- Microsoft .NET Blog: .NET 10 single-file apps
- Community.VisualStudio.Toolkit GitHub (v17.0.545)
- Stack Overflow / DevBlogs: VS Macro removal rationale

## Appendix B: Risk Notes

1. **Copilot dependency** — AI features require Copilot license. Ensure all AI features degrade gracefully (extension works fully without Copilot).
2. **VS.Extensibility migration** — Premature migration would break the extension. Stay in-process until OOP model covers DTE + editor buffers (likely 2027+).
3. **.NET Framework 4.8 lock-in** — Cannot use .NET 10 scripting natively. Roslyn scripting on .NET 4.8 remains the right choice until VS drops Framework support.
4. **Persistent cache invalidation** — Must hash all inputs (script text, referenced assemblies, Roslyn version) to avoid stale cache hits.
