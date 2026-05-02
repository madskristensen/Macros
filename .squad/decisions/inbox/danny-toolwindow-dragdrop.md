### 2026-05-02T16:33:39-07:00
**By:** Danny
**What:** The Macros tool window now handles drag-and-drop through view-model operations: dragging a saved macro between Repo and Global performs a move, while dragging a sample into Repo or Global instantiates a new macro file in the target scope. The Samples expander header is also kept rendered so the gallery stays discoverable at startup.
**Why:** Routing the file operations through the view-model keeps the code-behind thin and reuses existing storage semantics for move/copy behavior. Keeping the Samples header visible avoids a startup-only WPF visibility failure that made the section disappear in the deployed tool window.
