using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Macros.Samples;

public sealed class SampleTemplateProvider
{
    private static readonly IReadOnlyList<SampleTemplate> Templates = Array.AsReadOnly(
    [
        new SampleTemplate(
            "Auto-collapse #region blocks on open",
            "Triggers on document open and collapses outlining only when the file contains #region blocks.",
            "collapse-regions-on-open.csx"),
        new SampleTemplate(
            "Format on save for C# files only",
            "Uses a Document.Saved trigger with a when filter so only C# files are formatted automatically.",
            "format-on-save-csharp.csx"),
        new SampleTemplate(
            "Insert file header",
            "Prompts for values and inserts a reusable multi-line header wherever the caret is.",
            "insert-file-header.csx"),
    ]);

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public IReadOnlyList<SampleTemplate> GetTemplates() => Templates;

    public Task<string> InstantiateAsync(SampleTemplate template, string targetFolder, CancellationToken cancellation = default)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            throw new ArgumentException("Target folder must be a non-empty path.", nameof(targetFolder));
        }

        string content = LoadTemplateContent(template.ResourceName);
        string baseName = Path.GetFileNameWithoutExtension(template.ResourceName);

        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetFolder);

            for (int suffix = 1; ; suffix++)
            {
                cancellation.ThrowIfCancellationRequested();

                string candidateName = suffix == 1 ? baseName : $"{baseName}-{suffix}";
                string destinationPath = Path.Combine(targetFolder, candidateName + ".csx");
                string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    File.WriteAllText(tempPath, content, Utf8WithBom);

                    if (File.Exists(destinationPath))
                    {
                        TryDeleteSilently(tempPath);
                        continue;
                    }

                    try
                    {
                        File.Move(tempPath, destinationPath);
                        return destinationPath;
                    }
                    catch (IOException) when (File.Exists(destinationPath))
                    {
                        TryDeleteSilently(tempPath);
                    }
                }
                catch
                {
                    TryDeleteSilently(tempPath);
                    throw;
                }
            }
        }, cancellation);
    }

    public Task<string> ExtractViewableCopyAsync(SampleTemplate template, CancellationToken cancellation = default)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        string content = LoadTemplateContent(template.ResourceName);
        string destinationPath = Path.Combine(GetViewableCopyFolder(), template.ResourceName);

        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            string? folder = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new InvalidOperationException("Could not resolve the sample view folder.");
            }

            Directory.CreateDirectory(folder);

            if (File.Exists(destinationPath))
            {
                File.SetAttributes(destinationPath, FileAttributes.Normal);
            }

            File.WriteAllText(destinationPath, content, Utf8WithBom);
            File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) | FileAttributes.ReadOnly);
            return destinationPath;
        }, cancellation);
    }

    private static string LoadTemplateContent(string resourceName)
    {
        Assembly assembly = typeof(SampleTemplateProvider).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded sample template was not found: {resourceName}");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string GetViewableCopyFolder()
    {
        string version = typeof(SampleTemplateProvider).Assembly.GetName().Version?.ToString() ?? "dev";
        return Path.Combine(Path.GetTempPath(), "Macros", "Samples", version);
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
