using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Macros.Skills;
using Xunit;

namespace Macros.Tests.Skills;

/// <summary>
/// Tests for <see cref="SkillInstaller"/>. Covers the version-marker round-trip (write
/// at the end, read from the end), the legacy-format migration (top marker is ignored
/// and triggers a one-shot rewrite), and a static check that every embedded SKILL.md
/// resource has the canonical Copilot CLI frontmatter shape — `---` on line 1, exactly
/// `name` and `description` keys, no leading HTML comment.
/// </summary>
public sealed class SkillInstallerTests
{
    private static readonly string[] BundledResourceNames =
    {
        "Macros.Skills.Content.writing-macros.md",
        "Macros.Skills.Content.macro-triggers.md",
        "Macros.Skills.Content.macro-dte-api.md",
        "Macros.Skills.Content.macro-toolkit-api.md",
        "Macros.Skills.Content.macro-debugging.md",
    };

    [Fact]
    public void BuildInstalledContent_PlacesMarkerOnLastNonEmptyLine()
    {
        const string body = "---\nname: x\ndescription: y\n---\n\n# Heading\n\nBody text.\n";

        string output = SkillInstaller.BuildInstalledContent(body, "1.2.3.4");

        // The output must START with the YAML frontmatter so the Copilot CLI loader can parse it.
        Assert.StartsWith("---", output);

        // The output must END with the version marker on the last non-empty line.
        string trimmed = output.TrimEnd('\r', '\n');
        int lastNewline = trimmed.LastIndexOfAny(new[] { '\n', '\r' });
        string lastLine = lastNewline >= 0 ? trimmed.Substring(lastNewline + 1) : trimmed;
        Assert.Equal("<!-- macros-extension-version: 1.2.3.4 -->", lastLine);
    }

    [Fact]
    public void ExtractTrailingMarkerVersion_ReadsMarkerWrittenAtEnd()
    {
        const string body = "---\nname: x\ndescription: y\n---\n\n# Body\n";
        string written = SkillInstaller.BuildInstalledContent(body, "9.9.9.9");

        Version? parsed = SkillInstaller.ExtractTrailingMarkerVersion(written);

        Assert.NotNull(parsed);
        Assert.Equal(new Version(9, 9, 9, 9), parsed);
    }

    [Fact]
    public void ExtractTrailingMarkerVersion_LegacyTopMarker_ReturnsNull()
    {
        // Old format: marker on line 1. New reader must NOT treat this as a valid marker —
        // returning null forces a one-shot rewrite into the new format on extension upgrade.
        const string legacy = "<!-- macros-extension-version: 0.5.0 -->\n\n---\nname: x\ndescription: y\n---\n\n# Body text without trailing marker.\n";

        Version? parsed = SkillInstaller.ExtractTrailingMarkerVersion(legacy);

        Assert.Null(parsed);
    }

    [Fact]
    public void ExtractTrailingMarkerVersion_NoMarker_ReturnsNull()
    {
        const string content = "---\nname: x\ndescription: y\n---\n\n# Just a body, no marker anywhere.\n";

        Version? parsed = SkillInstaller.ExtractTrailingMarkerVersion(content);

        Assert.Null(parsed);
    }

    [Fact]
    public void BuildInstalledContent_StripsLegacyTopMarkerBeforeAppending()
    {
        // If the bundled resource still has a stray legacy top-marker, the writer must strip
        // it so we don't double-write the marker. Idempotency under repeated installs.
        const string legacyBundled = "<!-- macros-extension-version: 0.0.0 -->\n\n---\nname: x\ndescription: y\n---\n\n# Body.\n";

        string output = SkillInstaller.BuildInstalledContent(legacyBundled, "2.0.0.0");

        // Exactly one marker line in the output, and it carries the new version.
        var markers = Regex.Matches(output, @"<!-- macros-extension-version: ([^\s]+) -->");
        Assert.Single(markers);
        Assert.Equal("2.0.0.0", markers[0].Groups[1].Value);
        // Frontmatter must remain on line 1.
        Assert.StartsWith("---", output);
    }

    [Fact]
    public void BuildInstalledContent_StripsExistingTrailingMarkerBeforeAppending()
    {
        // If the bundled resource already has a trailing marker (e.g. a developer pasted one
        // in by mistake), the writer must strip it before appending the current one.
        const string content = "---\nname: x\ndescription: y\n---\n\n# Body.\n\n<!-- macros-extension-version: 1.0.0 -->\n";

        string output = SkillInstaller.BuildInstalledContent(content, "2.0.0.0");

        var markers = Regex.Matches(output, @"<!-- macros-extension-version: ([^\s]+) -->");
        Assert.Single(markers);
        Assert.Equal("2.0.0.0", markers[0].Groups[1].Value);
    }

    [Theory]
    [MemberData(nameof(BundledResources))]
    public void BundledResource_HasCanonicalFrontmatter(string resourceName)
    {
        string content = ReadEmbeddedResource(resourceName);

        // Line 1 must be `---` — the Copilot CLI skills loader is strict about this.
        // No leading HTML comment, no BOM (or BOM stripped before the check).
        string trimmed = content.TrimStart('\uFEFF');
        Assert.StartsWith("---\n", trimmed.Replace("\r\n", "\n"));
    }

    [Theory]
    [MemberData(nameof(BundledResources))]
    public void BundledResource_FrontmatterContainsNameAndDescription(string resourceName)
    {
        string content = ReadEmbeddedResource(resourceName);

        // Extract the YAML block between the first two `---` lines.
        var fm = ExtractFrontmatter(content);
        Assert.True(fm.Contains("name:"), $"{resourceName} is missing a `name:` key.");
        Assert.True(fm.Contains("description:"), $"{resourceName} is missing a `description:` key.");
    }

    [Theory]
    [MemberData(nameof(BundledResources))]
    public void BundledResource_FrontmatterDoesNotUseQuotedScalars(string resourceName)
    {
        // The canonical format from .copilot/skills/* uses unquoted YAML scalars. Quoted
        // scalars work too, but unquoted is the convention in every working skill we've seen.
        // This isn't strictly necessary but flags drift in code review.
        string content = ReadEmbeddedResource(resourceName);
        var fm = ExtractFrontmatter(content);

        Assert.DoesNotContain("name: \"", fm);
        Assert.DoesNotContain("description: \"", fm);
    }

    [Theory]
    [MemberData(nameof(BundledResources))]
    public void BundledResource_FrontmatterRejectsUnsupportedKeys(string resourceName)
    {
        // The `name`/`description` pair is the entire supported surface. Custom keys we used
        // to ship (domain, confidence, source) caused the loader to reject the skill silently.
        string content = ReadEmbeddedResource(resourceName);
        var fm = ExtractFrontmatter(content);

        foreach (string banned in new[] { "domain:", "confidence:", "source:", "when_to_use:" })
        {
            Assert.False(fm.Contains(banned),
                $"{resourceName} contains unsupported frontmatter key '{banned.TrimEnd(':')}'.");
        }
    }

    [Theory]
    [MemberData(nameof(BundledResources))]
    public void BundledResource_DescriptionMentionsUseWhenTriggers(string resourceName)
    {
        // The skills router uses the description text to decide which skill to load. Skills
        // that don't include "Use when ..." trigger phrases are functionally invisible — they
        // load on disk but never surface from a user prompt. Guarding against drift here.
        string content = ReadEmbeddedResource(resourceName);
        var fm = ExtractFrontmatter(content);

        // Match either "Use when" or "Use this" as routing-friendly trigger phrases.
        Assert.True(
            Regex.IsMatch(fm, @"\bUse when\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(fm, @"\bUse this\b", RegexOptions.IgnoreCase),
            $"{resourceName}'s description must include a `Use when ...` (or `Use this ...`) trigger clause for skills routing.");
    }

    public static System.Collections.Generic.IEnumerable<object[]> BundledResources()
    {
        foreach (string n in BundledResourceNames)
        {
            yield return new object[] { n };
        }
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        // The Macros assembly hosts SkillInstaller; embedded resources are nested under it.
        Assembly asm = typeof(SkillInstaller).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ExtractFrontmatter(string content)
    {
        string normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
        {
            return string.Empty;
        }

        int second = normalized.IndexOf("\n---", 4);
        if (second < 0)
        {
            return string.Empty;
        }

        return normalized.Substring(4, second - 4);
    }
}
