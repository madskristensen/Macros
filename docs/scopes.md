# Scopes: Global and Repo Macros

Macros live in two scopes, each with different storage locations, availability, and version-control semantics.

## Global scope

**Storage:** `%APPDATA%\Macros\<name>.csx` per Windows user.

**Availability:** Your global macros are available in every solution you open on this machine.

**Use case:** Personal automation — your go-to refactorings, formatting rules, file templates, and workflows that you use across projects.

**Sharing:** Not version-controlled; stored in your user profile. To sync across machines, configure **Tools → Options → Macros → General → GlobalMacrosFolder** to a shared path (e.g., OneDrive, Synology, shared network drive).

## Repo scope

**Storage:** `<solution>\.vs\Macros\<name>.csx` (inside the `.vs` hidden folder).

**Availability:** Repo macros are available only in the solution where they live.

**Use case:** Team automation — build checks, code generation, IDE setup workflows, and refactorings specific to your codebase or team standards.

**Sharing:** Commit the `.vs/Macros/` folder to source control and teammates can use the same macros. By default `.vs/` is gitignored; add a targeted exception:

```gitignore
# Track shared macros, ignore the rest of .vs
!.vs/
.vs/*
!.vs/Macros/
```

## Shadowing

When a repo macro and a global macro have the same name, the **repo macro wins**. In the tool window, the global version is still listed under **Shadowed Global** (italic, dimmed) so you know it exists but is hidden by the repo version.

To resolve a collision, rename or delete the shadowed macro, or move the repo macro to a different scope via **Move to Global**.

## Composite store

Internally, the extension treats global and repo scopes as a composite store with **repo-wins** semantics. Operations like **List All Macros** combine both scopes, reporting shadowed entries separately so the tool window can render them distinctly.

## Moving between scopes

To move a macro from Global to Repo (or vice versa):

1. Right-click the macro in the tool window.
2. Choose **Move to Repo** (if currently Global and a solution is open) or **Move to Global** (if currently Repo).
3. If the target scope has a same-named macro, a dialog prompts to confirm the overwrite.

Alternatively, use **Save As** with a different scope choice.

## Viewing storage

To reveal where your macros are stored:

1. Right-click a macro → **Open Folder**.
2. File Explorer opens to the containing scope folder (`%APPDATA%\Macros` or `<solution>\.vs\Macros`).
