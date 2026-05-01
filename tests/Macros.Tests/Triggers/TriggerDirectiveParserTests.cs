using System.Collections.Generic;
using System.Linq;
using Macros.Engine.Triggers;
using Xunit;

namespace Macros.Tests.Triggers;

/// <summary>
/// Pins the <c>@trigger</c> directive grammar parsed by <see cref="TriggerDirectiveParser"/>.
/// The parser is a pure string transformation, so every supported form, every defaulting
/// rule, and every "stop scanning here" boundary is exercised here. Downstream code
/// (M4 trigger registry, M4 event bus, M4 command dispatcher) treats the parser's output
/// as the canonical user contract — anything not asserted here is unspecified behaviour.
/// </summary>
public sealed class TriggerDirectiveParserTests
{
    [Fact]
    public void Parse_NullSource_ReturnsManual()
    {
        var result = TriggerDirectiveParser.Parse(null);
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_EmptySource_ReturnsManual()
    {
        var result = TriggerDirectiveParser.Parse(string.Empty);
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_NoDirectiveLines_ReturnsManual()
    {
        const string src =
            "// Macro: Foo\n" +
            "// Recorded: 2026-04-30T16:00:00Z\n" +
            "using System;\n";
        var result = TriggerDirectiveParser.Parse(src);
        Assert.Single(result);
        Assert.Equal(TriggerKind.Manual, result[0].Kind);
        Assert.Equal("Manual", result[0].Name);
    }

    [Fact]
    public void Parse_ExplicitManualDirective_ReturnsManual()
    {
        var result = TriggerDirectiveParser.Parse("// @trigger Manual\n");
        Assert.Single(result);
        Assert.Equal(TriggerKind.Manual, result[0].Kind);
        Assert.Equal("Manual", result[0].Name);
        Assert.Empty(result[0].Filters);
    }

    [Fact]
    public void Parse_VsEventNoFilters_BindsName()
    {
        var result = TriggerDirectiveParser.Parse("// @trigger Build.SolutionBuildDone\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
        Assert.Empty(binding.Filters);
    }

    [Fact]
    public void Parse_VsEventOneFilter_BindsFilter()
    {
        var result = TriggerDirectiveParser.Parse(
            "// @trigger Document.Saved when filename=*.cs\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Equal("Document.Saved", binding.Name);
        Assert.Single(binding.Filters);
        Assert.Equal("*.cs", binding.Filters["filename"]);
    }

    [Fact]
    public void Parse_VsEventMultipleFilters_BindsAll()
    {
        var result = TriggerDirectiveParser.Parse(
            "// @trigger Build.SolutionBuildDone when project=Foo and platform=x64\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
        Assert.Equal(2, binding.Filters.Count);
        Assert.Equal("Foo", binding.Filters["project"]);
        Assert.Equal("x64", binding.Filters["platform"]);
    }

    [Fact]
    public void Parse_BeforeCommand_BindsName()
    {
        var result = TriggerDirectiveParser.Parse("// @trigger BeforeCommand File.Save\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.BeforeCommand, binding.Kind);
        Assert.Equal("File.Save", binding.Name);
        Assert.Empty(binding.Filters);
    }

    [Fact]
    public void Parse_AfterCommand_BindsName()
    {
        var result = TriggerDirectiveParser.Parse(
            "// @trigger AfterCommand Build.BuildSolution\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.AfterCommand, binding.Kind);
        Assert.Equal("Build.BuildSolution", binding.Name);
        Assert.Empty(binding.Filters);
    }

    [Fact]
    public void Parse_MultipleDirectives_PreservesDeclaredOrder()
    {
        const string src =
            "// Macro: Auto-Format\n" +
            "// @trigger Build.SolutionBuildDone\n" +
            "// @trigger Document.Saved when filename=*.cs\n" +
            "// @trigger BeforeCommand File.Save\n" +
            "// @trigger AfterCommand Build.BuildSolution\n" +
            "using System;\n";

        var result = TriggerDirectiveParser.Parse(src);
        Assert.Equal(4, result.Count);
        Assert.Equal(TriggerKind.VsEvent, result[0].Kind);
        Assert.Equal("Build.SolutionBuildDone", result[0].Name);
        Assert.Equal(TriggerKind.VsEvent, result[1].Kind);
        Assert.Equal("Document.Saved", result[1].Name);
        Assert.Equal("*.cs", result[1].Filters["filename"]);
        Assert.Equal(TriggerKind.BeforeCommand, result[2].Kind);
        Assert.Equal("File.Save", result[2].Name);
        Assert.Equal(TriggerKind.AfterCommand, result[3].Kind);
        Assert.Equal("Build.BuildSolution", result[3].Name);
    }

    [Fact]
    public void Parse_RegularComments_AreIgnored()
    {
        const string src =
            "// Macro: Foo\n" +
            "// Recorded: 2026-04-30T16:00:00Z\n" +
            "// Steps: 5\n" +
            "// @trigger Build.SolutionBuildDone\n";

        var result = TriggerDirectiveParser.Parse(src);
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
    }

    [Fact]
    public void Parse_TriggeredTypo_IsNotADirective()
    {
        // "@triggered" must not be picked up as the keyword, otherwise typos silently
        // bind unintended events. Only "@trigger" followed by whitespace counts.
        var result = TriggerDirectiveParser.Parse(
            "// @triggered Build.SolutionBuildDone\n");
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_StopsAtFirstNonCommentLine()
    {
        // "using System;" must terminate the comment-block scan: any @trigger directive
        // appearing after code is body, not header, and must not be parsed.
        const string src =
            "// @trigger Build.SolutionBuildDone\n" +
            "using System;\n" +
            "// @trigger Document.Saved when filename=*.cs\n";

        var result = TriggerDirectiveParser.Parse(src);
        var binding = Assert.Single(result);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
    }

    [Fact]
    public void Parse_BlankLinesBetweenDirectives_AreTolerated()
    {
        const string src =
            "// @trigger Build.SolutionBuildDone\n" +
            "\n" +
            "   \n" +
            "// @trigger BeforeCommand File.Save\n";

        var result = TriggerDirectiveParser.Parse(src);
        Assert.Equal(2, result.Count);
        Assert.Equal("Build.SolutionBuildDone", result[0].Name);
        Assert.Equal(TriggerKind.BeforeCommand, result[1].Kind);
        Assert.Equal("File.Save", result[1].Name);
    }

    [Fact]
    public void Parse_WhitespaceInsideDirective_IsNormalized()
    {
        var result = TriggerDirectiveParser.Parse(
            "//    @trigger    Build.SolutionBuildDone   \n");
        var binding = Assert.Single(result);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
    }

    [Fact]
    public void Parse_KeywordIsCaseInsensitive()
    {
        var result = TriggerDirectiveParser.Parse(
            "// @TRIGGER Build.SolutionBuildDone WHEN project=Foo AND platform=x64\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Equal("Build.SolutionBuildDone", binding.Name);
        Assert.Equal("Foo", binding.Filters["project"]);
        Assert.Equal("x64", binding.Filters["platform"]);
    }

    [Fact]
    public void Parse_VsEventWithoutDot_IsRejected()
    {
        // A VS event name must be "Category.EventName" — a single bareword has no
        // category and is unmappable to VS.Events, so reject it (falls back to Manual).
        var result = TriggerDirectiveParser.Parse("// @trigger BogusBareword\n");
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_MalformedFilters_AreSilentlyDropped()
    {
        // "noequals" has no '=' → dropped. "=onlyrhs" has empty key → dropped.
        // The valid "platform=x64" survives, demonstrating per-pair tolerance.
        var result = TriggerDirectiveParser.Parse(
            "// @trigger Build.Done when noequals and =onlyrhs and platform=x64\n");
        var binding = Assert.Single(result);
        Assert.Equal(TriggerKind.VsEvent, binding.Kind);
        Assert.Single(binding.Filters);
        Assert.Equal("x64", binding.Filters["platform"]);
    }

    [Fact]
    public void Parse_BeforeCommandWithNoName_IsRejected()
    {
        var result = TriggerDirectiveParser.Parse("// @trigger BeforeCommand\n");
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_HandlesCrlfAndLfLineEndings()
    {
        const string srcCrLf =
            "// @trigger Build.SolutionBuildDone\r\n" +
            "// @trigger BeforeCommand File.Save\r\n" +
            "using System;\r\n";

        var result = TriggerDirectiveParser.Parse(srcCrLf);
        Assert.Equal(2, result.Count);
        Assert.Equal("Build.SolutionBuildDone", result[0].Name);
        Assert.Equal("File.Save", result[1].Name);
    }

    [Fact]
    public void Render_RoundTripsThroughParse()
    {
        var original = new[]
        {
            new TriggerBinding(TriggerKind.VsEvent, "Build.SolutionBuildDone"),
            new TriggerBinding(
                TriggerKind.VsEvent,
                "Document.Saved",
                new Dictionary<string, string> { ["filename"] = "*.cs" }),
            new TriggerBinding(TriggerKind.BeforeCommand, "File.Save"),
            new TriggerBinding(TriggerKind.AfterCommand, "Build.BuildSolution"),
            TriggerBinding.Manual,
        };

        var rendered = TriggerDirectiveParser.Render(original);
        var reparsed = TriggerDirectiveParser.Parse(rendered);

        Assert.Equal(original.Length, reparsed.Count);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i], reparsed[i]);
        }
    }

    [Fact]
    public void Render_VsEventWithMultipleFilters_UsesAndSeparator()
    {
        var bindings = new[]
        {
            new TriggerBinding(
                TriggerKind.VsEvent,
                "Build.SolutionBuildDone",
                new Dictionary<string, string> { ["project"] = "Foo", ["platform"] = "x64" }),
        };

        var rendered = TriggerDirectiveParser.Render(bindings);

        Assert.Contains("// @trigger Build.SolutionBuildDone when ", rendered);
        Assert.Contains("project=Foo", rendered);
        Assert.Contains("platform=x64", rendered);
        Assert.Contains(" and ", rendered);

        // Round-trip preserves values regardless of dictionary iteration order.
        var reparsed = TriggerDirectiveParser.Parse(rendered);
        var binding = Assert.Single(reparsed);
        Assert.Equal("Foo", binding.Filters["project"]);
        Assert.Equal("x64", binding.Filters["platform"]);
    }

    [Fact]
    public void TriggerBinding_Equals_IsStructural_OverFilters()
    {
        var a = new TriggerBinding(
            TriggerKind.VsEvent,
            "Document.Saved",
            new Dictionary<string, string> { ["filename"] = "*.cs" });
        var b = new TriggerBinding(
            TriggerKind.VsEvent,
            "Document.Saved",
            new Dictionary<string, string> { ["filename"] = "*.cs" });
        var c = new TriggerBinding(
            TriggerKind.VsEvent,
            "Document.Saved",
            new Dictionary<string, string> { ["filename"] = "*.vb" });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Parse_EmptyDirectivePayload_IsRejected()
    {
        var result = TriggerDirectiveParser.Parse("// @trigger    \n");
        Assert.Single(result);
        Assert.Equal(TriggerBinding.Manual, result[0]);
    }

    [Fact]
    public void Parse_FilterKeysAreCaseInsensitive_LastValueWins()
    {
        var result = TriggerDirectiveParser.Parse(
            "// @trigger Document.Saved when Filename=*.cs and FILENAME=*.vb\n");
        var binding = Assert.Single(result);
        Assert.Single(binding.Filters);
        Assert.Equal("*.vb", binding.Filters["filename"]);
        Assert.Equal("*.vb", binding.Filters["FILENAME"]);
    }

    [Fact]
    public void Parse_DirectiveAfterBlankLineButBeforeCode_IsAccepted()
    {
        // The leading-block scan tolerates blank gaps; only an actual code line stops it.
        const string src =
            "\n" +
            "// @trigger Build.SolutionBuildDone\n" +
            "\n" +
            "// @trigger BeforeCommand File.Save\n";

        var result = TriggerDirectiveParser.Parse(src);
        Assert.Equal(2, result.Count);
        Assert.Equal("Build.SolutionBuildDone", result[0].Name);
        Assert.Equal("File.Save", result[1].Name);
    }
}
