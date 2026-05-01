using System;
using Macros.Mvvm;
using Xunit;

namespace Macros.Tests.Mvvm;

/// <summary>
/// Verifies the MVVM <see cref="RelayCommand"/> wrapper used by every M3 tool-window VM.
/// </summary>
public sealed class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesAction_WithParameter()
    {
        object? observed = null;
        var cmd = new RelayCommand(p => observed = p);

        cmd.Execute("hello");

        Assert.Equal("hello", observed);
    }

    [Fact]
    public void Execute_ParameterlessOverload_InvokesAction()
    {
        int hits = 0;
        var cmd = new RelayCommand(() => hits++);

        cmd.Execute(parameter: null);
        cmd.Execute(parameter: 42);

        Assert.Equal(2, hits);
    }

    [Fact]
    public void CanExecute_NoPredicate_ReturnsTrue()
    {
        var cmd = new RelayCommand(_ => { });

        Assert.True(cmd.CanExecute(null));
        Assert.True(cmd.CanExecute(new object()));
    }

    [Fact]
    public void CanExecute_WithPredicate_DelegatesToPredicate()
    {
        var cmd = new RelayCommand(_ => { }, p => p is string);

        Assert.True(cmd.CanExecute("foo"));
        Assert.False(cmd.CanExecute(123));
    }

    [Fact]
    public void RaiseCanExecuteChanged_FiresEvent()
    {
        var cmd = new RelayCommand(_ => { });
        int fired = 0;
        cmd.CanExecuteChanged += (_, _) => fired++;

        cmd.RaiseCanExecuteChanged();
        cmd.RaiseCanExecuteChanged();

        Assert.Equal(2, fired);
    }

    [Fact]
    public void RaiseCanExecuteChanged_NoSubscribers_DoesNotThrow()
    {
        var cmd = new RelayCommand(_ => { });

        // Should silently no-op; the null-conditional dispatch protects against
        // raise-without-subscribers crashes that bit M2 buttons.
        cmd.RaiseCanExecuteChanged();
    }

    [Fact]
    public void Constructor_NullExecute_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayCommand((Action<object?>)null!));
        Assert.Throws<ArgumentNullException>(() => new RelayCommand((Action)null!));
    }

    [Fact]
    public void ParameterlessCanExecute_DelegatesToFuncBool()
    {
        bool gate = false;
        var cmd = new RelayCommand(() => { }, () => gate);

        Assert.False(cmd.CanExecute(null));
        gate = true;
        Assert.True(cmd.CanExecute(null));
    }
}
