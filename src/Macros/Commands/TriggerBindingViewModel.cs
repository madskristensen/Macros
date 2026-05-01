using System;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Macros.Engine.Triggers;
using Macros.Mvvm;

namespace Macros.Commands;

/// <summary>
/// View-model wrapper around a single <see cref="TriggerBinding"/> for display in the
/// "Manage Triggers..." dialog's <c>ListBox</c>. Exposes a one-line
/// <see cref="DisplaySummary"/> for the row text, a more verbose
/// <see cref="DisplayDetail"/> for the row tooltip, and a <see cref="RemoveCommand"/> bound
/// to the parent VM so each row can self-delete.
/// </summary>
internal sealed class TriggerBindingViewModel
{
    /// <summary>The underlying binding. Immutable after construction.</summary>
    public TriggerBinding Source { get; }

    /// <summary>Compact one-line description, suitable for a <c>ListBox</c> row.</summary>
    public string DisplaySummary { get; }

    /// <summary>Verbose multi-line description, suitable for a <c>ToolTip</c>.</summary>
    public string DisplayDetail { get; }

    /// <summary>Command bound to the per-row "Remove" button. Pops this row off the parent VM.</summary>
    public ICommand RemoveCommand { get; }

    public TriggerBindingViewModel(TriggerBinding source, Action<TriggerBindingViewModel> onRemove)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (onRemove is null) throw new ArgumentNullException(nameof(onRemove));

        DisplaySummary = BuildSummary(source);
        DisplayDetail = BuildDetail(source);
        RemoveCommand = new RelayCommand(() => onRemove(this));
    }

    private static string BuildSummary(TriggerBinding b) => b.Kind switch
    {
        TriggerKind.Manual => "Manual",
        TriggerKind.BeforeCommand => $"Before command: {b.Name}",
        TriggerKind.AfterCommand => $"After command: {b.Name}",
        TriggerKind.VsEvent => b.Filters.Count == 0
            ? $"Event: {b.Name}"
            : $"Event: {b.Name} when {RenderFilters(b)}",
        _ => b.Name,
    };

    private static string BuildDetail(TriggerBinding b)
    {
        var sb = new StringBuilder();
        sb.Append("Kind: ").AppendLine(b.Kind.ToString());
        if (b.Kind != TriggerKind.Manual)
        {
            sb.Append("Name: ").AppendLine(b.Name);
        }
        if (b.Filters.Count > 0)
        {
            sb.Append("Filters: ").Append(RenderFilters(b));
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderFilters(TriggerBinding b)
        => string.Join(" and ", b.Filters.Select(kv => $"{kv.Key}={kv.Value}"));
}
