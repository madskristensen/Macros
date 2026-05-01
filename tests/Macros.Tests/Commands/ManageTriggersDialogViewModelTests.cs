using System.Collections.Generic;
using System.Linq;
using Macros.Commands;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="ManageTriggersDialogViewModel"/>. The VM is pure MVVM (no VS
/// shell dependency) so every test runs in-process without a host.
/// </summary>
public sealed class ManageTriggersDialogViewModelTests
{
    private static ManageTriggersDialogViewModel NewVm(
        params TriggerBinding[] initial)
        => new("my-macro", initial.Length == 0
            ? new[] { TriggerBinding.Manual }
            : initial);

    [Fact]
    public void Construction_WithImplicitManual_StartsWithEmptyBindings()
    {
        var vm = NewVm();

        Assert.Empty(vm.Bindings);
        Assert.Equal("my-macro", vm.MacroName);
        Assert.Equal(TriggerKind.VsEvent, vm.NewTriggerKind);
    }

    [Fact]
    public void Construction_WithExplicitBindings_PopulatesBindings()
    {
        var vm = NewVm(
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"));

        Assert.Equal(2, vm.Bindings.Count);
        Assert.Equal("Build.SolutionBuildDone", vm.Bindings[0].Source.Name);
        Assert.Equal(TriggerKind.BeforeCommand, vm.Bindings[1].Source.Kind);
    }

    [Fact]
    public void AddVsEvent_BuildsCorrectBinding_AndAppendsToList()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Build.SolutionBuildDone";

        Assert.True(vm.CanAddTrigger);
        var added = vm.AddCurrentInputAsBinding();

        Assert.NotNull(added);
        Assert.Equal(TriggerKind.VsEvent, added!.Kind);
        Assert.Equal("Build.SolutionBuildDone", added.Name);
        Assert.Single(vm.Bindings);
    }

    [Fact]
    public void AddVsEvent_WithFilters_ParsesAndKeysCaseInsensitive()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Document.Saved";
        vm.NewTriggerFilters = "filename=*.cs and project=Foo";

        var added = vm.AddCurrentInputAsBinding();

        Assert.NotNull(added);
        Assert.Equal(2, added!.Filters.Count);
        Assert.Equal("*.cs", added.Filters["filename"]);
        Assert.Equal("Foo", added.Filters["project"]);
    }

    [Fact]
    public void AddVsEvent_WithSemicolonSeparator_AlsoParses()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Document.Saved";
        vm.NewTriggerFilters = "filename=*.cs;project=Foo";

        var added = vm.AddCurrentInputAsBinding();

        Assert.Equal(2, added!.Filters.Count);
    }

    [Fact]
    public void AddBeforeCommand_BuildsBindingWithName()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.BeforeCommand;
        vm.NewTriggerName = "File.Save";

        var added = vm.AddCurrentInputAsBinding();

        Assert.NotNull(added);
        Assert.Equal(TriggerKind.BeforeCommand, added!.Kind);
        Assert.Equal("File.Save", added.Name);
    }

    [Fact]
    public void EmptyName_VsEvent_CanAddTriggerFalse_WithMessage()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = string.Empty;

        Assert.False(vm.CanAddTrigger);
        Assert.Contains("required", vm.ValidationMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnqualifiedEventName_CanAddTriggerFalse_WithMessage()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "SomethingWithoutDot";

        Assert.False(vm.CanAddTrigger);
        Assert.Contains("qualified", vm.ValidationMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyName_BeforeCommand_CanAddTriggerFalse_WithMessage()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.BeforeCommand;
        vm.NewTriggerName = "   ";

        Assert.False(vm.CanAddTrigger);
        Assert.Contains("required", vm.ValidationMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateBinding_CanAddTriggerFalse_AlreadyAddedMessage()
    {
        var vm = NewVm(new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Build.SolutionBuildDone";

        Assert.False(vm.CanAddTrigger);
        Assert.Equal("Already added.", vm.ValidationMessage);
    }

    [Fact]
    public void Add_ResetsNameAndFilters_KeepsKind()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Build.SolutionBuildDone";
        vm.NewTriggerFilters = "x=1";

        vm.AddCurrentInputAsBinding();

        Assert.Equal(string.Empty, vm.NewTriggerName);
        Assert.Equal(string.Empty, vm.NewTriggerFilters);
        Assert.Equal(TriggerKind.VsEvent, vm.NewTriggerKind);
    }

    [Fact]
    public void RemoveCommand_RemovesItem_AndReenablesDuplicateInput()
    {
        var vm = NewVm(new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"));
        vm.NewTriggerKind = TriggerKind.VsEvent;
        vm.NewTriggerName = "Build.SolutionBuildDone";

        Assert.False(vm.CanAddTrigger); // duplicate

        var only = vm.Bindings[0];
        only.RemoveCommand.Execute(null);

        Assert.Empty(vm.Bindings);
        Assert.True(vm.CanAddTrigger);
    }

    [Fact]
    public void ManualKind_HidesNameAndFiltersInputs()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.Manual;

        Assert.False(vm.IsNameRequired);
        Assert.False(vm.AreFiltersAvailable);
        Assert.True(vm.CanAddTrigger);
    }

    [Fact]
    public void VsEventKind_ShowsBothInputs()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;

        Assert.True(vm.IsNameRequired);
        Assert.True(vm.AreFiltersAvailable);
    }

    [Fact]
    public void BeforeCommandKind_ShowsNameButNotFilters()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.BeforeCommand;

        Assert.True(vm.IsNameRequired);
        Assert.False(vm.AreFiltersAvailable);
    }

    [Fact]
    public void ToBindingsList_PreservesDeclaredOrder()
    {
        var vm = NewVm(
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(TriggerKind.AfterCommand, "Build.BuildSolution"));

        var list = vm.ToBindingsList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Build.SolutionBuildDone", list[0].Name);
        Assert.Equal("Build.BuildSolution", list[1].Name);
    }

    [Fact]
    public void PropertyChanged_FiresForCanAddAndValidationMessage_OnNameChange()
    {
        var vm = NewVm();
        vm.NewTriggerKind = TriggerKind.VsEvent;

        var fired = new List<string>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName!);

        // Empty → invalid → "required" message + CanAdd=false
        vm.NewTriggerName = "X"; // invalid (no dot)
        // Then valid
        vm.NewTriggerName = "Build.SolutionBuildDone";

        Assert.Contains(nameof(vm.NewTriggerName), fired);
        Assert.Contains(nameof(vm.ValidationMessage), fired);
        Assert.Contains(nameof(vm.CanAddTrigger), fired);
    }
}
