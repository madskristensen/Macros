using System;
using System.ComponentModel;
using System.IO;
using Macros.Engine.Storage;

namespace Macros.Commands;

/// <summary>
/// View-model for the <c>Rename Macro</c> dialog. Pure MVVM logic with no VS Shell
/// dependency so it can be unit-tested directly against a fake <see cref="IMacroStorage"/>.
/// </summary>
/// <remarks>
/// Validation runs synchronously on every <see cref="NewName"/> change using the pure-path
/// <see cref="IMacroStorage.GetMacroPath"/> helper then <see cref="File.Exists"/> —
/// no async I/O required.
/// </remarks>
internal sealed class RenameDialogViewModel : INotifyPropertyChanged
{
    private readonly IMacroStorage _storage;

    private string _newName = string.Empty;
    private string _validationMessage = string.Empty;
    private bool _canRename;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RenameDialogViewModel(MacroDescriptor descriptor, IMacroStorage storage)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        OldName = descriptor.Name;
        Scope = descriptor.Scope;
    }

    /// <summary>The current name of the macro being renamed. Shown in the dialog label.</summary>
    public string OldName { get; }

    /// <summary>The scope the macro lives in. Shown for user clarity; rename never crosses scopes.</summary>
    public MacroScope Scope { get; }

    /// <summary>The new name the user typed. Validated live on every keystroke.</summary>
    public string NewName
    {
        get => _newName;
        set
        {
            if (_newName == value) return;
            _newName = value;
            OnPropertyChanged(nameof(NewName));
            Validate(value);
        }
    }

    /// <summary>
    /// Validation feedback. Empty string when the name is acceptable or not yet typed.
    /// An error message when the name cannot be used.
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

    /// <summary>True when the new name is valid and the rename can proceed. Bound to the Rename button's IsEnabled.</summary>
    public bool CanRename
    {
        get => _canRename;
        private set
        {
            if (_canRename == value) return;
            _canRename = value;
            OnPropertyChanged(nameof(CanRename));
        }
    }

    /// <summary>
    /// True when a macro with <see cref="NewName"/> already exists in <see cref="Scope"/>.
    /// Pure path computation — no I/O beyond <see cref="File.Exists"/>.
    /// </summary>
    public bool TargetExists
    {
        get
        {
            if (string.IsNullOrEmpty(_newName) || !_storage.IsValidName(_newName))
                return false;

            try
            {
                return File.Exists(_storage.GetMacroPath(_newName, Scope));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    // ─── Private helpers ───────────────────────────────────────────────────────────────

    private void Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            ValidationMessage = string.Empty;
            CanRename = false;
            return;
        }

        if (string.Equals(name, OldName, StringComparison.Ordinal))
        {
            ValidationMessage = "New name is the same as current.";
            CanRename = false;
            return;
        }

        if (!_storage.IsValidName(name))
        {
            ValidationMessage = "Invalid characters or reserved name.";
            CanRename = false;
            return;
        }

        if (TargetExists)
        {
            ValidationMessage = "A macro with that name already exists.";
            CanRename = false;
            return;
        }

        ValidationMessage = string.Empty;
        CanRename = true;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
