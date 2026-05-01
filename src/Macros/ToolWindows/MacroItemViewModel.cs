using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Macros.Engine;
using Macros.Engine.Storage;
using Macros.Mvvm;

namespace Macros.ToolWindows;

/// <summary>
/// View-model for a single row in the macro list. Wraps a <see cref="MacroDescriptor"/> and
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
    private bool _canInvoke = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacroItemViewModel"/> class.
    /// </summary>
    /// <param name="descriptor">The on-disk metadata snapshot for this macro.</param>
    /// <param name="service">
    /// The macro engine. May be <see langword="null"/> in pure design-time scenarios; when
    /// <see langword="null"/>, <see cref="PlayCommand"/> becomes a no-op.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public MacroItemViewModel(MacroDescriptor descriptor, IMacroService? service)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _service = service;

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
    public MacroDescriptor Descriptor { get; }

    /// <summary>Gets the macro name (file stem, no extension) — primary row text.</summary>
    public string Name => Descriptor.Name;

    /// <summary>Gets the scope this macro lives in. Used by the <see cref="PlayCommand"/>.</summary>
    public MacroScope Scope => Descriptor.Scope;

    /// <summary>
    /// Gets a short human-readable last-modified timestamp ("just now", "5m ago", "2 days ago",
    /// or absolute date) for the secondary row text.
    /// </summary>
    public string LastModifiedDisplay => FormatRelative(Descriptor.LastModifiedUtc, DateTime.UtcNow);

    /// <summary>
    /// Gets a short human-readable file size for the tertiary row text (e.g. <c>"1.2 KB"</c>).
    /// </summary>
    public string SizeDisplay => FormatBytes(Descriptor.SizeBytes);

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
            _playCommand.RaiseCanExecuteChanged();
        }
    }

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

        // The command is sync (ICommand contract); the underlying call is async. Capture the
        // task on the VM so unit tests can observe completion. Production WPF doesn't await
        // it; the engine's StateChanged event is what surfaces playback progress to the UI.
        LastPlayTask = _service.PlayByNameAsync(Name, Scope, CancellationToken.None);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

    /// <summary>
    /// Formats a UTC timestamp as a coarse-grained "X ago" string for the tool window. Pure
    /// helper; exposed internally so the unit tests can pin the formatting without going
    /// through the live <see cref="DateTime.UtcNow"/>.
    /// </summary>
    /// <param name="utc">The timestamp to format.</param>
    /// <param name="nowUtc">The reference "now" timestamp.</param>
    internal static string FormatRelative(DateTime utc, DateTime nowUtc)
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
