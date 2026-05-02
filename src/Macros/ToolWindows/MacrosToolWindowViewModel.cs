using Community.VisualStudio.Toolkit;
using Macros.Commands.Context;
using Macros.Engine;
using Macros.Engine.Storage;
using Macros.Lifecycle;
using Macros.Mvvm;
using Macros.Samples;

using Microsoft.VisualStudio.Shell;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;

namespace Macros.ToolWindows;

/// <summary>
/// Top-level view-model behind the Macros tool window. Owns the Repo and Global
/// <see cref="MacroGroupViewModel"/>s, the search filter, the toolbar commands, and the
/// inline error banner. Subscribes to <see cref="IMacroStore.LibraryChanged"/> so the
/// list re-renders automatically when the underlying file system mutates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Async loading.</b> <see cref="LoadAsync"/> calls
/// <see cref="IMacroStore.ListAllAsync"/>, partitions the result into Global / Repo
/// groups, and rebuilds the view-model collections in place. Failures are surfaced via
/// <see cref="HasError"/> + <see cref="StatusMessage"/> rather than thrown to the caller —
/// the tool window is a passive surface and a temporary I/O error must not bring it down.
/// </para>
/// <para>
/// <b>Auto-refresh.</b> The view-model subscribes to
/// <see cref="IMacroStore.LibraryChanged"/> in the constructor and unsubscribes in
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
    private readonly IMacroStore _storage;
    private readonly IMacroService? _service;
    private readonly SolutionContextTracker? _solutionTracker;
    private readonly SynchronizationContext? _uiSync;
    private readonly TimeSpan _debounceInterval;
    private readonly object _gate = new();
    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _recordCommand;
    private readonly SampleTemplateProvider _sampleTemplateProvider;
    private readonly Func<string, Task> _statusReporter;
    private readonly Func<Exception, Task> _errorReporter;
    private readonly Func<string, string, Task<bool>> _confirmAsync;
    private readonly Func<string, string, Task> _showErrorAsync;
    private readonly Func<SampleTemplate, MacroScope, CancellationToken, Task<string>> _instantiateSampleAsync;
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
    /// Coalesce window for <see cref="IMacroStore.LibraryChanged"/> bursts. Defaults to
    /// 100 ms. Tests pass <see cref="TimeSpan.Zero"/> to drain the queue immediately.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is <see langword="null"/>.</exception>
    public MacrosToolWindowViewModel(
        IMacroStore storage,
        IMacroService? service = null,
        SynchronizationContext? uiSync = null,
        TimeSpan? debounceInterval = null,
        SampleTemplateProvider? sampleTemplateProvider = null,
        Func<string, Task>? statusReporter = null,
        Func<Exception, Task>? errorReporter = null,
        Func<string, string, Task<bool>>? confirmAsync = null,
        Func<string, string, Task>? showErrorAsync = null,
        Func<SampleTemplate, MacroScope, CancellationToken, Task<string>>? instantiateSampleAsync = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _service = service;
        _uiSync = uiSync;
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(100);
        _sampleTemplateProvider = sampleTemplateProvider ?? new SampleTemplateProvider();
        _statusReporter = statusReporter ?? (message => VS.StatusBar.ShowMessageAsync(message));
        _errorReporter = errorReporter ?? (ex => ex.LogAsync());
        _confirmAsync = confirmAsync ?? ((title, message) => VS.MessageBox.ShowConfirmAsync(title, message));
        _showErrorAsync = showErrorAsync ?? ((title, message) => VS.MessageBox.ShowErrorAsync(title, message));
        _instantiateSampleAsync = instantiateSampleAsync ?? InstantiateSampleAsync;

        Groups = new ObservableCollection<MacroGroupViewModel>
        {
            // Order: Repo first (when available), then Global, then the shadowed-global
            // overflow section. All three are created up-front so the XAML doesn't need to
            // react to group add/remove — only IsAvailable / IsVisible flips. The shadowed
            // section starts unavailable; LoadAsync flips it on once shadowing is detected.
            new("Repo", MacroScope.Repo) { IsAvailable = false },
            new("Global", MacroScope.Global) { IsAvailable = true },
            new("Shadowed Global Macros", MacroScope.Global, isShadowed: true) { IsAvailable = false },
        };

        SamplesGroup = new SampleGroupViewModel("Samples");
        foreach (var template in _sampleTemplateProvider.GetTemplates())
        {
            SamplesGroup.Items.Add(new SampleTemplateItemViewModel(
                template,
                OpenSampleAsync,
                _statusReporter,
                _errorReporter));
        }

        AllItems = new ObservableCollection<MacroItemViewModel>();
        GroupedItemsView = CollectionViewSource.GetDefaultView(AllItems);
        GroupedItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MacroItemViewModel.GroupName)));

        _refreshCommand = new RelayCommand(_ => _ = LoadAsync(), _ => !IsLoading);
        // RecordCommand is a placeholder until the keyboard-recording flow lands; we still
        // expose it so the toolbar XAML can bind once.
        _recordCommand = new RelayCommand(_ => { }, _ => false);
        RefreshCommand = _refreshCommand;
        RecordCommand = _recordCommand;

        _storage.LibraryChanged += OnLibraryChanged;

        // Repo-scope availability depends on whether a solution is currently open. Storage
        // doesn't emit LibraryChanged when the active solution switches, so listen to the
        // tracker and trigger a reload explicitly on open/close/switch.
        _solutionTracker = SolutionContextTracker.Current;
        if (_solutionTracker is not null)
        {
            _solutionTracker.SolutionChanged += OnSolutionChanged;
        }

        if (_service is not null)
        {
            _service.StateChanged += OnServiceStateChanged;
        }
    }

    /// <summary>
    /// Resolves <see cref="IMacroStore"/> and <see cref="IMacroService"/> from the VS
    /// service container, instantiates the view-model, and triggers the initial load.
    /// Captures <see cref="SynchronizationContext.Current"/> as the UI sync context — the
    /// caller is expected to invoke this from the WPF dispatcher (the tool window's
    /// <c>CreateAsync</c> path runs on the UI thread).
    /// </summary>
    public static async Task<MacrosToolWindowViewModel> CreateAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Resolve via the package's own service container — NOT VS.GetRequiredServiceAsync.
        // The tool window can be auto-restored on shell startup before the global service
        // container has finished promoting our services; the package container is populated
        // synchronously by AddService(...) and is always queryable once MacrosPackage.Instance
        // is non-null (set as the very first line of InitializeAsync).
        var package = MacrosPackage.Instance
            ?? throw new InvalidOperationException(
                "MacrosPackage is not loaded; cannot resolve services for the tool window.");

        var storage = await package.GetServiceAsync(typeof(IMacroStore)) as IMacroStore
            ?? throw new InvalidOperationException(
                "IMacroStore is not registered in the package container.");
        var service = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
            ?? throw new InvalidOperationException(
                "IMacroService is not registered in the package container.");

        var uiSync = SynchronizationContext.Current;
        var vm = new MacrosToolWindowViewModel(storage, service, uiSync);
        await vm.LoadAsync();
        return vm;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the read-only sample gallery shown below the saved macro groups.</summary>
    public SampleGroupViewModel SamplesGroup { get; }

    /// <summary>Gets the Repo + Global + ShadowedGlobal section view-models, in display order.</summary>
    public ObservableCollection<MacroGroupViewModel> Groups { get; }

    /// <summary>
    /// Gets the flat list of every <see cref="MacroItemViewModel"/> currently shown, in the
    /// order Repo / Global / Shadowed-Global, name-sorted within each section. Drives the
    /// XAML's grouped <see cref="System.Windows.Controls.ListView"/> via
    /// <see cref="GroupedItemsView"/>.
    /// </summary>
    public ObservableCollection<MacroItemViewModel> AllItems { get; }

    /// <summary>
    /// Gets the WPF <see cref="ICollectionView"/> projection of <see cref="AllItems"/>,
    /// pre-configured with a <see cref="PropertyGroupDescription"/> on
    /// <see cref="MacroItemViewModel.GroupName"/>. The XAML's ListView binds to this and
    /// provides a <c>GroupStyle</c> that renders one expander per group.
    /// </summary>
    public ICollectionView GroupedItemsView { get; }

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

    /// <summary>
    /// Sets the active filter text, driving the same logic as <see cref="FilterText"/>.
    /// Called by the native VS search integration (<c>MacrosToolWindow.Pane</c>) when the
    /// user types in the VS search bar. Pass <see langword="null"/> or empty to clear.
    /// </summary>
    public void SetFilter(string? text) => FilterText = text ?? string.Empty;

    public async Task<bool> MoveMacroAsync(MacroItemViewModel item, MacroScope target, CancellationToken cancellation = default)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var (ok, error) = MoveLogic.ValidateMove(item.Descriptor, target, SolutionContextTracker.Current?.HasSolution == true);
        if (!ok)
        {
            await _showErrorAsync($"Move to {ScopeTitle(target)}", error ?? string.Empty).ConfigureAwait(true);
            return false;
        }

        try
        {
            string? source = await _storage.LoadByNameAsync(item.Name, item.Scope, cancellation).ConfigureAwait(true);
            if (source is null)
            {
                return false;
            }

            string? existing = await _storage.LoadByNameAsync(item.Name, target, cancellation).ConfigureAwait(true);
            if (existing is not null)
            {
                bool confirmed = await _confirmAsync(
                    "Conflict",
                    $"A {ScopeLabel(target)} macro named \"{item.Name}\" already exists. Overwrite?")
                    .ConfigureAwait(true);
                if (!confirmed)
                {
                    return false;
                }
            }

            await _storage.SaveAsAsync(item.Name, source, target, overwrite: true, cancellation: cancellation).ConfigureAwait(true);
            await _storage.DeleteAsync(item.Name, item.Scope, cancellation).ConfigureAwait(true);
            await _statusReporter($"Macros: Moved \"{item.Name}\" to {ScopeLabel(target)}").ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await _showErrorAsync($"Move to {ScopeTitle(target)}", ex.Message).ConfigureAwait(true);
            return false;
        }
    }

    public async Task<bool> CopySampleToScopeAsync(SampleTemplateItemViewModel item, MacroScope target, CancellationToken cancellation = default)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (target == MacroScope.Repo && SolutionContextTracker.Current?.HasSolution != true)
        {
            await _showErrorAsync("Copy sample to Repo", "Cannot copy to Repo scope: no solution is open.").ConfigureAwait(true);
            return false;
        }

        try
        {
            string createdPath = await _instantiateSampleAsync(item.Template, target, cancellation).ConfigureAwait(true);
            string createdName = Path.GetFileNameWithoutExtension(createdPath);
            await _statusReporter($"Macros: Added sample \"{createdName}\" to {ScopeLabel(target)}").ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await _showErrorAsync($"Copy sample to {ScopeTitle(target)}", ex.Message).ConfigureAwait(true);
            return false;
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
    /// Gets the debounce interval used to coalesce <see cref="IMacroStore.LibraryChanged"/>
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
    /// Loads the macro library from <see cref="IMacroStore.ListAllAsync"/>, partitions
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

        IReadOnlyList<MacroEntry>? globalList = null;
        IReadOnlyList<MacroEntry>? repoList = null;
        Exception? failure = null;
        bool repoAvailable = false;

        try
        {
            // M4: enumerate Global and Repo separately so we can surface shadowing in the
            // tool window. ListAllAsync's repo-wins merge silently drops shadowed globals,
            // which is exactly what we want to expose.
            globalList = await _storage.ListAsync(MacroScope.Global, cancellation).ConfigureAwait(true);

            try
            {
                repoList = await _storage.ListAsync(MacroScope.Repo, cancellation).ConfigureAwait(true);
                repoAvailable = true;
            }
            catch (InvalidOperationException)
            {
                // No solution open — repo half intentionally degrades to empty so the tool
                // window can render a global-only list without surfacing an error.
                repoList = Array.Empty<MacroEntry>();
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
            else
            {
                ReplaceItems(globalList ?? Array.Empty<MacroEntry>(), repoList ?? Array.Empty<MacroEntry>(), repoAvailable);
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
        if (_solutionTracker is not null)
        {
            _solutionTracker.SolutionChanged -= OnSolutionChanged;
        }

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

    private void OnSolutionChanged(object? sender, EventArgs e)
        => ScheduleReload();

    private void OnServiceStateChanged(object sender, MacroStateChangedEventArgs e)
    {
        bool canInvoke = e.NewState == MacroState.Idle;
        // Only mutate VM state on the UI thread when one is available; ObservableCollection
        // doesn't tolerate cross-thread reads from a WPF binding.
        Marshal(() =>
        {
            foreach (var item in AllItems)
            {
                item.CanInvoke = canInvoke;
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

    private void ReplaceItems(IReadOnlyList<MacroEntry> globalEntries, IReadOnlyList<MacroEntry> repoEntries, bool repoAvailable)
    {
        bool serviceIdle = _service?.State == MacroState.Idle || _service is null;

        // Build a case-insensitive set of repo names so we can flag the global entries that
        // are overridden. Repo always wins on collision; the global "loser" appears in the
        // dedicated shadowed section so the user understands why their global isn't running.
        var repoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in repoEntries)
        {
            repoNames.Add(entry.Name);
        }

        var sortedRepo = repoEntries
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sortedGlobal = globalEntries
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nonShadowedGlobal = new List<MacroEntry>(sortedGlobal.Count);
        var shadowedGlobal = new List<MacroEntry>();
        foreach (var entry in sortedGlobal)
        {
            if (repoNames.Contains(entry.Name))
            {
                shadowedGlobal.Add(entry);
            }
            else
            {
                nonShadowedGlobal.Add(entry);
            }
        }

        var repoGroup = Groups.First(g => g.Scope == MacroScope.Repo);
        var globalGroup = Groups.First(g => g.Scope == MacroScope.Global && !g.IsShadowed);
        var shadowedGroup = Groups.First(g => g.IsShadowed);

        repoGroup.Items.Clear();
        globalGroup.Items.Clear();
        shadowedGroup.Items.Clear();

        // AllItems backs GroupedItemsView (a WPF DispatcherObject / ListCollectionView).
        // Mutating AllItems from a non-owning thread causes ListCollectionView to call
        // Dispatcher.Invoke back to the creation thread, which deadlocks when that thread is
        // a thread-pool thread with no message pump (as in the debounce-timer callback when
        // _uiSync is null).  In production ALL reloads are marshalled to the UI thread via
        // _uiSync, so this check is always true there.  In tests the timer callback runs on
        // a raw thread-pool thread; we skip AllItems in that case — the Groups collections
        // (which the tests inspect) are always updated regardless of thread.
        bool onFlatViewThread = GroupedItemsView is not System.Windows.Threading.DispatcherObject d
            || d.CheckAccess();

        if (onFlatViewThread)
        {
            AllItems.Clear();
        }

        foreach (var entry in sortedRepo)
        {
            var item = new MacroItemViewModel(entry, _service)
            {
                CanInvoke = serviceIdle,
                IsShadowed = false,
            };
            repoGroup.Items.Add(item);
            if (onFlatViewThread)
            {
                AllItems.Add(item);
            }
        }

        foreach (var entry in nonShadowedGlobal)
        {
            var item = new MacroItemViewModel(entry, _service)
            {
                CanInvoke = serviceIdle,
                IsShadowed = false,
            };
            globalGroup.Items.Add(item);
            if (onFlatViewThread)
            {
                AllItems.Add(item);
            }
        }

        foreach (var entry in shadowedGlobal)
        {
            var item = new MacroItemViewModel(entry, _service)
            {
                CanInvoke = serviceIdle,
                IsShadowed = true,
            };
            shadowedGroup.Items.Add(item);
            if (onFlatViewThread)
            {
                AllItems.Add(item);
            }
        }

        repoGroup.IsAvailable = repoAvailable;
        globalGroup.IsAvailable = true;
        // The shadowed section is "available" only when there's something to display; when
        // empty it stays collapsed so the UI doesn't sprout an empty header.
        shadowedGroup.IsAvailable = shadowedGlobal.Count > 0;

        if (onFlatViewThread)
        {
            // Re-issue the grouping after a bulk edit so the ICollectionView refreshes its
            // group partitioning. Without this the grouped expanders can lag a beat behind
            // AllItems.
            GroupedItemsView.Refresh();
        }

        ApplyFilterToGroups(updateFlatView: onFlatViewThread);
        Interlocked.Increment(ref _loadCount);
        OnPropertyChanged(nameof(LoadCount));
    }

    private void ApplyFilterToGroups(bool updateFlatView = true)
    {
        string trimmed = _filterText?.Trim() ?? string.Empty;
        Func<MacroItemViewModel, bool> predicate = trimmed.Length == 0
            ? static _ => true
            : item => item.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;

        Func<SampleTemplateItemViewModel, bool> samplePredicate = trimmed.Length == 0
            ? static _ => true
            : item => item.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0
                || item.Description.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;

        SamplesGroup.ApplyFilter(samplePredicate);

        foreach (var group in Groups)
        {
            group.ApplyFilter(predicate);
        }

        if (updateFlatView)
        {
            // Mirror the same filter onto the flat ICollectionView so the grouped ListView
            // hides non-matching rows declaratively. The predicate is identical to the per-
            // group filter above.
            GroupedItemsView.Filter = trimmed.Length == 0
                ? null
                : (object o) => o is MacroItemViewModel item && predicate(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task<string> OpenSampleAsync(SampleTemplate template, CancellationToken cancellation)
    {
        string samplePath = await _sampleTemplateProvider.ExtractViewableCopyAsync(template, cancellation).ConfigureAwait(false);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
        await VS.Documents.OpenAsync(samplePath);
        return samplePath;
    }

    private Task<string> InstantiateSampleAsync(SampleTemplate template, MacroScope target, CancellationToken cancellation)
    {
        string probePath = _storage.GetMacroPath("sample", target);
        string? targetFolder = Path.GetDirectoryName(probePath);
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new InvalidOperationException($"Could not resolve the {ScopeLabel(target)} macros folder.");
        }

        return _sampleTemplateProvider.InstantiateAsync(template, targetFolder, cancellation);
    }

    private static string ScopeLabel(MacroScope scope)
        => scope == MacroScope.Repo ? "repo" : "global";

    private static string ScopeTitle(MacroScope scope)
        => scope == MacroScope.Repo ? "Repo" : "Global";

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
