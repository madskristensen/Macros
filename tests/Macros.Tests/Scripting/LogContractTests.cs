using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Macros.Engine.Scripting;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Contract + pure-formatter tests for the <see cref="Log"/> helper. The async write paths
/// touch the toolkit's <c>VS.Windows.CreateOutputWindowPaneAsync</c>, which requires a hosted
/// VS process; that's covered by integration tests, not here.
/// </summary>
public sealed class LogContractTests
{
    [Fact]
    public void Log_IsPublicStatic()
    {
        Type t = typeof(Log);
        Assert.True(t.IsPublic, "Log must be public.");
        Assert.True(t.IsAbstract && t.IsSealed, "Log must be a static class.");
    }

    [Theory]
    [InlineData(nameof(Log.InfoAsync))]
    [InlineData(nameof(Log.WarnAsync))]
    [InlineData(nameof(Log.ErrorAsync))]
    public void Log_PublicVerbsReturnTask(string methodName)
    {
        MethodInfo? method = typeof(Log).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True(method!.IsStatic, $"{methodName} must be static.");
        Assert.Equal(typeof(Task), method.ReturnType);

        ParameterInfo[] parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
    }

    [Fact]
    public void Log_PublicSurfaceIsExactlyTheThreeSeverities()
    {
        var expected = new[]
        {
            nameof(Log.InfoAsync),
            nameof(Log.WarnAsync),
            nameof(Log.ErrorAsync),
        };

        var actual = typeof(Log)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact]
    public void Format_IncludesTimestampSeverityAndMacroName()
    {
        // Reflection because Format is internal — the assembly already grants
        // InternalsVisibleTo("Macros.Tests").
        MethodInfo? format = typeof(Log).GetMethod(
            "Format",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(format);

        var ts = new DateTime(2026, 5, 3, 9, 8, 30, DateTimeKind.Local);
        string line = (string)format!.Invoke(null, new object?[] { ts, "INFO ", "fmt-on-save", "hello" })!;

        Assert.Contains("09:08:30", line);
        Assert.Contains("INFO", line);
        Assert.Contains("fmt-on-save", line);
        Assert.Contains("hello", line);
    }

    [Fact]
    public void Format_NullMacroNameAndMessageRenderSafely()
    {
        MethodInfo format = typeof(Log).GetMethod(
            "Format",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        string line = (string)format.Invoke(null, new object?[] { ts, "ERROR", null, null })!;

        Assert.Contains("<unknown>", line);
        Assert.Contains("<null>", line);
        Assert.Contains("ERROR", line);
    }
}
