using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Macros.Options;

public partial class TrustedSolutionsPageControl : UserControl
{
    public TrustedSolutionsPageControl()
    {
        InitializeComponent();
    }

    internal void LoadFrom(MacrosOptions options)
    {
        TrustedList.Items.Clear();
        BlockedList.Items.Clear();

        foreach (var p in Split(options.TrustedSolutions))
            TrustedList.Items.Add(p);

        foreach (var p in Split(options.BlockedSolutions))
            BlockedList.Items.Add(p);
    }

    internal void SaveTo(MacrosOptions options)
    {
        options.TrustedSolutions = Join(TrustedList.Items);
        options.BlockedSolutions = Join(BlockedList.Items);
    }

    private void AddTrusted_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseForSolution();
        if (path is null) return;
        AddUnique(TrustedList, path);
        RemoveItem(BlockedList, path);
    }

    private void AddBlocked_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseForSolution();
        if (path is null) return;
        AddUnique(BlockedList, path);
        RemoveItem(TrustedList, path);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelected(TrustedList);
        RemoveSelected(BlockedList);
    }

    private void MoveToBlocked_Click(object sender, RoutedEventArgs e)
        => MoveSelected(TrustedList, BlockedList);

    private void MoveToTrusted_Click(object sender, RoutedEventArgs e)
        => MoveSelected(BlockedList, TrustedList);

    // ── helpers ────────────────────────────────────────────────────────────

    private static string? BrowseForSolution()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select solution file",
            Filter = "Solution files (*.sln)|*.sln|All files (*.*)|*.*",
            Multiselect = false
        };
        return dlg.ShowDialog() == true ? Canonicalize(dlg.FileName) : null;
    }

    private static void AddUnique(ListBox list, string path)
    {
        foreach (string item in list.Items)
            if (string.Equals(item, path, System.StringComparison.OrdinalIgnoreCase))
                return;
        list.Items.Add(path);
    }

    private static void RemoveItem(ListBox list, string path)
    {
        for (int i = list.Items.Count - 1; i >= 0; i--)
            if (string.Equals((string)list.Items[i], path, System.StringComparison.OrdinalIgnoreCase))
                list.Items.RemoveAt(i);
    }

    private static void RemoveSelected(ListBox list)
    {
        foreach (var item in list.SelectedItems.Cast<string>().ToList())
            list.Items.Remove(item);
    }

    private static void MoveSelected(ListBox source, ListBox target)
    {
        foreach (var item in source.SelectedItems.Cast<string>().ToList())
        {
            source.Items.Remove(item);
            AddUnique(target, item);
        }
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries);

    private static string Join(ItemCollection items) =>
        string.Join(";", items.Cast<string>());

    private static string Canonicalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
