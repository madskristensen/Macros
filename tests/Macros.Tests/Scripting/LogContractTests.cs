using System;
using System.Linq;
using System.Reflection;
using Macros.Engine.Scripting;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Surface and pure-formatter tests for the <see cref="Log"/> helper. The async write paths
/// touch the toolkit's <c>VS.Windows.CreateOutputWindowPaneAsync</c>, which requires a hosted
/// VS process; that path can only be smoke-tested manually inside an experimental hive.
/// </summary>
public sealed class LogContractTests
{
    [Fact]
    public void Log_PublicSurfaceIsExactlyTheThreeSeverities()
    {
        // Lock the surface so accidentally adding a public method without updating the
        // codegen contract / docs fails the test loudly.
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
