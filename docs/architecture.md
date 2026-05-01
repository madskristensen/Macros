# Architecture Overview

For contributors and those curious about how the extension works. The codebase is two assemblies:

```
src/
├── Macros/              ← VSIX shell (commands, tool window, options, .vsct, dialogs)
└── Macros.Engine/       ← Pure engine (recorder, codegen, storage, triggers, player)
tests/
├── Macros.Tests/                 ← xUnit unit tests (engine + VSIX-side, ~800 tests)
└── Macros.IntegrationTests/      ← VS Sdk.TestFramework tests (events, lifecycle)
```

## Recording

- **`CommandObserver`** registers as an `IOleCommandTarget` priority command target via `IVsRegisterPriorityCommandTarget`. Every command dispatch passes through; we resolve the GUID/ID to a string name via `DTE.Commands` (with a skip list for `Macros.*` and noisy `VSStd97` chatter).
- **`TextEditObserver`** is an MEF `IWpfTextViewCreationListener` that subscribes to `ITextBuffer.Changed` per view.
- **`StepAggregator`** debounces consecutive single-character typings (500ms gap) into batched `Type("…")` calls so generated code stays readable.
- **`ReplayGuard`** (an `AsyncLocal<int>`) suppresses both observers while a script is running, preventing record-while-replaying loops.

## Storage

- **`IMacroStore`** abstracts the file-system. `GlobalMacroStore` and `RepoMacroStore` are scope-specific implementations.
- **`CompositeMacroStore`** combines both with **repo-wins** semantics on name collision; `ListAllAsync` reports the shadowed global so the tool window can render it dimmed.
- All writes are atomic (`tmp + rename`). `FileSystemWatcher` per scope (200ms debounce + 500ms self-write suppression) drives live tool-window refresh.

## Replay

- **`MacroPlayer`** uses Roslyn's `CSharpScript.RunAsync<object>` against `MacroGlobals { DTE, VS, Context, Trigger? }`.
- **`ScriptCompilationCache`** keys compiled `Script<object>` instances by SHA-256 of source, LRU eviction at ~50 entries — re-running a macro is essentially zero-cost after the first execution.
- Compilation runs on the threadpool; execution switches to the UI thread via `JoinableTaskFactory`.
- Errors are surfaced through a unified renderer: Output pane entry + Error List items with `.csx` line numbers + an info bar with a *View Output* link.

## Trigger system

- **`TriggerDirectiveParser`** parses `// @trigger …` headers into `TriggerBinding { Kind, Name, Filters }`.
- **`MacroEventBus`** lazily reflects over `Community.VisualStudio.Toolkit.VS.Events`, builds delegates via `System.Linq.Expressions`, refcounts subscriptions, and extracts payloads.
- **`CommandTriggerDispatcher`** is hooked from `CommandObserver`: `BeforeCommand` runs synchronously with a timeout; `AfterCommand` is enqueued.
- **`IMacroTriggerRegistry`** indexes all bindings, exposes a hot `RegisteredBeforeCommandNames` `HashSet<string>` so the command path is O(1) per dispatch when nothing's bound, and re-checks the kill switch on every query.
- **`TriggerReentranceGuard`** (per-key `AsyncLocal`) caps recursion at depth 3.
- **`MacroFailureTracker`** auto-disables a macro after 3 consecutive failures and raises `MacroAutoDisabled` for the UI to surface.

## Trust and lifecycle

- **`SolutionContextTracker`** subscribes to `VS.Events.SolutionEvents` open/close, tracks the current repo store path, and raises `SolutionChanged`.
- **`TrustGateInfoBar`** + **`TrustGateLogic`** consult `MacrosOptions.IsSolutionTrusted` / `IsSolutionBlocked` on solution-open and decide whether to show the gold-bar prompt.

## Requirements

- **Visual Studio 2022** — version **17.10** or later.
- **Windows** — VS 2022 is Windows-only; this extension is too.
- **Visual Studio extension development** workload (only required to *build* from source — not to *use* the extension).

The VSIX targets `.NET Framework 4.8`, which ships with VS itself.

## Building from source

```pwsh
git clone https://github.com/MadsKristensen/Macros.git
cd Macros
dotnet build Macros.slnx --configuration Release
```

The build produces `src/Macros/bin/Release/net48/Macros.vsix`. Open `Macros.slnx` in Visual Studio 2022 and press **F5** to launch the experimental hive with the extension installed.

To run the test suite:

```pwsh
dotnet test tests/Macros.Tests/Macros.Tests.csproj --configuration Release
```

CI runs the same commands on `windows-latest` (see [`.github/workflows/build.yml`](https://github.com/MadsKristensen/Macros/blob/main/.github/workflows/build.yml)) and uploads the VSIX as an artifact on every push.

> 💡 The project uses the new SDK-style VSIX format. `dotnet build` works end-to-end — no `msbuild.exe` setup needed.
