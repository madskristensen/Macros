using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Macros.Mvvm;
using Macros.Samples;

namespace Macros.ToolWindows;

public sealed class SampleTemplateItemViewModel : INotifyPropertyChanged
{
    private readonly Func<SampleTemplate, CancellationToken, Task<string>> _openAsync;
    private readonly Func<string, Task> _statusReporter;
    private readonly Func<Exception, Task> _errorReporter;
    private readonly RelayCommand _openCommand;
    private bool _isOpening;

    public SampleTemplateItemViewModel(
        SampleTemplate template,
        Func<SampleTemplate, CancellationToken, Task<string>> openAsync,
        Func<string, Task>? statusReporter = null,
        Func<Exception, Task>? errorReporter = null)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        _openAsync = openAsync ?? throw new ArgumentNullException(nameof(openAsync));
        _statusReporter = statusReporter ?? (_ => Task.CompletedTask);
        _errorReporter = errorReporter ?? (_ => Task.CompletedTask);

        _openCommand = new RelayCommand(_ => InvokeOpen(), _ => !IsOpening);
        OpenCommand = _openCommand;
    }

    public SampleTemplate Template { get; }

    public string Name => Template.Name;

    public string Description => Template.Description;

    public string AutomationName => $"Sample macro {Name}, {Description}";

    public bool IsOpening
    {
        get => _isOpening;
        private set
        {
            if (_isOpening == value)
            {
                return;
            }

            _isOpening = value;
            OnPropertyChanged();
            _openCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand OpenCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal Task? LastOpenTask { get; private set; }

    private void InvokeOpen()
    {
        if (IsOpening)
        {
            return;
        }

        LastOpenTask = InvokeOpenAsync();
    }

    private async Task InvokeOpenAsync()
    {
        try
        {
            IsOpening = true;
            string openedPath = await _openAsync(Template, CancellationToken.None);
            string openedName = Path.GetFileNameWithoutExtension(openedPath);
            await _statusReporter($"Macros: Opened sample \"{openedName}\"");
        }
        catch (Exception ex)
        {
            await _errorReporter(ex);
            await _statusReporter("Macros: Couldn't open the sample macro.");
        }
        finally
        {
            IsOpening = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
