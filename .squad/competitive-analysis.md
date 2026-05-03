# Competitive Analysis — Macros for Visual Studio

> Saved 2026-05-03. Sources: live fetches of the JetBrains help center, the Visual Studio Marketplace listings for Visual Commander, `geddski.macros`, `ryuta46.multi-command`, `tshino.kb-macro`, and `EXCEEDSYSTEM.vscode-macros`; existing knowledge for VBA, old VS Macros (2002–2010), Vim, Emacs, Sublime, and Notepad++.

**Legend:** ✅ first-class · ◐ partial / awkward / scriptable-but-not-out-of-the-box · ❌ absent · — not applicable

## Feature matrix

| # | Feature | **Macros (VS)** | Old VS Macros | VBA / Office | JB built‑in | JB Script. Console | Visual Commander | VSC kb‑macro | VSC macros / multi‑cmd | VSC EXCEEDSYSTEM | Vim `q` | Emacs kbd | Sublime / N++ |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Recording** ||||||||||||||
| 1 | Record typing | ✅ | ✅ | ✅ | ✅ (action) | — | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ |
| 2 | Record IDE/editor commands | ✅ | ✅ | ✅ | ✅ | — | ✅ | ◐ (only built‑ins by default) | ❌ (manual) | ❌ | ✅ | ✅ | ◐ |
| 3 | Record menu / dialog / popup | ❌ | ❌ | ◐ | ❌ (explicit) | — | ❌ | ❌ (explicit) | — | — | ❌ | ❌ | ❌ |
| 4 | Step aggregation (typing coalesced) | ✅ | ❌ | ❌ | ❌ | — | ❌ | ❌ | — | — | — | — | ❌ |
| 5 | Recording cap / safety | ✅ (5000) | ❌ | ❌ | ❌ | — | ❌ | ❌ | — | — | — | — | ❌ |
| **Editing & language** ||||||||||||||
| 6 | Stored as transparent, editable source | ✅ (.csx) | ✅ (VBA) | ✅ | ◐ (XML, action list only) | ✅ | ✅ | ◐ (JSON config) | ✅ (settings.json) | ✅ (.js files) | ◐ (register text) | ◐ (Elisp via `name-last-kbd-macro`) | ◐ |
| 7 | Full general‑purpose language | ✅ (C#) | ✅ (VBA) | ✅ (VBA) | ❌ | ✅ (Kotlin/Groovy/JS) | ✅ (C#/VB) | ❌ | ❌ | ✅ (JS + Node) | ◐ (Vimscript/Lua outside macro) | ✅ (Elisp) | ❌ |
| 8 | Full IDE/automation API access | ✅ (DTE + VS facade) | ✅ (DTE) | ✅ (Office model) | ❌ | ✅ (IntelliJ Platform) | ✅ (DTE) | ❌ | ❌ | ✅ (vscode API) | ◐ | ✅ | ❌ |
| 9 | IntelliSense in macro editor | ✅ (Roslyn + shim) | ✅ (VBE) | ✅ (VBE) | — (no editor) | ✅ | ◐ (basic in dialog) | ❌ | ❌ | ◐ (manual @types) | ❌ | ◐ | ❌ |
| 10 | Top‑level `await` / async | ✅ | ❌ | ❌ | — | ✅ | ◐ | — | — | ✅ | — | ◐ | — |
| 11 | Debugger | ❌ | ✅ (VBE) | ✅ (VBE) | ❌ | ◐ | ❌ | ❌ | — | ✅ (VS Code debugger) | ❌ | ◐ (`edebug`) | ❌ |
| **Triggers / automation** ||||||||||||||
| 12 | Manual run via hotkey | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 13 | IDE event triggers (build/save/debug/…) | ✅ | ✅ | ✅ | ❌ | ◐ (manual subscription) | ✅ | ❌ | ❌ | ❌ | ◐ (autocmds) | ◐ (hooks) | ❌ |
| 14 | Pre‑command interception (cancel) | ✅ | ✅ | ◐ (Before* events) | ❌ | ◐ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ (advice) | ❌ |
| 15 | Post‑command observation | ✅ | ✅ | ✅ | ❌ | ◐ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ |
| 16 | Trigger filters (e.g. `filename=*.cs`) | ✅ | ❌ | ❌ | — | ❌ | ❌ | ❌ | ◐ (`languages` in mc) | ❌ | ◐ | ◐ (mode hooks) | ❌ |
| 17 | Re‑entrance / recursion guard | ✅ (depth 3) | ❌ | ❌ | — | ❌ | ❌ | ❌ | — | — | ❌ | ❌ | ❌ |
| 18 | Auto‑disable on repeated failure | ✅ | ❌ | ❌ | — | ❌ | ❌ | ❌ | — | — | ❌ | ❌ | ❌ |
| 19 | Master kill switch | ✅ | ❌ | ❌ | — | ❌ | ◐ (disable ext) | ❌ | — | — | ❌ | ❌ | ❌ |
| **Storage & sharing** ||||||||||||||
| 20 | Per‑user / global scope | ✅ | ✅ | ◐ (`Personal.xlsb`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| 21 | Per‑project / per‑repo scope | ✅ (`.vs/Macros`) | ❌ | ◐ (per‑document) | ❌ | ❌ | ❌ | ◐ (workspace settings) | ◐ (workspace settings) | ❌ | ❌ | ❌ | ❌ |
| 22 | Version‑controllable storage | ✅ (text .csx) | ◐ (.vsmacros, awkward) | ◐ (binary) | ◐ (XML, global) | ◐ (script files) | ❌ (per‑user XML) | ◐ | ◐ | ◐ | ◐ | ◐ | ◐ |
| 23 | Team‑shared by default | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Security** ||||||||||||||
| 24 | Trust gate / consent prompt for shared macros | ✅ | ❌ | ◐ (Office trust center, doc‑level) | — | — | ❌ | — | — | — | — | — | — |
| **UX** ||||||||||||||
| 25 | Native management UI (list + actions) | ✅ (tool window) | ✅ (Macro Explorer) | ✅ (Macros dialog) | ✅ (Edit Macros dlg) | ◐ (just a console) | ✅ | ❌ | ❌ | ◐ (palette list) | ❌ | ◐ | ◐ |
| 26 | Right‑click context actions | ✅ | ✅ | ✅ | ◐ | ◐ | ◐ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| 27 | Guided trigger editor / autocomplete | ✅ (Manage Triggers) | ❌ | ❌ | — | ❌ | ❌ | — | — | — | — | — | — |
| 28 | Built‑in sample gallery (15+ ready‑to‑use templates) | ✅ (`Samples` group, embedded) | ❌ | ◐ (Office templates exist but not macro‑focused) | ❌ | ❌ | ◐ (example commands/extensions) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Distribution & lifecycle** ||||||||||||||
| 29 | Built‑in (no install) | ❌ (Marketplace) | ✅ | ✅ | ✅ | ❌ (plugin) | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ✅ |
| 30 | Actively maintained (2025) | ✅ | ❌ (removed 2012) | ◐ (Office Scripts deprecating VBA) | ✅ | ◐ | ✅ | ✅ | ◐ (`geddski` stale; `multi‑command` active) | ✅ | ✅ | ✅ | ✅ |

> **Correction note:** the original draft of this analysis omitted the existing built‑in sample gallery. Verified in `src/Macros/Samples/SampleTemplateProvider.cs` and `src/Macros/ToolWindows/SampleGroupViewModel.cs` — the extension already ships 15 embedded templates surfaced as a *Samples* group in the tool window. Row 28 was added to reflect this; it is yet another differentiator no competitor matches.

---

## Where Macros for VS uniquely wins

These rows are the moat — no other competitor has all of them, and most don't have *any* of them:

- **Trust gate for shared macros (row 24).** Truly unique.
- **Per‑repo, version‑controllable, team‑shared scope (rows 21‑23).** Visual Commander explicitly does *not* have this; JetBrains macros are global.
- **Trigger filters with glob/value syntax (row 16).** Multi‑command's `languages` is the closest, and far weaker.
- **Re‑entrance guard + auto‑disable + kill switch (rows 17‑19).** No competitor has even one.
- **Step aggregation, recording cap, guided trigger editor (rows 4, 5, 27).**
- **Roslyn‑powered IntelliSense via the `#load` shim trick (row 9).**
- **In‑product sample gallery (row 28) — already shipping.**

The combination "**recorded + scriptable + triggered + team‑shared + trust‑gated + sample‑seeded**" is unique. Visual Commander has the first three; nobody else has the rest.

---

## Where competitors are ahead

1. **Debugger (row 11).** VBA's VBE has been the gold standard for 25+ years; `EXCEEDSYSTEM.vscode-macros` brags about VS Code's debugger working on `.js` macro files. Macros has no debugger story.
2. **External library / NuGet references in scripts.** `EXCEEDSYSTEM` ships `require()` for any Node module; JB Scripting Console has the full IntelliJ classpath. The README explicitly steers users away ("consider building a VS extension instead").
3. **Recording fidelity for extension‑provided commands.** `kb-macro`'s `kb-macro.wrap` mechanism is a clever way to record commands from other extensions reliably.
4. **Built‑in vs. install (row 29).** Vim, Emacs, Sublime, Notepad++, JB built‑in macros, and old VS Macros all shipped with the host. Adoption tax.

---

## Recommendations — prioritized

Tagged **impact × effort**.

### Quick wins — high impact, low effort

- **🟢 Improve sample gallery discoverability** ([H × L]). The gallery exists but its tool‑window group defaults to **collapsed** (`SampleGroupViewModel(_, isExpanded: false)`) and there's no first‑run nudge. Options:
  - Auto‑expand the *Samples* group on first run and only first run, then remember the user's collapsed/expanded preference.
  - Surface a *Welcome* InfoBar on first solution open: "Macros ships with 15 ready‑to‑use templates — open the tool window to browse." Dismissible.
  - Show a `(15)` count next to the *Samples* header so users know there's content there.
- **🟢 Add `RunMacroAsync("name")` helper** ([H × L]). Lets users compose macros — directly answers `multi-command`'s use case. Trivial against the existing store; gates re‑entrance with the existing depth cap.
- **🟢 Add `InsertSnippetAsync("prefix")` helper** ([M × L]). `geddski.macros` highlights snippet integration as a key use case. One line on top of `Edit.InsertSnippet`.
- **🟢 Structured logging helper `Log.Info/Warn/Error`** ([M × L]). Writes to the Macros Output pane channel and surfaces in the Error List. Fills the "no debugger" gap a bit; competitors rely on `print()` / `console.log`.
- **🟢 Marketplace listing copy that names the moat** ([H × L]). Current README is feature‑complete but generic. Worth a "vs Visual Commander / vs Old VS Macros / vs VS Code macro extensions" mini‑section. The fact that 15 templates ship in‑box is a great hook for the Marketplace screenshot strip.

### Strategic — high impact, medium effort

- **🟡 NuGet/`#r` package references in macro scripts** ([H × M]). Roslyn `ScriptOptions.WithReferences` + a NuGet resolver — feasible. Removes the largest "you must build a real extension instead" off‑ramp. Pair with a per‑solution allow‑list to keep the trust model intact.
- **🟡 Settings Sync integration for global macros** ([H × M]). VS, JetBrains, and VS Code all sync settings; Macros' global scope is currently pinned to one machine unless the user manually points `GlobalMacrosFolder` at OneDrive. Hook into `Microsoft.VisualStudio.Settings` sync surface.
- **🟡 Time‑based / interval triggers** (`@trigger Every 30s`) ([M × M]). No competitor has scheduled execution inside the IDE. Combined with the existing kill switch + auto‑disable, this is safe and unlocks "auto‑save", "auto‑pull", "ping‑me‑every‑15‑minutes" use cases.
- **🟡 First‑class "make my extension recordable" guidance** ([M × M]). Borrow the `kb-macro.wrap` idea: document and possibly provide an attribute/registration so 3rd‑party VS extensions can opt their commands into reliable recording.
- **🟡 Recording in non‑editor surfaces** (Solution Explorer, etc.) ([M × M]). Solution Explorer commands route through `IOleCommandTarget` already and would be a meaningful expansion.

### Bold bets — high impact, high effort

- **🔵 Macro debugger** ([H × H]). Hard but feasible: the Roslyn scripting engine targets a synthesized assembly that VS's managed debugger can step into via `Debugger.Launch()` + symbol emission. Even a "set a breakpoint by line and hit F5 from the tool window" minimal version would be a massive differentiator.
- **🔵 AI‑assisted macro generation** ([H × H]). "Generate a macro that … " powered by Copilot Chat → emits a `.csx` you can review before saving. Plays directly to the *editable C# script* differentiator. No competitor is close.
- **🔵 In‑IDE macro store / share bundle** ([M × H]). A curated public catalog (gist‑backed?) of trustworthy macros with the trust gate as the safety net. Risky to operate, high reward.

### Maintain / invest — preserve the moat

- **🟣 Keep the IntelliSense shim solid.**
- **🟣 Keep `.csx` text‑based and diff‑friendly.**
- **🟣 Document the security model loudly.** Trust gate + auto‑disable + kill switch + manual‑always‑allowed is genuinely best‑in‑class.
- **🟣 Keep the sample library current.** It's already shipping; the bar is to keep it modern as the helper API evolves.

### Lower priority / explicitly skip

- **⚫ Mouse / menu / dialog recording.** Every competitor has tried; every competitor has failed. Not worth chasing.
- **⚫ Cross‑IDE port (VS Code).** Different runtime, different toolkit, different user.

---

## TL;DR

Macros for VS is **already the most capable IDE‑macros product on the market** when you weight all 30 dimensions equally — its only feature‑parity peer is the long‑defunct VBA + old VS Macros combo. The two competitive holes that an existing VBA refugee or Visual Commander user *will* notice are **(1) no debugger** and **(2) no external package references**. Closing those, plus making the *existing* sample gallery more discoverable and an honest Marketplace listing that names the moat, would put it in a category of one.
