# Macros for Visual Studio

Macros brings back macro support to Visual Studio — record keystrokes and commands,
write C# scripts that automate repetitive tasks, and trigger them on events like
builds, saves, or commands.

## Quick start

1. Press **Ctrl+Shift+R** to start recording
2. Perform actions in the editor (type, refactor, run commands)
3. Press **Ctrl+Shift+R** again to stop
4. Press **Ctrl+Shift+P** to play it back

Macros are saved as `.csx` (C# Script) files — you can edit them by hand, add logic,
and share them with your team via source control.

## Guides

| Guide | Description |
|-------|-------------|
| [Getting Started](getting-started.md) | Install, record your first macro, and play it back |
| [Writing Macros](csx-reference.md) | C# Script (.csx) API reference and helpers |
| [Recording](recording.md) | How the recorder captures keystrokes and commands |
| [Replaying](replaying.md) | Playback behavior, error handling, and undo |
| [Triggers](triggers.md) | Run macros automatically on events (build, save, commands) |
| [Scopes](scopes.md) | User vs. repo macros and folder layout |
| [Samples](macro-samples.md) | Built-in sample macros you can use as templates |
| [Security](security.md) | Trust gates, repo macros, and safe execution |
| [Troubleshooting](troubleshooting.md) | Common issues and fixes |

## Key concepts

- **Macros are just C# scripts.** Anything you can do in a `.csx` file works — loops,
  conditionals, async/await, NuGet types already loaded by VS.
- **Two scopes:** Global macros live in `%APPDATA%\Macros` (personal). Repo macros
  live in `<solution>\.vs\Macros` (team-shareable).
- **Triggers** let macros run automatically — on build completion, file save,
  or before/after any VS command.
- **IntelliSense works.** The extension generates a shim file so you get full
  autocomplete for `DTE`, `VS`, helpers, and trigger data.

## Contributing

See [Architecture](architecture.md) for internal design details.
