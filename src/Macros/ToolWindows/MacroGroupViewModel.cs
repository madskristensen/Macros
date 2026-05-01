using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Macros.Engine.Storage;

namespace Macros.ToolWindows;

/// <summary>
/// View-model for a tool-window section ("Repo" or "Global"). Owns the full list of
/// <see cref="MacroItemViewModel"/>s in that scope plus the filtered projection the XAML
/// renders.
/// </summary>
/// <remarks>
/// <para>
/// The owning <see cref="MacrosToolWindowViewModel"/> updates <see cref="FilterPredicate"/>
/// whenever the search box text changes, then asks every group to <see cref="ApplyFilter"/>.
/// Doing the projection in plain code (instead of <c>ICollectionView</c>) keeps the
/// view-model unit-testable without spinning up a WPF Dispatcher.
/// </para>
/// <para>
/// <see cref="IsVisible"/> collapses Repo when no solution is open and collapses any group
/// whose visible item count drops to zero under the active filter, so the headers don't
/// orphan above empty lists.
/// </para>
/// </remarks>
public sealed class MacroGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isAvailable = true;
    private Func<MacroItemViewModel, bool> _filterPredicate = static _ => true;

    /// <summary>
    /// Initializes a new instance of the <see cref="MacroGroupViewModel"/> class.
    /// </summary>
    /// <param name="header">Display label rendered as the section title (e.g. <c>"Global"</c>).</param>
    /// <param name="scope">The on-disk scope this group represents.</param>
    /// <exception cref="ArgumentException"><paramref name="header"/> is null or whitespace.</exception>
    public MacroGroupViewModel(string header, MacroScope scope)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("Header must be non-empty.", nameof(header));
        }

        Header = header;
        Scope = scope;
        Items = new ObservableCollection<MacroItemViewModel>();
        VisibleItems = new ObservableCollection<MacroItemViewModel>();
        Items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>Gets the display label for this section.</summary>
    public string Header { get; }

    /// <summary>Gets the on-disk scope this group represents.</summary>
    public MacroScope Scope { get; }

    /// <summary>Gets the unfiltered list of macro view-models in this section.</summary>
    public ObservableCollection<MacroItemViewModel> Items { get; }

    /// <summary>
    /// Gets the filtered projection of <see cref="Items"/> bound by the XAML. Refreshed by
    /// <see cref="ApplyFilter"/> whenever the parent view-model's filter text changes.
    /// </summary>
    public ObservableCollection<MacroItemViewModel> VisibleItems { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the section is structurally available (Repo is
    /// unavailable when no solution is open). When <see langword="false"/> the group is
    /// collapsed regardless of <see cref="Items"/> contents.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value)
            {
                return;
            }

            _isAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the expander is open. Defaults to
    /// <see langword="true"/>; the user can collapse a group from the UI to reduce noise.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets a value indicating whether this group should be shown. Hidden when the section is
    /// unavailable (Repo + no solution) or when the active filter has nothing to display.
    /// </summary>
    public bool IsVisible => IsAvailable && VisibleItems.Count > 0;

    /// <summary>Gets the active filter predicate (set by the parent view-model).</summary>
    internal Func<MacroItemViewModel, bool> FilterPredicate => _filterPredicate;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Re-applies <paramref name="predicate"/> across <see cref="Items"/> and rewrites
    /// <see cref="VisibleItems"/> in place. Cheap for the small list sizes the tool window
    /// realistically holds (single-digit thousands at the absolute outside) — no need for
    /// incremental delta tracking.
    /// </summary>
    /// <param name="predicate">
    /// The filter predicate. <see langword="null"/> resets to "show everything".
    /// </param>
    public void ApplyFilter(Func<MacroItemViewModel, bool>? predicate)
    {
        _filterPredicate = predicate ?? (static _ => true);
        RefreshVisibleItems();
    }

    private void OnItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        => RefreshVisibleItems();

    private void RefreshVisibleItems()
    {
        VisibleItems.Clear();
        foreach (var item in Items.Where(_filterPredicate))
        {
            VisibleItems.Add(item);
        }

        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(IsVisible));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
