using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Community.VisualStudio.Toolkit;

namespace Macros.Options;

/// <summary>
/// Persisted user options for the Macros extension. Values are written to the user's settings store
/// by the Community Toolkit's <see cref="BaseOptionModel{T}"/> infrastructure.
/// </summary>
/// <remarks>
/// The Tools → Options page registration (<c>[ProvideOptionPage]</c>) lands later in
/// <c>m3-options-page</c>; this class only models the persisted settings. Properties marked
/// <see cref="BrowsableAttribute">[Browsable(false)]</see> are internal flags that should not be
/// surfaced even once the options page exists.
/// </remarks>
internal sealed class MacrosOptions : BaseOptionModel<MacrosOptions>
{
    [Category("General")]
    [DisplayName("First run")]
    [Description("Internal flag — true until the user has seen the first-run InfoBar.")]
    [Browsable(false)]
    public bool FirstRun { get; set; } = true;

    [Category("Recording")]
    [DisplayName("Maximum recording steps")]
    [Description("Stop recording automatically after this many steps to prevent runaway captures.")]
    public int MaxRecordingSteps { get; set; } = 5000;

    [Category("Storage")]
    [DisplayName("Global macros folder")]
    [Description("Path where global macros are stored. Default: %APPDATA%\\Macros\\macros")]
    public string GlobalMacrosFolder { get; set; } = "";

    [Category("Storage")]
    [DisplayName("Repo macros folder name")]
    [Description("Subfolder name under <solution>\\.vs\\ for repo-scoped macros. Default: Macros")]
    public string RepoMacrosFolderName { get; set; } = "Macros";

    [Category("Replay")]
    [DisplayName("Confirm before overwrite")]
    [Description("When recording over an existing named macro, prompt for confirmation.")]
    public bool ConfirmBeforeOverwrite { get; set; } = true;

    [Category("Triggers")]
    [DisplayName("Disable all triggers")]
    [Description("Master kill switch for event-triggered and command-triggered macros (M4 feature). Manual invocation is unaffected.")]
    public bool DisableAllTriggers { get; set; } = false;

    [Category("Triggers")]
    [DisplayName("BeforeCommand timeout (ms)")]
    [Description("Maximum time a BeforeCommand-triggered macro may take before VS forwards the original command (M4 feature). Lower = safer; higher = more powerful macros.")]
    public int BeforeCommandTimeoutMs { get; set; } = 2000;

    [Category("Triggers")]
    [DisplayName("Trusted solutions")]
    [Description("Semicolon-separated absolute paths to .sln files. Triggers from repo macros only auto-run for trusted solutions.")]
    [Browsable(false)]
    public string TrustedSolutions { get; set; } = "";

    [Category("Triggers")]
    [DisplayName("Blocked solutions")]
    [Description("Solutions explicitly blocked from running any triggers.")]
    [Browsable(false)]
    public string BlockedSolutions { get; set; } = "";

    // ── Trust helpers ──────────────────────────────────────────────────────

    public bool IsSolutionTrusted(string? solutionPath)
    {
        if (string.IsNullOrEmpty(solutionPath)) return false;
        return GetTrustedSet().Contains(Canonicalize(solutionPath!));
    }

    public bool IsSolutionBlocked(string? solutionPath)
    {
        if (string.IsNullOrEmpty(solutionPath)) return false;
        return GetBlockedSet().Contains(Canonicalize(solutionPath!));
    }

    public void TrustSolution(string solutionPath)
    {
        var trusted = GetTrustedSet();
        var blocked = GetBlockedSet();
        var key = Canonicalize(solutionPath);
        trusted.Add(key);
        blocked.Remove(key);
        TrustedSolutions = string.Join(";", trusted);
        BlockedSolutions = string.Join(";", blocked);
    }

    public void BlockSolution(string solutionPath)
    {
        var trusted = GetTrustedSet();
        var blocked = GetBlockedSet();
        var key = Canonicalize(solutionPath);
        blocked.Add(key);
        trusted.Remove(key);
        BlockedSolutions = string.Join(";", blocked);
        TrustedSolutions = string.Join(";", trusted);
    }

    public void RevokeTrust(string solutionPath)
    {
        var set = GetTrustedSet();
        if (set.Remove(Canonicalize(solutionPath)))
            TrustedSolutions = string.Join(";", set);
    }

    private HashSet<string> GetTrustedSet() =>
        new HashSet<string>(
            (TrustedSolutions ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    private HashSet<string> GetBlockedSet() =>
        new HashSet<string>(
            (BlockedSolutions ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    private static string Canonicalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
