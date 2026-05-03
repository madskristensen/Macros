

### 2026-05-02: PromptAsync crosses the engine/VSIX boundary via a prompt service seam
**By:** Danny
**What:** Implemented macro-time prompting by keeping `Helpers.PromptAsync` in `Macros.Engine` and introducing an internal `IMacroPromptService` implemented in the WPF-enabled VSIX project. `MacroPlayer` now injects that service and a `JoinableTaskFactory` into `MacroGlobals`, and the VSIX supplies a themed `DialogWindow` prompt.
**Why:** `Macros.Engine` should stay reusable and free of direct WPF dialog dependencies, while runtime macros still need a real Visual Studio UI prompt. The service seam keeps that layering clean, makes the helper unit-testable, and avoids pulling new UI dependencies into the engine project.


### 2026-05-02: Macro Copilot skills stay in macro-authoring scope
**By:** Rusty
**What:** Added `.copilot/skills/` guidance for writing and debugging `.csx` macros, but deliberately kept the content in macro-authoring scope only. The new skills teach header format, triggers, DTE usage inside macros, Toolkit facade usage inside macros, and macro troubleshooting.
**Why:** This repo's Copilot guidance should help generate editable `.csx` automation scripts without drifting into VSIX/extensibility patterns, which belong in the separate vs-agent-plugins repo.


### 2026-05-02T16:33:39-07:00: Tool window drag-and-drop routing via view-model
**By:** Danny
**What:** The Macros tool window now handles drag-and-drop through view-model operations: dragging a saved macro between Repo and Global performs a move, while dragging a sample into Repo or Global instantiates a new macro file in the target scope. The Samples expander header is also kept rendered so the gallery stays discoverable at startup.
**Why:** Routing the file operations through the view-model keeps the code-behind thin and reuses existing storage semantics for move/copy behavior. Keeping the Samples header visible avoids a startup-only WPF visibility failure that made the section disappear in the deployed tool window.


### 2026-05-02T17:25:53-07:00: User directive — no git push without explicit approval
**By:** Mads Kristensen (via Copilot)
**What:** Never git push unless Mads explicitly says it's OK in the current session. If he hasn't said so, do not push.
**Why:** User request — captured for team memory.


### 2026-05-03T08:20:48-07:00: Event bus pre-warms UI-thread category cache
**By:** Danny
**What:** `MacroEventBus` now caches resolved toolkit event-category instances by `Type`, stores the resolved instance on each bus entry for detach/reattach, and is pre-warmed from `MacrosPackage` on the UI thread.
**Why:** Toolkit category constructors (for example build and solution events) can touch COM services that require the UI thread. Pre-warming and reusing the same category instances keeps background-thread registry rebuilds from silently failing to attach VS event handlers.


### 2026-05-03T08:20:48-07:00: Trust prompt is just-in-time on first auto-run
**By:** Rusty
**What:** Repo macro trust is now decided the first time an automatic repo trigger tries to run, not proactively on solution open. `TrustGate` stays the pure allow/deny helper, while `TrustPromptService` owns the modal Visual Studio message box, per-solution in-flight prompt deduplication, and persistence through `MacrosOptions.TrustSolution()` / `BlockSolution()`.
**Why:** This keeps trust UX on the exact execution path that needs a decision and removes the extra InfoBar/options-page surface area. Sharing one pending prompt per solution prevents simultaneous command/event triggers from stacking multiple dialogs while still making every blocked trigger wait for the same answer.


### 2026-05-03T08:20:48-07:00: User directive — remove trust options page, use just-in-time modal
**By:** Mads (via Copilot)
**What:** Ditch the TrustedSolutionsPage options page entirely. Instead, the first time an automated repo macro trigger fires, pop a message box asking for permission. Store the trust decision in settings but remove the dedicated options page UI.
**Why:** User request — the current trust InfoBar + options page UX doesn't work well. Just-in-time consent at the moment of first auto-run is clearer.

