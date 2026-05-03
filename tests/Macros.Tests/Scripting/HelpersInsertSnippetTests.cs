using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Macros.Engine.Scripting;
using Microsoft.VisualStudio.Threading;
using Moq;
using Xunit;

namespace Macros.Tests.Scripting;

/// <summary>
/// Argument validation for <see cref="Helpers.InsertSnippetAsync"/>. The full happy-path
/// behaviour (typing the prefix and invoking <c>Edit.InsertSnippet</c>) is exercised by an
/// integration test in <c>Macros.IntegrationTests</c> against a hosted Visual Studio.
/// </summary>
public sealed class HelpersInsertSnippetTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InsertSnippetAsync_NullOrWhitespacePrefix_Throws(string? prefix)
    {
        // The validation runs before any helper crosses the UI thread, so we don't even need
        // the ambient globals — the ArgumentException is thrown unconditionally.
        await Assert.ThrowsAsync<ArgumentException>(() => Helpers.InsertSnippetAsync(prefix!));
    }

    [Fact]
    public async Task InsertSnippetAsync_NoAmbientGlobals_Throws()
    {
        // With a non-empty prefix we get past the argument check and hit the ambient-globals
        // requirement (TypeAsync's RequireGlobals call).
        Helpers.CurrentGlobals.Value = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Helpers.InsertSnippetAsync("prop"));
    }
}
