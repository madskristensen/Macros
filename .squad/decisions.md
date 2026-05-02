

### 2026-05-02: PromptAsync crosses the engine/VSIX boundary via a prompt service seam
**By:** Danny
**What:** Implemented macro-time prompting by keeping `Helpers.PromptAsync` in `Macros.Engine` and introducing an internal `IMacroPromptService` implemented in the WPF-enabled VSIX project. `MacroPlayer` now injects that service and a `JoinableTaskFactory` into `MacroGlobals`, and the VSIX supplies a themed `DialogWindow` prompt.
**Why:** `Macros.Engine` should stay reusable and free of direct WPF dialog dependencies, while runtime macros still need a real Visual Studio UI prompt. The service seam keeps that layering clean, makes the helper unit-testable, and avoids pulling new UI dependencies into the engine project.


### 2026-05-02: Macro Copilot skills stay in macro-authoring scope
**By:** Rusty
**What:** Added `.copilot/skills/` guidance for writing and debugging `.csx` macros, but deliberately kept the content in macro-authoring scope only. The new skills teach header format, triggers, DTE usage inside macros, Toolkit facade usage inside macros, and macro troubleshooting.
**Why:** This repo's Copilot guidance should help generate editable `.csx` automation scripts without drifting into VSIX/extensibility patterns, which belong in the separate vs-agent-plugins repo.

