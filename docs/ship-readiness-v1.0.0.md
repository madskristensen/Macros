# Macros v1.0.0 — Ship-Readiness Audit

_Auditor: Linus (QA) · Date: ship-readiness pass · Repo: `MadsKristensen/Macros`_

## Verdict: ✅ READY FOR DOGFOOD

All automated gates green. The remaining two M5 items are explicitly human-only
(manual smoke run + Marketplace publish/tag) and are correctly left to Mads.

---

## Build

- Solution: `Macros.slnx`
- Configuration: **Release** (net48)
- `dotnet clean` + `dotnet build`: **succeeded**
- **Errors: 0**
- **Warnings: 4** — all benign:
  - 2× `NU1603` on `Microsoft.VisualStudio.Sdk.TestFramework.Xunit 17.11.8`
    requesting `Microsoft.VisualStudio.Interop 17.11.39607` /
    `Microsoft.VisualStudio.Shell.15.0 17.11.39607`; NuGet resolved
    `17.11.40262` instead. Test-only project, transitive, unavoidable until
    that test framework package republishes.
  - (Each NU1603 is reported once per restore graph, so the build prints them
    twice; same two warnings.)
- VSIX size: **1,254.67 KB (≈ 1.225 MB)** — `src\Macros\bin\Release\net48\Macros.vsix`

## Tests

- Project: `tests\Macros.Tests` (xUnit on net48)
- Result: **`Passed! - Failed: 0, Passed: 884, Skipped: 0, Total: 884, Duration: 8 s`**
- Coverage spans engine, storage, triggers (command + event), trust gate,
  tool window VMs, observers, error handling, onboarding, accessibility,
  and **9 performance benchmarks with SLA assertions** (P95 ≤ 5 ms warm
  invoke, etc.).

## VSIX

Inspected `src\Macros\bin\Release\net48\Macros.vsix` via `[System.IO.Compression.ZipFile]::OpenRead`.

Required entries — all **present**:

| Entry                    | Status | Size (bytes) |
| ------------------------ | :----: | -----------: |
| `extension.vsixmanifest` |   ✅    |        1,531 |
| `Macros.dll`             |   ✅    |      254,464 |
| `Macros.Engine.dll`      |   ✅    |      126,976 |
| `Macros.pkgdef`          |   ✅    |        5,212 |
| `LICENSE`                |   ✅    |        1,093 |

Bundled redistributables (expected): `Microsoft.CodeAnalysis.Scripting`,
`Microsoft.CodeAnalysis.CSharp.Scripting` + satellite resources,
`Community.VisualStudio.Toolkit.dll`, `System.Text.Encoding.CodePages.dll`.
`Macros.Engine.pdb` is included; `Macros.pdb` is intentionally not.

### `extension.vsixmanifest` — required fields

| Field                                       | Value                                                                                                               | OK  |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- | :-: |
| `Identity@Id`                               | `Macros.MadsKristensen.d13e532a-bd43-40df-ae9f-8d05e54138c7` (matches expected `<Name>.<Publisher>.<GUID>` pattern) |  ✅  |
| `Identity@Version`                          | `1.0.0`                                                                                                             |  ✅  |
| `Identity@Publisher`                        | `Mads Kristensen`                                                                                                   |  ✅  |
| `<DisplayName>`                             | `Macros for Visual Studio`                                                                                          |  ✅  |
| `<Description>`                             | "Record text edits and IDE actions. Replay instantly with hotkeys or trigger on events…"                            |  ✅  |
| `<MoreInfo>`                                | `https://github.com/MadsKristensen/Macros`                                                                          |  ✅  |
| `<License>`                                 | `LICENSE`                                                                                                           |  ✅  |
| `<Tags>`                                    | `macros, automation, recording, replay, productivity, csharp, scripting, triggers`                                  |  ✅  |
| `InstallationTarget`                        | `Microsoft.VisualStudio.Community [17.0,18.0)` / `amd64`                                                            |  ✅  |
| Asset `Microsoft.VisualStudio.VsPackage`    | `Macros.pkgdef`                                                                                                     |  ✅  |
| Asset `Microsoft.VisualStudio.MefComponent` | `Macros.dll`                                                                                                        |  ✅  |

## Docs

| File                   | Lines |  Bytes | First heading                                           |                         Status                          |
| ---------------------- | ----: | -----: | ------------------------------------------------------- | :-----------------------------------------------------: |
| `README.md`            |   429 | 24,135 | `# Macros for Visual Studio`                            |                     ✅ (target 200+)                     |
| `CHANGELOG.md`         |   109 |  7,539 | `# Changelog`                                           |                     ✅ (target 50+)                      |
| `LICENSE`              |    21 |  1,093 | _(MIT, no markdown heading)_                            |                        ✅ present                        |
| `docs\manual-smoke.md` |   232 |  7,908 | `# Macros — Manual Smoke Test Checklist (v1.0.0)`       | ✅ — 54 numbered items across 11 sections (well past 15) |
| `docs\marketplace.md`  |   153 |  7,384 | `# Macros for Visual Studio 2022 — Marketplace Listing` |                     ✅ (target 50+)                      |
| `docs\overview.md`     |    11 |  1,333 | `# Macros for Visual Studio — Overview`                 |                        ✅ present                        |
| `docs\performance.md`  |   140 |  7,032 | `# Macros Extension — Performance Baselines (v1.0)`     |                        ✅ present                        |

## Code quality

Scan over `src\**\*.cs` (production only):

| Check                                |  Count | Notes                                                                                                                                                                                                                                                                                                                                                                                  |
| ------------------------------------ | -----: | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `// FIXME` comments                  |  **0** | clean                                                                                                                                                                                                                                                                                                                                                                                  |
| `// HACK` comments                   |  **0** | clean                                                                                                                                                                                                                                                                                                                                                                                  |
| `// TODO(integration):` markers      |  **0** | clean — no Linus-integration leftovers                                                                                                                                                                                                                                                                                                                                                 |
| `Debug.WriteLine` in production code | **11** | acceptable: 9 in `Macros.Engine\Triggers\MacroEventBus.cs` (2 are XML doc references, 7 are intentional `catch`-arm diagnostics for handler/queue failures), 1 in `MacroEventBus.cs:276/298` listener guards, 1 in `TriggerWorkQueue.cs:92`. All are deliberate "swallow + trace" guards documented in the bus's XML summary; they keep VS responsive when third-party handlers throw. |
| Hardcoded `C:\…` literals in `src\`  |  **0** | clean                                                                                                                                                                                                                                                                                                                                                                                  |

## Git state

- **Commits on `master`:** 8
- Most recent (HEAD): `cf91d44 fix: LibraryChanged_TriggersReload deadlock; correct RepoMacroStore + FileSystemMacroStore docs`
- **Working tree:** **not clean** — **49 uncommitted entries** (22 modified, 27 untracked).
  This is **expected**: autopilot waves wrote files without committing.
  Mads's call whether to consolidate into a single `release: v1.0.0` commit
  before tagging.

Notable untracked artefacts that should land in the release commit:
`CHANGELOG.md`, `docs/` (entire folder including `manual-smoke.md`,
`marketplace.md`, `overview.md`, `performance.md`, and this audit report),
plus the M3/M4/M5 source additions (Manage Triggers dialog, trust gate,
event-trigger dispatcher, onboarding info bar, accessibility helpers, and
their tests).

## Outstanding items (require Mads)

1. **Manual smoke** — walk `docs\manual-smoke.md` (54 items across 11
   sections, ~30 min in VS Experimental Instance).
2. **Tag `v1.0.0` + publish to Marketplace** — after consolidating the
   uncommitted changes (recommended single `release: v1.0.0` commit) and
   verifying the smoke pass.

## Known limitations (carried to v1.1)

- Watcher path-change restart on solution change
  (`m5-watcher-restart-on-solution-change`).
- `SaveCurrent` fire-and-forget UI surfacing
  (background save without progress / error toast).
- Yellow status-bar indicator at the recording cap.

---

**Bottom line:** v1.0.0 is functionally and structurally ready. Build is
clean, all 884 tests pass, the VSIX is well-formed with every required
manifest field and asset, docs are complete, and there are no FIXME/HACK
or `TODO(integration)` markers in production code. Ship it after the manual
smoke pass.
