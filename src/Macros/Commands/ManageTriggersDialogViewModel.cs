using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Macros.Engine.Triggers;

namespace Macros.Commands;

/// <summary>
/// View-model for the M4 "Manage Triggers..." dialog. Pure MVVM logic with no VS Shell
/// dependency so it can be unit-tested directly. The owning
/// <see cref="Context.ManageTriggersContextCommand"/> seeds the view-model from the parsed
/// header of the selected macro and reads <see cref="Bindings"/> back after the dialog
/// closes to rebuild the <c>.csx</c> header via <see cref="TriggerHeaderRewriter.Rewrite"/>.
/// </summary>
/// <remarks>
/// <para>
/// The view-model intentionally does NOT touch <see cref="Macros.Engine.Storage.IMacroStore"/>.
/// Saving to disk is the context command's responsibility — this VM is a pure UI helper that
/// validates input, prevents duplicate bindings, and exposes an in-memory list the caller
/// can persist however it likes.
/// </para>
/// </remarks>
internal sealed class ManageTriggersDialogViewModel : INotifyPropertyChanged
{
    private TriggerKind _newTriggerKind = TriggerKind.VsEvent;
    private string _newTriggerName = string.Empty;
    private string _newTriggerFilters = string.Empty;
    private string _validationMessage = string.Empty;
    private bool _canAddTrigger;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new view-model.
    /// </summary>
    /// <param name="macroName">The display name of the macro whose triggers are being edited.</param>
    /// <param name="initialBindings">
    /// Bindings parsed from the <c>.csx</c> header. The single-element <c>[Manual]</c>
    /// default is filtered out so the user starts with an empty list (Manual is implicit
    /// when no directives are present).
    /// </param>
    /// <param name="knownEvents">
    /// Discovered <c>VS.Events</c> catalog used to populate the autocomplete drop-down for
    /// <see cref="TriggerKind.VsEvent"/>. May be empty when the toolkit isn't loaded.
    /// </param>
    public ManageTriggersDialogViewModel(
        string macroName,
        IReadOnlyList<TriggerBinding> initialBindings,
        IReadOnlyList<KnownVsEvent>? knownEvents = null)
    {
        MacroName = macroName ?? throw new ArgumentNullException(nameof(macroName));
        if (initialBindings is null) throw new ArgumentNullException(nameof(initialBindings));

        Bindings = new ObservableCollection<TriggerBindingViewModel>();
        foreach (var b in FilterImplicitManual(initialBindings))
        {
            Bindings.Add(new TriggerBindingViewModel(b, RemoveBinding));
        }

        KnownEvents = knownEvents ?? Array.Empty<KnownVsEvent>();

        Validate();
    }

    /// <summary>Read-only display name of the macro whose triggers are being edited.</summary>
    public string MacroName { get; }

    /// <summary>
    /// Current set of bindings. Mutated by <see cref="AddCurrentInputAsBinding"/> and the
    /// per-row <see cref="TriggerBindingViewModel.RemoveCommand"/>. The dialog binds this
    /// directly to its <c>ListBox.ItemsSource</c>.
    /// </summary>
    public ObservableCollection<TriggerBindingViewModel> Bindings { get; }

    /// <summary>Discovered <c>VS.Events</c> catalog. Bound to the Add panel's autocomplete drop-down.</summary>
    public IReadOnlyList<KnownVsEvent> KnownEvents { get; }

    /// <summary>The kind of trigger the user is composing in the Add panel.</summary>
    public TriggerKind NewTriggerKind
    {
        get => _newTriggerKind;
        set
        {
            if (_newTriggerKind == value) return;
            _newTriggerKind = value;
            OnPropertyChanged(nameof(NewTriggerKind));
            OnPropertyChanged(nameof(IsNameRequired));
            OnPropertyChanged(nameof(AreFiltersAvailable));
            Validate();
        }
    }

    /// <summary>The name field in the Add panel (event canonical name or VS command name).</summary>
    public string NewTriggerName
    {
        get => _newTriggerName;
        set
        {
            value ??= string.Empty;
            if (_newTriggerName == value) return;
            _newTriggerName = value;
            OnPropertyChanged(nameof(NewTriggerName));
            Validate();
        }
    }

    /// <summary>
    /// The filters field in the Add panel. Only meaningful for
    /// <see cref="TriggerKind.VsEvent"/>. Accepted forms:
    /// <c>key=value</c>, <c>key=value and key2=value2</c>, or <c>key=value;key2=value2</c>.
    /// </summary>
    public string NewTriggerFilters
    {
        get => _newTriggerFilters;
        set
        {
            value ??= string.Empty;
            if (_newTriggerFilters == value) return;
            _newTriggerFilters = value;
            OnPropertyChanged(nameof(NewTriggerFilters));
            Validate();
        }
    }

    /// <summary>True when the Add panel's input forms a valid, non-duplicate binding.</summary>
    public bool CanAddTrigger
    {
        get => _canAddTrigger;
        private set
        {
            if (_canAddTrigger == value) return;
            _canAddTrigger = value;
            OnPropertyChanged(nameof(CanAddTrigger));
        }
    }

    /// <summary>Validation feedback shown inline in the Add panel. Empty string when input is valid.</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            value ??= string.Empty;
            if (_validationMessage == value) return;
            _validationMessage = value;
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    /// <summary>True when <see cref="ValidationMessage"/> is non-empty (drives TextBlock visibility).</summary>
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    /// <summary>True for kinds that need a name field (everything except <see cref="TriggerKind.Manual"/>).</summary>
    public bool IsNameRequired => _newTriggerKind != TriggerKind.Manual;

    /// <summary>True for kinds that accept a filter expression (only <see cref="TriggerKind.VsEvent"/>).</summary>
    public bool AreFiltersAvailable => _newTriggerKind == TriggerKind.VsEvent;

    /// <summary>
    /// Builds a <see cref="TriggerBinding"/> from the current Add-panel inputs and appends
    /// it to <see cref="Bindings"/>. No-op when <see cref="CanAddTrigger"/> is
    /// <see langword="false"/>.
    /// </summary>
    /// <returns>The added binding, or <see langword="null"/> when add was rejected.</returns>
    public TriggerBinding? AddCurrentInputAsBinding()
    {
        if (!CanAddTrigger) return null;

        var binding = BuildFromInputs();
        if (binding is null) return null;

        Bindings.Add(new TriggerBindingViewModel(binding, RemoveBinding));

        // Reset the input fields so the user can immediately compose another trigger.
        // Kind is intentionally preserved — most users add several of the same kind.
        _newTriggerName = string.Empty;
        _newTriggerFilters = string.Empty;
        OnPropertyChanged(nameof(NewTriggerName));
        OnPropertyChanged(nameof(NewTriggerFilters));
        Validate();
        return binding;
    }

    /// <summary>
    /// Returns the current bindings as a plain list, in display order. Called by the
    /// context command after the dialog closes to feed
    /// <see cref="TriggerHeaderRewriter.Rewrite"/>.
    /// </summary>
    public IReadOnlyList<TriggerBinding> ToBindingsList()
        => Bindings.Select(b => b.Source).ToArray();

    // ─── internals ─────────────────────────────────────────────────────────────────────

    private void RemoveBinding(TriggerBindingViewModel item)
    {
        Bindings.Remove(item);
        // Removing may unblock a previously-duplicate input — re-validate.
        Validate();
    }

    private TriggerBinding? BuildFromInputs()
    {
        var name = (_newTriggerName ?? string.Empty).Trim();
        switch (_newTriggerKind)
        {
            case TriggerKind.Manual:
                return TriggerBinding.Manual;

            case TriggerKind.BeforeCommand:
                return string.IsNullOrEmpty(name)
                    ? null
                    : new TriggerBinding(TriggerKind.BeforeCommand, name);

            case TriggerKind.AfterCommand:
                return string.IsNullOrEmpty(name)
                    ? null
                    : new TriggerBinding(TriggerKind.AfterCommand, name);

            case TriggerKind.VsEvent:
                if (string.IsNullOrEmpty(name) || !name.Contains('.')) return null;
                var filters = ParseFilters(_newTriggerFilters);
                return new TriggerBinding(TriggerKind.VsEvent, name, filters);

            default:
                return null;
        }
    }

    private void Validate()
    {
        var name = (_newTriggerName ?? string.Empty).Trim();

        switch (_newTriggerKind)
        {
            case TriggerKind.Manual:
                // Manual takes no name and no filters; the only failure mode is a duplicate.
                break;

            case TriggerKind.BeforeCommand:
            case TriggerKind.AfterCommand:
                if (string.IsNullOrEmpty(name))
                {
                    ValidationMessage = "Command name required.";
                    CanAddTrigger = false;
                    return;
                }
                break;

            case TriggerKind.VsEvent:
                if (string.IsNullOrEmpty(name))
                {
                    ValidationMessage = "Event name required.";
                    CanAddTrigger = false;
                    return;
                }
                if (!name.Contains('.'))
                {
                    ValidationMessage = "Event name must be qualified (Category.EventName).";
                    CanAddTrigger = false;
                    return;
                }
                break;
        }

        var candidate = BuildFromInputs();
        if (candidate is null)
        {
            ValidationMessage = "Invalid input.";
            CanAddTrigger = false;
            return;
        }

        if (Bindings.Any(b => b.Source.Equals(candidate)))
        {
            ValidationMessage = "Already added.";
            CanAddTrigger = false;
            return;
        }

        ValidationMessage = string.Empty;
        CanAddTrigger = true;
    }

    private static IReadOnlyDictionary<string, string> ParseFilters(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return dict;

        // Accept both " and " (canonical) and ";" (terminal-friendly) as separators.
        foreach (var part in Regex.Split(s, @"\s+and\s+|;", RegexOptions.IgnoreCase))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;

            int eq = p.IndexOf('=');
            if (eq <= 0) continue;

            var k = p.Substring(0, eq).Trim();
            var v = p.Substring(eq + 1).Trim();
            if (k.Length == 0) continue;

            dict[k] = v;
        }
        return dict;
    }

    /// <summary>
    /// Drops the implicit single-Manual binding so the dialog opens with an empty list when
    /// the macro has no <c>@trigger</c> directives. Real Manual directives that the user
    /// explicitly added (anything beyond a single Manual) are preserved.
    /// </summary>
    private static IEnumerable<TriggerBinding> FilterImplicitManual(IReadOnlyList<TriggerBinding> bindings)
    {
        if (bindings.Count == 1 && bindings[0].Kind == TriggerKind.Manual)
        {
            yield break;
        }
        foreach (var b in bindings) yield return b;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
