# Macros for Visual Studio — Overview

**Macros** is a modern Visual Studio 2022 extension that brings back the power of recorded automation. Record sequences of text edits, command invocations, and cursor movements — then replay them with a single keystroke or trigger them automatically on IDE events.

Every macro is stored as an editable **C# script** (`.csx`), not a black-box JSON file. This means you can read it in the editor, debug it, add logic (loops, conditionals, helper functions), and version-control it alongside your code. The Roslyn C# scripting host executes it, so no boilerplate — just your automation, plain and simple.

Macros live in two scopes: **Global** (per-user, always available) and **Repo** (per-solution, team-shared and version-controlled). Use the **trigger system** to bind macros to ~40 built-in VS events (`Build.SolutionBuildDone`, `Document.Saved`, etc.) or to before/after named commands.

A **trust gate** protects repo macros — auto-triggers require per-solution approval on first run. Stability features include **re-entrance protection**, **auto-disable after 3 failures**, and a **kill switch** to instantly halt all triggers.

Record with **Ctrl+Shift+R**, replay with **Ctrl+Shift+P**, and manage everything from the **Macros tool window**. Free, open-source (MIT).
