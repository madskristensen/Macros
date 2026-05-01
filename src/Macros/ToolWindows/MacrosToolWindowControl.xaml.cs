using System;
using System.Windows.Controls;

namespace Macros.ToolWindows;

/// <summary>
/// Code-behind for the Macros tool window WPF content. The class is deliberately a thin shell:
/// all behaviour lives in <see cref="MacrosToolWindowViewModel"/> and is bound from XAML. The
/// only responsibilities here are wiring an injected view-model (production path through
/// <see cref="MacrosToolWindow.CreateAsync"/>) and disposing it when the panel is unloaded so
/// the engine / storage event subscriptions don't leak across tool-window close-and-reopen.
/// </summary>
public partial class MacrosToolWindowControl : UserControl
{
    /// <summary>
    /// Initializes the control with no view-model. Used by the XAML designer and by the
    /// fallback path when <see cref="MacrosToolWindow"/> hasn't been able to construct a
    /// view-model yet.
    /// </summary>
    public MacrosToolWindowControl()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Initializes the control with a pre-built <paramref name="viewModel"/> bound as the
    /// data context. Production callers (<see cref="MacrosToolWindow.CreateAsync"/>) use
    /// this overload so the panel renders populated content on first show.
    /// </summary>
    /// <param name="viewModel">The view-model to bind. Required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public MacrosToolWindowControl(MacrosToolWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Dispose the VM if we own it. Disposing here (rather than in the pane) ensures the
        // storage / service event handlers are detached even if VS recycles the WPF tree
        // without fully closing the tool window pane.
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
