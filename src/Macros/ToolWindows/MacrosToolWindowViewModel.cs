using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Macros.Engine.Storage;
using Macros.Mvvm;

namespace Macros.ToolWindows;

/// <summary>
/// Top-level view-model behind the Macros tool window. Owns the Repo and Global
/// <see cref="MacroGroupViewModel"/>s, the search filter, the toolbar commands, and the
/// inline error banner. Subscribes to <see cref="IMacroStorage.LibraryChanged"/> so the
/// list re-renders automatically when the underlying file system mutates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Async loading.</b> <see cref="LoadAsync"/> calls
/// <see cref="IMacroStorage.ListAllAsync"/>, partitions the result into Global / Repo
/// groups, and rebuilds the view-model collections in place. Failures are surfaced via
/// <see cref="HasError"/> + <see cref="StatusMessage"/> rather than thrown to the caller —
/// the tool window is a passive surface and a temporary I/O error must not bring it down.
/// </para>
/// <para>
/// <b>Auto-refresh.</b> The view-model subscribes to
/// <see cref="IMacroStorage.LibraryChanged"/> in the constructor and unsubscribes in
/// <see cref="Dispose"/>. Library events fire on whichever thread performed the change, so
/// the handler debounces through a <see cref="System.Threading.Timer"/>: rapid bursts (e.g.
/// rename = remove + add) coalesce into a single reload after
/// <see cref="DebounceInterval"/>. Reloads are marshalled back to the UI through the
/// <see cref="SynchronizationContext"/> captured at construction time so the
/// <c>ObservableCollection</c> mutations happen on the WPF dispatcher.
/// </para>
/// <para>
/// <b>Service gating.</b> The view-model also subscribes to
/// <see cref="IMacroService.StateChanged"/> and propagates the engine's
/// <c>State == Idle</c> bit into <see cref="MacroItemViewModel.CanInvoke"/> on every row
/// so the per-row Play button disables mid-replay.
/// </para>
/// </remarks>
public sealed class MacrosToolWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMacroStorage _storage;
    private readonly IMacroService? _service;
    private readonly SynchronizationContext? _uiSync;
    private readonly TimeSpan _debounceInterval;
    private readonly object _gate = new();
    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _recordCommand;
    private System.Threading.Timer? _debounceTimer;
    private TaskCompletionSource<bool> _nextLoadTcs = CreateTcs();
    private bool _disposed;
    private string _filterText = string.Empty;
    private bool _isLoading;
    private bool _hasError;
    private string? _statusMessage;
    private int _loadCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacrosToolWindowViewModel"/> class. The
    /// constructor subscribes to storage and service events but does not load — call
    /// <see cref="LoadAsync"/> (or rely on the auto-trigger from
    /// <see cref="CreateAsync"/>) to populate the groups.
    /// </summary>
    /// <param name="storage">The macro storage layer to enumerate. Required.</param>
    /// <param name="service">
    /// Optional macro engine. When supplied, the view-model wires the per-row
    /// <see cref="MacroItemViewModel.PlayCommand"/> through it and tracks
    /// <see cref="IMacroService.StateChanged"/> to gate invocations.
    /// </param>
    /// <param name="uiSync">
    /// Optional UI-thread <see cref="SynchronizationContext"/>. When non-null, all
    /// auto-refresh reloads are posted onto it so the WPF dispatcher sees the
    /// <c>ObservableCollection</c> mutations. Tests typically pass <see langword="null"/>.
    /// </param>
    /// <param name="debounceInterval">
    /// Coalesce window for <see cref="IMacroStorage.LibraryChanged"/> bursts. Defaults to
    /// 100 ms. Tests pass <see cref="TimeSpan.Zero"/> to drain the queue immediately.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is <see langword="null"/>.</exception>
    public MacrosToolWindowViewModel(
        IMacroStorage storage,
        IMacroService? service = null,
        SynchronizationContext? uiSync = null,
        TimeSpan? debounceInterval = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _service = service;
        _uiSync = uiSync;
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(100);

        Groups = new ObservableCollection<MacroGroupViewModel>
        {
            // Order: Repo first (when available), then Global. Both are created up-front so
            // the XAML doesn't need to react to group add/remove — only IsVisible flips.
            new("Repo", MacroScope.Repo) { IsAvailable = false },
            new("Global", MacroScope.Global) { IsAvailable = true },
        };

        _refreshCommand = new RelayCommand(_ => _ = LoadAsync(), _ => !IsLoading);
        // RecordCommand is a placeholder until the keyboard-recording flow lands; we still
        // expose it so the toolbar XAML can bind once.
        _recordCommand = new RelayCommand(_ => { }, _ => false);
        RefreshCommand = _refreshCommand;
        RecordCommand = _recordCommand;

        _storage.LibraryChanged += OnLibraryChanged;

        if (_service is not null)
        {
            _service.StateChanged += OnServiceStateChanged;
        }
    }

    /// <summary>
    /// Resolves <see cref="IMacroStorage"/> and <see cref="IMacroService"/> from the VS
    /// service container, instantiates the view-model, and triggers the initial load.
    /// Captures <see cref="SynchronizationContext.Current"/> as the UI sync context — the
    /// caller is expected to invoke this from the WPF dispatcher (the tool window's
    /// <c>CreateAsync</c> path runs on the UI thread).
    /// </summary>
    public static async Task<MacrosToolWindowViewModel> CreateAsync()
    {
        var storage = await VS.GetRequiredServiceAsync<IMacroStorage, IMacroStorage>();
        var service = await VS.GetRequiredServiceAsync<IMacroService, IMacroService>();

        var vm = new MacrosToolWindowViewModel(storage, service, SynchronizationContext.Current);
        await vm.LoadAsync();
        return vm;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the Repo + Global section view-models, in display order.</summary>
    public ObservableCollection<MacroGroupViewModel> Groups { get; }

    /// <summary>
    /// Gets or sets the active filter text. Setting it re-runs the per-group filter and
    /// updates each <see cref="MacroGroupViewModel.VisibleItems"/> projection.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            value ??= string.Empty;
            if (_filterText == value)
            {
                return;
            }

            _filterText = value;
            OnPropertyChanged();
            ApplyFilterToGroups();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Gets a value indicating whether a load is in flight.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
            _refreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Gets a value indicating whether the inline error banner should appear.</summary>
    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (_hasError == value)
            {
                return;
            }

            _hasError = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the message shown in the inline status banner. Non-null when
    /// <see cref="HasError"/> is true.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the empty-state placeholder should show. True when
    /// no group has any visible items and the panel is not in error.
    /// </summary>
    public bool IsEmpty => !HasError && Groups.All(g => g.VisibleItems.Count == 0);

    /// <summary>Gets the toolbar Refresh command.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Gets the toolbar "Record / save current.csx as named macro" command. Disabled in M3
    /// — wired by a later wave (m3-record-from-current).
    /// </summary>
    public ICommand RecordCommand { get; }

    /// <summary>
    /// Gets the monotonically-increasing count of completed loads (success or failure).
    /// Useful in unit tests as a synchronization barrier.
    /// </summary>
    public int LoadCount => _loadCount;

    /// <summary>
    /// Gets the debounce interval used to coalesce <see cref="IMacroStorage.LibraryChanged"/>
    /// events. Exposed for testing.
    /// </summary>
    internal TimeSpan DebounceInterval => _debounceInterval;

    /// <summary>
    /// Returns a task that completes after the next reload finishes. Each completed reload
    /// rolls the underlying TaskCompletionSource so consecutive calls observe distinct
    /// reloads. Used by tests to synchronise on auto-refresh without polling.
    /// </summary>
    public Task NextLoadAsync()
    {
        lock (_gate)
        {
#pragma warning disable VSTHRD003 // The TCS is owned by this VM; awaiting it from tests is the
                                  // intended synchronisation primitive and cannot deadlock.
            return _nextLoadTcs.Task;
#pragma warning restore VSTHRD003
        }
    }

    /// <summary>
    /// Loads the macro library from <see cref="IMacroStorage.ListAllAsync"/>, partitions
    /// into Global / Repo groups, and refreshes the view-model. Errors are caught and
    /// surfaced via <see cref="HasError"/>.
    /// </summary>
    /// <param name="cancellation">Cancels the underlying enumeration.</param>
    public async Task LoadAsync(CancellationToken cancellation = default)
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        HasError = false;
        StatusMessage = null;

        IReadOnlyList<MacroDescriptor>? loaded = null;
        Exception? failure = null;
        bool repoAvailable = false;

        try
        {
            loaded = await _storage.ListAllAsync(cancellation).ConfigureAwait(true);

            // Repo availability tracks whether ListAsync(Repo) would succeed. ListAllAsync
            // silently skips Repo when no solution is open, so we probe separately. The probe
            // throws InvalidOperationException for "no solution" — which is the signal we want.
            try
            {
                _ = await _storage.ListAsync(MacroScope.Repo, cancellation).ConfigureAwait(true);
                repoAvailable = true;
            }
            catch (InvalidOperationException)
            {
                repoAvailable = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error condition; just bow out without mutating state.
            IsLoading = false;
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            if (failure is not null)
            {
                HasError = true;
                StatusMessage = "Failed to load macros — check Output window.";
                await failure.LogAsync().ConfigureAwait(true);
            }
            else if (loaded is not null)
            {
                ReplaceItems(loaded, repoAvailable);
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            CompleteCurrentLoadSignal();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _storage.LibraryChanged -= OnLibraryChanged;
        if (_service is not null)
        {
            _service.StateChanged -= OnServiceStateChanged;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        // Drain any awaiters so they don't hang on a never-completing TCS after disposal.
        lock (_gate)
        {
            _nextLoadTcs.TrySetResult(false);
        }
    }

    private void OnLibraryChanged(object sender, MacroLibraryChangedEventArgs e)
        => ScheduleReload();

    private void OnServiceStateChanged(object sender, MacroStateChangedEventArgs e)
    {
        bool canInvoke = e.NewState == MacroState.Idle;
        // Only mutate VM state on the UI thread when one is available; ObservableCollection
        // doesn't tolerate cross-thread reads from a WPF binding.
        Marshal(() =>
        {
            foreach (var group in Groups)
            {
                foreach (var item in group.Items)
                {
                    item.CanInvoke = canInvoke;
                }
            }
        });
    }

    private void ScheduleReload()
    {
        if (_disposed)
        {
            return;
        }

        // System.Threading.Timer is a one-shot here: we Dispose / re-create on every event so
        // a burst (rename = removed + added) coalesces into one reload at the trailing edge.
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(
            _ =>
            {
                if (_disposed)
                {
                    return;
                }

                Marshal(() => _ = LoadAsync());
            },
            state: null,
            dueTime: _debounceInterval,
            period: System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void ReplaceItems(IReadOnlyList<MacroDescriptor> descriptors, bool repoAvailable)
    {
        var byScope = descriptors
            .GroupBy(d => d.Scope)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList());

        bool serviceIdle = _service?.State == MacroState.Idle || _service is null;

        foreach (var group in Groups)
        {
            group.Items.Clear();
            if (byScope.TryGetValue(group.Scope, out var list))
            {
                foreach (var descriptor in list)
                {
                    var item = new MacroItemViewModel(descriptor, _service)
                    {
                        CanInvoke = serviceIdle,
                    };
                    group.Items.Add(item);
                }
            }

            group.IsAvailable = group.Scope == MacroScope.Global || repoAvailable;
        }

        ApplyFilterToGroups();
        Interlocked.Increment(ref _loadCount);
        OnPropertyChanged(nameof(LoadCount));
    }

    private void ApplyFilterToGroups()
    {
        string trimmed = _filterText?.Trim() ?? string.Empty;
        Func<MacroItemViewModel, bool> predicate = trimmed.Length == 0
            ? static _ => true
            : item => item.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;

        foreach (var group in Groups)
        {
            group.ApplyFilter(predicate);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Marshal(Action action)
    {
        if (_uiSync is null)
        {
            action();
            return;
        }

#pragma warning disable VSTHRD001 // SynchronizationContext.Post is the documented contract for
                                  // pushing work to the WPF dispatcher captured at construction
                                  // time. We deliberately don't depend on JoinableTaskFactory
                                  // here so the VM stays unit-testable without a JTF.
        _uiSync.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    private void CompleteCurrentLoadSignal()
    {
        TaskCompletionSource<bool> previous;
        lock (_gate)
        {
            previous = _nextLoadTcs;
            _nextLoadTcs = CreateTcs();
        }

        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
