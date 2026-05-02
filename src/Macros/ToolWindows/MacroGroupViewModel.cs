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
    /// <param name="isShadowed">
    /// When <see langword="true"/>, this group represents the "Shadowed Global Macros"
    /// section — globals that are overridden by a same-named repo macro. Drives the muted
    /// row styling in the XAML.
    /// </param>
    public MacroGroupViewModel(string header, MacroScope scope, bool isShadowed = false)
        : base(header)
    {
        Scope = scope;
        IsShadowed = isShadowed;
    }

    /// <summary>Gets the on-disk scope this group represents.</summary>
    public MacroScope Scope { get; }

    /// <summary>
    /// Gets a value indicating whether this group represents the shadowed-global section
    /// (a global macro overridden by a repo macro of the same name). Used by the XAML to
    /// distinguish the two <see cref="MacroScope.Global"/> sections.
    /// </summary>
    public bool IsShadowed { get; }
}
