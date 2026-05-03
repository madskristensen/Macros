using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Macros.Engine;
using Macros.Engine.Player;
using Macros.Engine.Storage;
using Macros.Errors;
using Macros.Mvvm;
using Macros.Samples;

namespace Macros.ToolWindows;

/// <summary>
/// View-model for a single row in the macro list. Wraps a <see cref="MacroEntry"/> and
/// exposes the per-row commands the XAML binds to.
/// </summary>
/// <remarks>
/// The <see cref="PlayCommand"/> is wired in M3; <see cref="EditCommand"/>,
/// <see cref="RenameCommand"/> and <see cref="DeleteCommand"/> are placeholder
/// implementations that raise no-op execute handlers. The <c>m3-context-menu</c> wave
/// replaces those handlers with their real flows; until then they remain bound so the
/// XAML can be authored once.
/// </remarks>
public sealed class MacroItemViewModel : INotifyPropertyChanged
{
    private readonly IMacroService? _service;
    private readonly Func<MacroPlayResult, string, Task> _errorRenderer;
    private bool _canInvoke = true;
    private bool _isShadowed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacroItemViewModel"/> class.
    /// </summary>
    /// <param name="descriptor">The on-disk metadata snapshot for this macro.</param>
    /// <param name="service">
    /// The macro engine. May be <see langword="null"/> in pure design-time scenarios; when
    /// <see langword="null"/>, <see cref="PlayCommand"/> becomes a no-op.
    /// </param>
    /// <param name="errorRenderer">
    /// Optional override for the error-surfacing call invoked when
    /// <see cref="IMacroService.PlayByNameAsync"/> returns a failed result. Defaults to
    /// <see cref="MacroErrorRenderer.RenderAsync"/>. Supply a stub in unit tests to avoid
    /// touching VS shell services (Output pane, InfoBar, Error List).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public MacroItemViewModel(MacroEntry descriptor, IMacroService? service,
        Func<MacroPlayResult, string, Task>? errorRenderer = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _service = service;
        _errorRenderer = errorRenderer ?? MacroErrorRenderer.RenderAsync;

        var play = new RelayCommand(_ => InvokePlay(), _ => CanInvoke && service is not null);
        PlayCommand = play;
        _playCommand = play;

        // Stub commands — wired in m3-context-menu. They stay bound so the XAML doesn't need
        // null-checks per-row; CanExecute returns false until the wave wires real handlers.
        EditCommand = new RelayCommand(_ => { }, _ => false);
        RenameCommand = new RelayCommand(_ => { }, _ => false);
        DeleteCommand = new RelayCommand(_ => { }, _ => false);
    }

    private readonly RelayCommand _playCommand;

    /// <summary>Gets the underlying file-system metadata snapshot for this macro.</summary>
    public MacroEntry Descriptor { get; }

    /// <summary>Gets the macro name (file stem, no extension) — primary row text.</summary>
    public string Name => Descriptor.Name;

    /// <summary>Gets the scope this macro lives in. Used by the <see cref="PlayCommand"/>.</summary>
    public MacroScope Scope => Descriptor.Scope;

    /// <summary>
    /// Gets a short human-readable last-modified timestamp ("just now", "5m ago", "2 days ago",
    /// or absolute date) for the secondary row text.
    /// </summary>
    public string LastModifiedDisplay => FormatRelative(Descriptor.Modified, DateTimeOffset.UtcNow);

    /// <summary>
    /// Gets a short human-readable file size for the tertiary row text (e.g. <c>"1.2 KB"</c>).
    /// </summary>
    public string SizeDisplay => FormatBytes(Descriptor.SizeBytes);

    /// <summary>Gets the macro's recorded step count (from <see cref="MacroEntry.StepCount"/>).</summary>
    public int StepCount => Descriptor.StepCount;

    /// <summary>Gets the text shown in the grid's "Steps" column.</summary>
    public string StepCountDisplay => IsSample ? string.Empty : $"{StepCount} steps";

    /// <summary>
    /// Gets a compact, single-line summary of this macro's triggers, suitable for the
    /// "Triggers" column. Sample rows show their description instead.
    /// </summary>
    public string TriggersSummary => IsSample
        ? (SampleDescription ?? string.Empty)
        : TriggerSummaryFormatter.Summary(Descriptor.Triggers);

    /// <summary>
    /// Gets the multi-line trigger detail string, suitable for the "Triggers" column tooltip.
    /// Sample rows show their description instead.
    /// </summary>
    public string TriggersDetail => IsSample
        ? (SampleDescription ?? string.Empty)
        : TriggerSummaryFormatter.Detail(Descriptor.Triggers);

    /// <summary>
    /// Gets a slightly terser variant of <see cref="LastModifiedDisplay"/> tailored for the
    /// grid's "Modified" column ("just now" / "5m" / "2h" / "yesterday" / "3d" / yyyy-MM-dd).
    /// </summary>
    public string ModifiedRelative => FormatModifiedRelative(Descriptor.Modified, DateTimeOffset.UtcNow);

    /// <summary>
    /// Gets the compound screen-reader label for the row. Concatenates the macro name, the
    /// triggers summary, and the relative modified timestamp via
    /// <see cref="AccessibilityHelpers.FormatMacroAutomationName"/> so Narrator/JAWS announce
    /// "Macro Greeting, Manual, modified 5m" instead of just "Greeting".
    /// </summary>
    public string AutomationName => IsSample
        ? $"Sample macro {Name}, {SampleTemplate?.Description ?? "read only sample"}"
        : AccessibilityHelpers.FormatMacroAutomationName(Descriptor, TriggersSummary, ModifiedRelative);

    /// <summary>Gets or sets whether this item represents a read-only sample template.</summary>
    public bool IsSample { get; init; }

    /// <summary>Gets the associated sample template, if this is a sample item.</summary>
    public SampleTemplate? SampleTemplate { get; init; }

    /// <summary>Gets the sample description shown in the tool window for sample rows.</summary>
    public string? SampleDescription => SampleTemplate?.Description;

    /// <summary>
    /// Gets the group label this row belongs to in the tool window's grouped ListView. The
    /// XAML's <c>PropertyGroupDescription</c> binds to this property, so it is plain text and
    /// identical to the corresponding <see cref="MacroGroupViewModel.Header"/>.
    /// </summary>
    /// <remarks>
    /// Repo entries are <c>"Repo"</c>; sample entries are <c>"Samples"</c>; non-shadowed
    /// global entries are <c>"Global"</c>; shadowed global entries (a global macro of the
    /// same name is overridden by a repo macro) are <c>"Shadowed Global Macros"</c>.
    /// </remarks>
    public string GroupName => IsSample
        ? "Samples"
        : (Scope == MacroScope.Repo ? "Repo" : (IsShadowed ? "Shadowed Global Macros" : "Global"));

    /// <summary>
    /// Gets or sets a value indicating whether this row represents a global macro that is
    /// shadowed by a repo macro of the same name. Drives the muted/italic/strikethrough
    /// styling in the "Shadowed Global Macros" group and changes <see cref="GroupName"/>.
    /// </summary>
    public bool IsShadowed
    {
        get => _isShadowed;
        set
        {
            if (_isShadowed == value)
            {
                return;
            }

            _isShadowed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupName));
            OnPropertyChanged(nameof(ShadowedTooltip));
            OnPropertyChanged(nameof(ItemToolTip));
        }
    }

    /// <summary>
    /// Gets the tooltip text that explains why a row is rendered with shadowed styling.
    /// <see langword="null"/> when <see cref="IsShadowed"/> is <see langword="false"/>.
    /// </summary>
    public string? ShadowedTooltip => IsShadowed
        ? "Shadowed by repo macro of the same name."
        : null;

    /// <summary>Gets the per-row tooltip shown in the Name column.</summary>
    public string? ItemToolTip => IsSample ? SampleTemplate?.Description : ShadowedTooltip;

    /// <summary>Gets the button tooltip for the row's primary action.</summary>
    public string PrimaryActionToolTip => IsSample ? "Open sample" : "Play macro";

    /// <summary>Gets the accessibility label for the row's primary-action button.</summary>
    public string PrimaryActionAutomationName => IsSample ? "Open sample" : "Play macro";

    /// <summary>
    /// Gets or sets a value indicating whether the per-row commands are currently invocable.
    /// Flips to <see langword="false"/> while <c>IMacroService.State</c> is non-Idle so
    /// the XAML disables the play button mid-replay.
    /// </summary>
    public bool CanInvoke
    {
        get => _canInvoke;
        set
        {
            if (_canInvoke == value)
            {
                return;
            }

            _canInvoke = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPrimaryAction));
            _playCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Gets a value indicating whether the primary action button is enabled.</summary>
    public bool CanPrimaryAction => IsSample || (CanInvoke && _service is not null);

    /// <summary>Gets the WPF command bound to the row's play button / row activation.</summary>
    public ICommand PlayCommand { get; }

    /// <summary>Gets the placeholder edit command. Disabled until m3-context-menu lands.</summary>
    public ICommand EditCommand { get; }

    /// <summary>Gets the placeholder rename command. Disabled until m3-context-menu lands.</summary>
    public ICommand RenameCommand { get; }

    /// <summary>Gets the placeholder delete command. Disabled until m3-context-menu lands.</summary>
    public ICommand DeleteCommand { get; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Records the most recent fire-and-forget Play task so tests can await completion.
    /// Internal — not bound from XAML.
    /// </summary>
    internal Task? LastPlayTask { get; private set; }

    private void InvokePlay()
    {
        if (_service is null || !CanInvoke)
        {
            return;
        }

        LastPlayTask = InvokePlayAsync(Name, Scope);
    }

    private async Task InvokePlayAsync(string name, MacroScope scope)
    {
        MacroPlayResult result = await _service!
            .PlayByNameAsync(name, scope, CancellationToken.None)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await _errorRenderer(result, name).ConfigureAwait(false);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

    /// <summary>
    /// Formats a UTC timestamp as a coarse-grained "X ago" string for the tool window. Pure
    /// helper; exposed internally so the unit tests can pin the formatting without going
    /// through the live <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    /// <param name="utc">The timestamp to format.</param>
    /// <param name="nowUtc">The reference "now" timestamp.</param>
    internal static string FormatRelative(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        var delta = nowUtc - utc;
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            int minutes = (int)delta.TotalMinutes;
            return $"{minutes}m ago";
        }

        if (delta.TotalHours < 24)
        {
            int hours = (int)delta.TotalHours;
            return $"{hours}h ago";
        }

        if (delta.TotalDays < 7)
        {
            int days = (int)delta.TotalDays;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }

        return utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Formats a UTC timestamp in the terser "modified column" style: "just now" / "5m" /
    /// "2h" / "yesterday" / "3d" / yyyy-MM-dd. Distinct from
    /// <see cref="FormatRelative(DateTimeOffset, DateTimeOffset)"/> which always appends
    /// "ago" and uses "1 day ago" instead of "yesterday".
    /// </summary>
    /// <param name="utc">The timestamp to format.</param>
    /// <param name="nowUtc">The reference "now" timestamp.</param>
    internal static string FormatModifiedRelative(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        var delta = nowUtc - utc;
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            int minutes = (int)delta.TotalMinutes;
            return $"{minutes}m";
        }

        if (delta.TotalHours < 24)
        {
            int hours = (int)delta.TotalHours;
            return $"{hours}h";
        }

        if (delta.TotalHours < 48)
        {
            return "yesterday";
        }

        if (delta.TotalDays < 7)
        {
            int days = (int)delta.TotalDays;
            return $"{days}d";
        }

        return utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Formats a byte count as a short human-readable string. Pure helper; exposed internally
    /// for unit tests that don't want to hit the file system to construct a descriptor.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return kb < 10 ? $"{kb:0.0} KB" : $"{kb:0} KB";
        }

        double mb = kb / 1024.0;
        return mb < 10 ? $"{mb:0.0} MB" : $"{mb:0} MB";
    }
}
