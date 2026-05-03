# Security & Trust

## Why macros are code

Macros are stored as `.csx` C# scripts, not opaque binary blobs. This transparency is a feature — you can read, audit, version-control, and review them before they run. But it also means: **macros are code, and code can do anything the IDE can do.**

A malicious macro could:

- Delete files on disk.
- Modify your source code.
- Run build scripts with arbitrary arguments.
- Steal environment variables and secrets.

Treat macros like you treat any untrusted code: read before you trust.

## Trust gate

Repo macros are committed to source control. A fresh clone could ship macros that auto-run on solution open — potentially without your knowledge. The trust gate prevents that.

### The prompt

When you open a solution that contains repo macros with **non-`Manual` triggers**, you'll see a gold info bar:

> *"This solution contains N macro(s) with auto-run triggers. They are blocked until you trust this solution. [Review Macros] [Trust this solution] [Block] [Dismiss]"*

- **Review Macros** — Opens the tool window so you can inspect the macros before deciding.
- **Trust this solution** — Auto-triggers register immediately; remembered across restarts (per solution path).
- **Block this solution** — Auto-triggers stay disabled; remembered.
- **Dismiss** — Decide later. Triggers stay disabled until you act.

### Manual invocation is always allowed

Running a macro via **Play** (from tool window, hotkey, Quick Launch) is **not** blocked by the trust gate — it's treated as explicit consent. Only automatic triggers are gated.

### Trust persistence

Your trust decisions are stored in **Tools → Options → Macros → Trusted Solutions**. Two lists:

- **Trusted solutions** — solution paths whose repo macros are allowed to register triggers automatically.
- **Blocked solutions** — solution paths whose repo macro triggers are explicitly suppressed.

Edit them directly to revoke trust, unblock, or pre-seed trusted solution paths.

## Auto-disable on failure

To prevent a broken macro from repeatedly firing and annoying you:

- If a **triggered** macro fails 3 times in a row, it's automatically disabled.
- An InfoBar surfaces: *"Macro 'X' has been disabled after 3 consecutive failures. [View Details] [Re-enable]"*.
- Manual invocations never trigger auto-disable, even if they fail repeatedly.

To re-enable a disabled macro, click **Re-enable** in the InfoBar or right-click the macro in the tool window.

## Kill switch

The **Disable All Triggers** option (visible in the toolbar as *Toggle Triggers*) is a master kill-switch:

- When enabled, **no triggers fire** — not VS events, not BeforeCommand, not AfterCommand.
- Manual invocation still works (Play, Play Last, tool window Play button).
- Handy when recording a macro (so triggers don't interfere) or debugging a trigger issue.

Toggle via:

- Toolbar button **Toggle Triggers**.
- **Tools → Options → Macros → General → Disable all triggers** checkbox.

## File permissions and trust context

Macros run with the **full permissions of the logged-in user** — they have access to every file and network resource available to you. The IDE doesn't sandbox macro execution. This is by design (full automation power), but it means your trust decision matters.

**Best practice:** If cloning an unfamiliar or untrusted repository, open it in a dedicated VS instance or in a virtual machine until you've reviewed the repo macros.
