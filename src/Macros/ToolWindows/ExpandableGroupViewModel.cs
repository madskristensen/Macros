using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Macros.ToolWindows;

/// <summary>
/// Shared expandable group view-model logic used by macro and sample sections.
/// </summary>
public abstract class ExpandableGroupViewModel<TItem> : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isAvailable = true;
    private Func<TItem, bool> _filterPredicate = static _ => true;

    protected ExpandableGroupViewModel(string header, bool isExpanded = true)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("Header must be non-empty.", nameof(header));
        }

        Header = header;
        _isExpanded = isExpanded;
        Items = new ObservableCollection<TItem>();
        VisibleItems = new ObservableCollection<TItem>();
        Items.CollectionChanged += OnItemsChanged;
    }

    public string Header { get; }

    public ObservableCollection<TItem> Items { get; }

    public ObservableCollection<TItem> VisibleItems { get; }

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

    public bool IsVisible => IsAvailable && VisibleItems.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyFilter(Func<TItem, bool>? predicate)
    {
        _filterPredicate = predicate ?? (static _ => true);
        RefreshVisibleItems();
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

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
}
