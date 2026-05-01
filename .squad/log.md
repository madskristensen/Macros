# Squad Session Log

## 2026-04-30

### Round 3 — Triggers + Scope design + drop signing

**Participants:** Mads (directives), Danny-3 (engine extension v2), Rusty-1 (UX extension), Scribe (session record-keeper)

**Mads's directives:**
- Drop code signing from v1 scope; publish to Marketplace unsigned
- Add event-triggered macros: Macros auto-run when VS events fire (VS.Events surface via Community Toolkit)
- Add BeforeCommand/AfterCommand triggers: macros intercept named VS commands (e.g., `File.Open`, `Build.BuildSolution`); BeforeCommand can cancel
- Add global vs repo macro scopes with per-solution trust gate (InfoBar prompt on first repo-macro encounter)

**Agent outputs:**
- `danny-triggers-and-scope.md` (16 sections, ~46KB) — v2 engine architecture spec (locked)
- `rusty-triggers-and-scope.md` (13 sections, ~57KB) — UX extension spec (locked)
- `plan.md` v3 (M4 Triggers/Scope added; Polish renamed to M5; signing dropped; 4 new locked decisions + 5 new open questions)

**Planning state:**
- SQL todos: 75 todos / 152 deps across M1-M5
- M4 scope: 28 new todos for triggers/scope work
- Dependencies cascading into M5 review and M6 scope (not yet enumerated)

**Summary:** v3 plan locked: Macros now records, replays, AND auto-runs on VS events / command intercept; Global + Repo scope with per-solution trust gate; signing dropped; 28 new M4 todos.
