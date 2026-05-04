using Macros.Engine.Storage;

namespace Macros.ToolWindows;

/// <summary>
/// View-model for a tool-window section ("Repo" or "Global"). Owns the full list of
/// <see cref="MacroItemViewModel"/>s in that scope plus the filtered projection the XAML
/// renders.
/// </summary>
/// <remarks>
/// <para>
/// The owning <see cref="MacrosToolWindowViewModel"/> updates each group whenever the search
/// box text changes. Doing the projection in plain code (instead of <c>ICollectionView</c>)
/// keeps the view-model unit-testable without spinning up a WPF Dispatcher.
/// </para>
/// <para>
/// <see cref="IsVisible"/> collapses Repo when no solution is open and collapses any group
/// whose visible item count drops to zero under the active filter, so the headers don't
/// orphan above empty lists.
/// </para>
/// </remarks>
public sealed class MacroGroupViewModel : ExpandableGroupViewModel<MacroItemViewModel>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MacroGroupViewModel"/> class.
    /// </summary>
    /// <param name="header">Display label rendered as the section title (e.g. <c>"Global"</c>).</param>
    /// <param name="scope">The on-disk scope this group represents.</param>
    public MacroGroupViewModel(string header, MacroScope scope)
        : base(header)
    {
        Scope = scope;
    }

    /// <summary>Gets the on-disk scope this group represents.</summary>
    public MacroScope Scope { get; }
}
