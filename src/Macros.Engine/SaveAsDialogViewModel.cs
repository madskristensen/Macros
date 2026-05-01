using System;
using System.ComponentModel;
using System.IO;
using Macros.Engine.Storage;

namespace Macros.Engine;

/// <summary>
/// View-model for the <c>Save Macro As</c> dialog. Pure MVVM logic with no VS Shell
/// dependency so it can be unit-tested directly against a fake <see cref="IMacroStore"/>.
/// </summary>
/// <remarks>
/// Validation runs synchronously on every <see cref="Name"/> or <see cref="Scope"/> change.
/// The heavy lifting (file-system existence check) calls the pure-path
/// <see cref="IMacroStore.GetMacroPath"/> helper then <see cref="File.Exists"/> —
/// no async I/O required because no directory is created or read.
/// </remarks>
internal sealed class SaveAsDialogViewModel : INotifyPropertyChanged
{
    private readonly IMacroStore _storage;
    private readonly bool _hasRepo;

    private string _name = string.Empty;
    private MacroScope _scope = MacroScope.Global;
    private string _validationMessage = string.Empty;
    private bool _canSave;
    private bool _existsInTargetScope;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SaveAsDialogViewModel(string currentSource, IMacroStore storage, bool hasRepo)
    {
        CurrentSource = currentSource ?? throw new ArgumentNullException(nameof(currentSource));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _hasRepo = hasRepo;
    }

    /// <summary>The macro source code that will be written when the user saves.</summary>
    public string CurrentSource { get; }

    /// <summary>
    /// Whether the Repo radio button is available. False when no solution is open.
    /// </summary>
    public bool IsRepoEnabled => _hasRepo;

    /// <summary>The name the user typed. Validated live on every keystroke.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged(nameof(Name));
            Validate(value);
        }
    }

    /// <summary>Currently selected scope. Setting <see cref="MacroScope.Repo"/> is a no-op
    /// when <see cref="IsRepoEnabled"/> is false.</summary>
    public MacroScope Scope
    {
        get => _scope;
        set
        {
            if (value == MacroScope.Repo && !_hasRepo) return;
            if (_scope == value) return;
            _scope = value;
            OnPropertyChanged(nameof(Scope));
            OnPropertyChanged(nameof(IsGlobalScope));
            OnPropertyChanged(nameof(IsRepoScope));
            Validate(_name);
        }
    }

    /// <summary>Convenience bool for two-way RadioButton binding (Global).</summary>
    public bool IsGlobalScope
    {
        get => _scope == MacroScope.Global;
        set { if (value) Scope = MacroScope.Global; }
    }

    /// <summary>Convenience bool for two-way RadioButton binding (Repo).</summary>
    public bool IsRepoScope
    {
        get => _scope == MacroScope.Repo;
        set { if (value) Scope = MacroScope.Repo; }
    }

    /// <summary>
    /// Validation feedback. Empty string when the name is acceptable (or not yet typed).
    /// A warning message when the name is valid but a file already exists (CanSave stays true).
    /// An error message when the name is invalid (CanSave is false).
    /// </summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (_validationMessage == value) return;
            _validationMessage = value;
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    /// <summary>True when <see cref="ValidationMessage"/> is non-empty; drives TextBlock visibility.</summary>
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    /// <summary>
    /// True when the name is valid and non-empty. Bound to the Save button's IsEnabled.
    /// Also true when the macro already exists (overwrite scenario) — <see cref="ValidationMessage"/>
    /// carries the warning in that case.
    /// </summary>
    public bool CanSave
    {
        get => _canSave;
        private set
        {
            if (_canSave == value) return;
            _canSave = value;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>
    /// True when a <c>.csx</c> file with <see cref="Name"/> already exists in
    /// <see cref="Scope"/>. The Save button stays enabled; the dialog shows an
    /// "It will be overwritten" warning instead of blocking the save.
    /// </summary>
    public bool ExistsInTargetScope
    {
        get => _existsInTargetScope;
        private set
        {
            if (_existsInTargetScope == value) return;
            _existsInTargetScope = value;
            OnPropertyChanged(nameof(ExistsInTargetScope));
        }
    }

    // ─── Private helpers ───────────────────────────────────────────────────────────────

    private void Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            ValidationMessage = string.Empty;
            ExistsInTargetScope = false;
            CanSave = false;
            return;
        }

        if (!_storage.IsValidName(name))
        {
            ValidationMessage = "Name contains invalid characters or is reserved.";
            ExistsInTargetScope = false;
            CanSave = false;
            return;
        }

        bool exists = false;
        try
        {
            string path = _storage.GetMacroPath(name, _scope);
            exists = File.Exists(path);
        }
        catch (InvalidOperationException)
        {
            // Repo scope with no solution open. The Scope setter already blocks setting
            // Repo when !_hasRepo, so this path is only reached in edge cases (e.g. solution
            // closed while dialog is open). Treat as non-existing and allow save — the
            // storage layer will throw a clean InvalidOperationException on SaveAsAsync.
            exists = false;
        }

        ExistsInTargetScope = exists;

        if (exists)
        {
            ValidationMessage = "A macro with this name already exists. It will be overwritten.";
            CanSave = true;
        }
        else
        {
            ValidationMessage = string.Empty;
            CanSave = true;
        }
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
