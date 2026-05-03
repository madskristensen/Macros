using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Macros.Samples;
using Xunit;

namespace Macros.Tests.Samples;

public sealed class SampleTemplateProviderTests : IDisposable
{
    private readonly string _workspaceRoot = Path.Combine(
        AppContext.BaseDirectory,
        "sample-template-provider-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetTemplates_ReturnsAllEmbeddedTemplates()
    {
        var provider = new SampleTemplateProvider();

        var resources = provider.GetTemplates().Select(t => t.ResourceName).ToArray();

        Assert.Equal(15, resources.Length);
        Assert.Contains("collapse-regions-on-open.csx", resources);
        Assert.Contains("format-on-save-csharp.csx", resources);
        Assert.Contains("insert-file-header.csx", resources);
        Assert.Contains("sort-selected-lines.csx", resources);
        Assert.Contains("show-build-error-count.csx", resources);
        Assert.Contains("log-document-path.csx", resources);
    }

    [Fact]
    public async Task InstantiateAsync_WhenNameExists_AppendsNumber()
    {
        Directory.CreateDirectory(_workspaceRoot);
        var provider = new SampleTemplateProvider();
        var template = provider.GetTemplates().Single(t => t.ResourceName == "collapse-regions-on-open.csx");

        string firstPath = await provider.InstantiateAsync(template, _workspaceRoot);
        string secondPath = await provider.InstantiateAsync(template, _workspaceRoot);

        Assert.Equal(Path.Combine(_workspaceRoot, "collapse-regions-on-open.csx"), firstPath);
        Assert.Equal(Path.Combine(_workspaceRoot, "collapse-regions-on-open-2.csx"), secondPath);

        string source = File.ReadAllText(secondPath);
        Assert.Contains("// Collapses outlining in newly opened documents", source);
        Assert.Contains("// @trigger Document.Opened", source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }
}
