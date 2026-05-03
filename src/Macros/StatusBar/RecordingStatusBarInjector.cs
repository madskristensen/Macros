using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Community.VisualStudio.Toolkit;
using Macros.Engine;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Macros.StatusBar;

/// <summary>
/// Injects a "Recording" indicator into the <em>left</em> side of the VS status bar while
/// <see cref="IMacroService"/> is in <see cref="MacroState.Recording"/>. Clicking the indicator
/// prompts the user to stop recording via <see cref="IMacroService.StopRecordingAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses the same visual-tree reflection technique as the Modes StatusBarInjector: walks
/// <see cref="Application.Current"/>.<c>MainWindow</c> to find the <c>StatusBarPanel</c>
/// <see cref="DockPanel"/>, then inserts a <see cref="StackPanel"/> with
/// <see cref="DockPanel.DockProperty"/> set to <see cref="Dock.Left"/>. This places the
/// indicator in the left portion of the status bar alongside cursor position, encoding, etc.
/// </para>
/// <para>
/// All visual-tree access is guarded in a try/catch: if VS changes its shell layout the
/// injection silently fails (logged via <c>ex.Log()</c>) and the rest of the extension
/// continues normally.
/// </para>
/// </remarks>
internal static class RecordingStatusBarInjector
{
    private static AsyncPackage? _package;
    private static IMacroService? _service;
    private static Panel? _leftPanel;
    private static FrameworkElement? _indicator;

    /// <summary>
    /// Resolves <see cref="IMacroService"/>, subscribes to <see cref="IMacroService.StateChanged"/>,
    /// and mirrors the initial recording state into the status bar.
    /// </summary>
    public static async Task InitializeAsync(AsyncPackage package)
    {
        if (package == null) throw new ArgumentNullException(nameof(package));
        _package = package;

        try
        {
            _service = await package.GetServiceAsync(typeof(IMacroService)) as IMacroService
                ?? throw new InvalidOperationException("IMacroService not registered in package container.");

            _service.StateChanged += OnStateChanged;

            // Reflect initial state in case recording started before we subscribed.
            if (_service.State == MacroState.Recording)
            {
                package.JoinableTaskFactory.RunAsync(ShowIndicatorAsync)
                    .FileAndForget("macros/statusbar/recordingindicator/init");
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    /// <summary>
    /// Unsubscribes from service events and removes the injected element from the status bar.
    /// Safe to call even if <see cref="InitializeAsync"/> was never called or failed silently.
    /// </summary>
    public static void Dispose()
    {
        if (_service != null)
        {
            _service.StateChanged -= OnStateChanged;
            _service = null;
        }

        AsyncPackage? pkg = _package;
        FrameworkElement? indicator = _indicator;
        Panel? panel = _leftPanel;

        _package = null;
        _indicator = null;
        _leftPanel = null;

        if (pkg != null && indicator != null && panel != null)
        {
            pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (panel.Children.Contains(indicator))
                    {
                        panel.Children.Remove(indicator);
                    }
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }).FileAndForget("macros/statusbar/recordingindicator/dispose");
        }
    }

    // ── Event handlers ──────────────────────────────────────────────────────────

    private static void OnStateChanged(object sender, MacroStateChangedEventArgs e)
    {
        AsyncPackage? package = _package;
        if (package == null) return;

        if (e.NewState == MacroState.Recording)
        {
            package.JoinableTaskFactory.RunAsync(ShowIndicatorAsync)
                .FileAndForget("macros/statusbar/recordingindicator/show");
        }
        else
        {
            package.JoinableTaskFactory.RunAsync(HideIndicatorAsync)
                .FileAndForget("macros/statusbar/recordingindicator/hide");
        }
    }

    // ── Show / Hide ──────────────────────────────────────────────────────────────

    private static async Task ShowIndicatorAsync()
    {
        AsyncPackage? package = _package;
        if (package == null) return;

        try
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (_leftPanel == null)
            {
                _leftPanel = FindStatusBarPanel();
                if (_leftPanel == null) return;
            }

            if (_indicator == null)
            {
                _indicator = BuildIndicator(package);
            }

            PlaceIndicatorAtFarLeft(_leftPanel, _indicator);
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static async Task HideIndicatorAsync()
    {
        AsyncPackage? package = _package;
        if (package == null) return;

        try
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            Panel? panel = _leftPanel;
            FrameworkElement? indicator = _indicator;

            if (panel != null && indicator != null && panel.Children.Contains(indicator))
            {
                panel.Children.Remove(indicator);
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();
        }
    }

    private static void PlaceIndicatorAtFarLeft(Panel panel, FrameworkElement indicator)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        indicator.SetValue(DockPanel.DockProperty, Dock.Left);

        int index = panel.Children.IndexOf(indicator);
        if (index == 0) return;

        // If the indicator is already elsewhere in the panel, remove it before reinserting at the far left.
        if (index != -1)
        {
            panel.Children.RemoveAt(index);
        }

        panel.Children.Insert(0, indicator);
    }

    // ── WPF element construction ─────────────────────────────────────────────────

    private static FrameworkElement BuildIndicator(AsyncPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xE5, 0x14, 0x00),
                ShadowDepth = 0,
                BlurRadius = 4,
                Opacity = 0.6,
            },
        };

        var label = new TextBlock
        {
            Text = "Recording",
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.StatusBarTextBrushKey);

        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Click to stop recording",
        };
        container.Children.Add(dot);
        container.Children.Add(label);

        container.MouseLeftButtonUp += (_, _) =>
        {
            AsyncPackage? pkg = _package;
            if (pkg == null) return;

            pkg.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await pkg.JoinableTaskFactory.SwitchToMainThreadAsync();

                    bool confirmed = await VS.MessageBox.ShowConfirmAsync(
                        "Stop recording?",
                        "Stop the current macro recording?");

                    if (confirmed)
                    {
                        IMacroService? svc = _service;
                        if (svc?.State == MacroState.Recording)
                        {
                            // Subscribe before Stop so we can't race the fire-and-forget save.
                            var tcs = new TaskCompletionSource<string>();
                            EventHandler<RecordingSavedEventArgs> handler = (_, args) => tcs.TrySetResult(args.Path);
                            svc.RecordingSaved += handler;

                            try
                            {
                                await svc.StopRecordingAsync();

                                var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                                if (winner == tcs.Task)
                                {
                                    await VS.Documents.OpenAsync(await tcs.Task);
                                }
                            }
                            finally
                            {
                                svc.RecordingSaved -= handler;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                }
            }).FileAndForget("macros/statusbar/recordingindicator/click");
        };

        return container;
    }

    // ── Visual-tree helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds the VS status-bar <c>StatusBarPanel</c> <see cref="DockPanel"/> via a
    /// recursive visual-tree walk rooted at <see cref="Application.Current.MainWindow"/>.
    /// Returns <see langword="null"/> and swallows any exception if the element is absent
    /// (e.g. VS layout changed between releases).
    /// </summary>
    private static Panel? FindStatusBarPanel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            return FindChild<DockPanel>(Application.Current.MainWindow, "StatusBarPanel");
        }
        catch
        {
            return null;
        }
    }

    private static T? FindChild<T>(DependencyObject? parent, string childName)
        where T : DependencyObject
    {
        if (parent == null) return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T typed)
            {
                if (string.IsNullOrEmpty(childName) ||
                    (child is FrameworkElement fe && fe.Name == childName))
                {
                    return typed;
                }
            }

            var found = FindChild<T>(child, childName);
            if (found != null) return found;
        }

        return null;
    }
}
