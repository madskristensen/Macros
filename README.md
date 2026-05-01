# Macros for Visual Studio

[![Build](https://github.com/MadsKristensen/Macros/actions/workflows/build.yml/badge.svg)](https://github.com/MadsKristensen/Macros/actions/workflows/build.yml)

> Record once. Repeat forever — manually or on cue.

A modern Visual Studio 2022 extension that brings back the **macro recorder**: capture text edits and command invocations, replay them on demand or auto-trigger on IDE events.

## Status

🚧 **Pre-alpha.** See [`plan.md`](https://github.com/MadsKristensen/Macros/blob/main/plan.md) (in session workspace) for the full design and implementation plan.

## Vision

- **Record** — text edits, caret moves, command invocations, refactorings.
- **Replay** — manually via toolbar / hotkey / palette / tool window, or automatically via `// @trigger` directives.
- **Edit as code** — every macro is an executable C# script (`.csx`) opened with full IntelliSense in VS itself.
- **Trigger on events** — bind macros to any event from `Community.VisualStudio.Toolkit.VS.Events` (build done, document saved, solution opened, debugger break, etc.).
- **Trigger on commands** — `// @trigger BeforeCommand File.Open` or `// @trigger AfterCommand Build.BuildSolution`. BeforeCommand can intercept and **cancel** the command.
- **Two scopes** — global macros in `%APPDATA%\Macros\macros\`, per-repo macros in `<solutionDir>\.macros\`. Repo wins on collision.
- **Per-solution trust gate** — repo macros with auto-triggers prompt the user once before being allowed to run.

## Hotkeys

| Key | Action |
|-----|--------|
| `Ctrl+Shift+R` | Start / stop recording |
| `Ctrl+Shift+P` | Play last recorded macro |

> ⚠️ `Ctrl+Shift+R` shadows `View.RefreshRemoteReferences` in default profiles. Rebind via Tools → Options → Keyboard if needed.

## Building

Requires Visual Studio 2022 17.10+ with the **Visual Studio extension development** workload.

```pwsh
git clone https://github.com/MadsKristensen/Macros.git
cd Macros
# Open Macros.slnx in Visual Studio 2022 and press F5 to launch the experimental hive.
```

CI uses MSBuild on `windows-latest` (no signing).

## License

MIT — see [`LICENSE`](LICENSE).
