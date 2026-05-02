using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace Macros.Skills;

internal static class SkillInstaller
{
    private const string SkillFileName = "SKILL.md";
    private const string VersionMarkerPrefix = "<!-- macros-extension-version: ";
    private const string VersionMarkerSuffix = " -->";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex VersionMarkerRegex = new(
        @"^\s*<!--\s*macros-extension-version:\s*(?<version>[^\s]+)\s*-->\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly SkillResource[] BundledSkills =
    {
        new("writing-macros", "Macros.Skills.Content.writing-macros.md"),
        new("macro-triggers", "Macros.Skills.Content.macro-triggers.md"),
        new("macro-dte-api", "Macros.Skills.Content.macro-dte-api.md"),
        new("macro-toolkit-api", "Macros.Skills.Content.macro-toolkit-api.md"),
        new("macro-debugging", "Macros.Skills.Content.macro-debugging.md"),
    };

    public static void InstallAsync(JoinableTaskFactory joinableTaskFactory)
    {
        _ = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));

        joinableTaskFactory.RunAsync(InstallCoreAsync)
            .FileAndForget("Macros/Skills/Install");
    }

    private static async Task InstallCoreAsync()
    {
        await TaskScheduler.Default;

        try
        {
            string skillRoot = GetSkillRoot();
            Directory.CreateDirectory(skillRoot);

            Version bundledVersion = GetBundledVersion();
            string bundledVersionText = FormatVersion(bundledVersion);
            Assembly assembly = typeof(SkillInstaller).Assembly;

            foreach (SkillResource skill in BundledSkills)
            {
                await InstallSkillAsync(
                    assembly,
                    skillRoot,
                    bundledVersion,
                    bundledVersionText,
                    skill).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
        {
            await ex.LogAsync().ConfigureAwait(false);
        }
    }

    private static async Task InstallSkillAsync(
        Assembly assembly,
        string skillRoot,
        Version bundledVersion,
        string bundledVersionText,
        SkillResource skill)
    {
        try
        {
            string targetDirectory = Path.Combine(skillRoot, skill.Name);
            string targetFilePath = Path.Combine(targetDirectory, SkillFileName);

            if (File.Exists(targetFilePath))
            {
                Version? installedVersion = await ReadInstalledVersionAsync(targetFilePath).ConfigureAwait(false);
                if (installedVersion is not null && installedVersion >= bundledVersion)
                {
                    return;
                }
            }

            string? bundledContent = await ReadEmbeddedContentAsync(assembly, skill.ResourceName).ConfigureAwait(false);
            if (bundledContent is null || bundledContent.Length == 0)
            {
                return;
            }

            string installedContent = BuildInstalledContent(bundledContent, bundledVersionText);
            Directory.CreateDirectory(targetDirectory);
            await WriteAllTextAsync(targetFilePath, installedContent).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException)
        {
            await ex.LogAsync().ConfigureAwait(false);
        }
    }

    private static string GetSkillRoot()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".copilot", "skills");
    }

    private static Version GetBundledVersion()
    {
        if (Version.TryParse(Vsix.Version, out Version? manifestVersion))
        {
            return NormalizeVersion(manifestVersion);
        }

        Version? assemblyVersion = typeof(MacrosPackage).Assembly.GetName().Version;
        return NormalizeVersion(assemblyVersion ?? new Version(1, 0, 0, 0));
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            version.Build >= 0 ? version.Build : 0,
            version.Revision >= 0 ? version.Revision : 0);
    }

    private static string FormatVersion(Version version)
    {
        if (version.Revision == 0)
        {
            return version.Build == 0
                ? version.ToString(3)
                : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.0", version.Major, version.Minor, version.Build);
        }

        return version.ToString();
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value, out Version? version))
        {
            return null;
        }

        return NormalizeVersion(version);
    }

    private static async Task<string?> ReadEmbeddedContentAsync(Assembly assembly, string resourceName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            await new InvalidOperationException($"Missing embedded skill resource '{resourceName}'.")
                .LogAsync()
                .ConfigureAwait(false);
            return null;
        }

        using StreamReader reader = new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task<Version?> ReadInstalledVersionAsync(string filePath)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using StreamReader reader = new(fileStream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        string? firstLine = await reader.ReadLineAsync().ConfigureAwait(false);
        return ParseVersionMarker(firstLine);
    }

    private static async Task WriteAllTextAsync(string filePath, string content)
    {
        using FileStream fileStream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        using StreamWriter writer = new(fileStream, Utf8NoBom);
        await writer.WriteAsync(content).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static string BuildInstalledContent(string rawContent, string versionText)
    {
        string trimmedContent = StripVersionMarker(rawContent).TrimStart('\uFEFF', '\r', '\n');
        return string.Concat(VersionMarkerPrefix, versionText, VersionMarkerSuffix, Environment.NewLine, Environment.NewLine, trimmedContent);
    }

    private static string StripVersionMarker(string content)
    {
        using StringReader reader = new(content);
        string? firstLine = reader.ReadLine();
        if (!VersionMarkerRegex.IsMatch(firstLine ?? string.Empty))
        {
            return content;
        }

        string remainder = reader.ReadToEnd();
        return remainder.TrimStart('\r', '\n');
    }

    private static Version? ParseVersionMarker(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        Match match = VersionMarkerRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return ParseVersion(match.Groups["version"].Value);
    }

    private readonly struct SkillResource
    {
        public SkillResource(string name, string resourceName)
        {
            Name = name;
            ResourceName = resourceName;
        }

        public string Name { get; }

        public string ResourceName { get; }
    }
}
