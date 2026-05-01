using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Macros.Commands;

/// <summary>
/// Thread-safe bi-directional cache that maps between command names ("File.Save") and
/// command identifiers (Guid + uint). Primed once at package load by enumerating
/// <c>DTE.Commands</c>; thereafter all lookups are lock-only (no COM calls).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PrimeAsync"/> is intentionally not unit-tested because it requires a live DTE
/// instance. All cache-manipulation and lookup logic is exercised via
/// <c>CommandNameCacheTests</c> using the <see cref="TryAddName"/> helper.
/// </para>
/// </remarks>
internal sealed class CommandNameCache
{
    private readonly Dictionary<string, (Guid group, uint id)> _nameToCommand
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid, uint), string> _commandToName = new();
    private readonly object _sync = new();

    /// <summary>Gets the singleton instance.</summary>
    public static CommandNameCache Instance { get; private set; } = new();

    /// <summary>
    /// Looks up the command identifier for a given name.
    /// </summary>
    /// <returns><see langword="true"/> if found; <see langword="false"/> otherwise.</returns>
    public bool TryGetCommand(string name, out Guid group, out uint id)
    {
        lock (_sync)
        {
            if (_nameToCommand.TryGetValue(name, out var entry))
            {
                group = entry.group;
                id = entry.id;
                return true;
            }
        }

        group = default;
        id = default;
        return false;
    }

    /// <summary>
    /// Looks up the command name for a given (group, id) pair.
    /// </summary>
    /// <returns><see langword="true"/> if found; <see langword="false"/> otherwise.</returns>
    public bool TryGetName((Guid, uint) cmd, out string name)
    {
        lock (_sync)
        {
            return _commandToName.TryGetValue(cmd, out name!);
        }
    }

    /// <summary>
    /// Returns the command name for a given (group, id) pair, or <see langword="null"/>
    /// if not present in the cache.
    /// </summary>
    public string? Lookup(Guid group, uint id)
    {
        lock (_sync)
        {
            if (_commandToName.TryGetValue((group, id), out var name)) return name;
            return null;
        }
    }

    /// <summary>
    /// Adds or updates a mapping for the given command. Used by <see cref="CommandObserver"/>
    /// to cache names discovered at runtime via DTE fallback.
    /// </summary>
    public void TryAddName(Guid group, uint id, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_sync)
        {
            var key = (group, id);
            _commandToName[key] = name;
            _nameToCommand[name] = (group, id);
        }
    }

    /// <summary>
    /// Enumerates <c>DTE.Commands</c> on the UI thread and populates both dictionaries.
    /// Must be called at most once per session; subsequent calls replace the cached data.
    /// </summary>
    /// <remarks>
    /// This method is fire-and-forget friendly: any COM exception thrown during enumeration
    /// is swallowed per-command so a single bad entry doesn't abort the whole priming pass.
    /// </remarks>
    public async Task PrimeAsync(EnvDTE80.DTE2 dte, JoinableTaskFactory jtf)
    {
        await jtf.SwitchToMainThreadAsync();

        var nameToCommand = new Dictionary<string, (Guid, uint)>(StringComparer.OrdinalIgnoreCase);
        var commandToName = new Dictionary<(Guid, uint), string>();

        foreach (EnvDTE.Command cmd in dte.Commands)
        {
            try
            {
                if (string.IsNullOrEmpty(cmd.Name)) continue;
                if (!Guid.TryParse(cmd.Guid, out var g)) continue;
                var key = (g, (uint)cmd.ID);
                commandToName[key] = cmd.Name;
                nameToCommand[cmd.Name] = key;
            }
            catch
            {
                // Some Command items throw on Name / Guid access — skip silently.
            }
        }

        lock (_sync)
        {
            _nameToCommand.Clear();
            _commandToName.Clear();
            foreach (var kvp in nameToCommand) _nameToCommand[kvp.Key] = kvp.Value;
            foreach (var kvp in commandToName) _commandToName[kvp.Key] = kvp.Value;
        }
    }
}
