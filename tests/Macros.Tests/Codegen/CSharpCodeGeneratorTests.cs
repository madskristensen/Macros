using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Macros.Engine.Codegen;
using Macros.Engine.Recording;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Macros.Tests.Codegen;

/// <summary>
/// Tests for <see cref="CSharpCodeGenerator"/>. The generator is a pure transform —
/// no Visual Studio host required, so every code path is exercised here directly.
/// </summary>
/// <remarks>
/// Snapshots use a fixed UTC timestamp so the golden file in <c>Codegen/golden/</c>
/// is stable across runs and machines. Line endings in both the generated string
/// and the golden file are normalised to <c>\n</c> before comparison so a developer
/// who has <c>core.autocrlf=true</c> doesn't break the snapshot.
/// </remarks>
public sealed class CSharpCodeGeneratorTests
{
    private static readonly DateTime FixedUtc = new(2025, 4, 30, 21, 48, 37, DateTimeKind.Utc);

    [Fact]
    public void Generate_EmptySteps_ProducesHeaderAndUsingsOnly()
    {
        string src = CSharpCodeGenerator.Generate(Array.Empty<RecordedStep>(), "Empty", FixedUtc);

        Assert.Contains("// Macro: Empty", src);
        Assert.Contains("// Steps: 0", src);
        Assert.Contains("using static Macros.Engine.Scripting.Helpers;", src);

        // No body emitted: no `await ` calls.
        Assert.DoesNotContain("await ", src);

        // Still parseable C# script (header / usings / #r are all valid script syntax).
        AssertNoSyntaxErrors(src);
    }

    [Fact]
    public void Generate_SingleInsertion_EmitsTypeAsyncCall()
    {
        var step = new TextEditStep(OldPosition: 0, OldLength: 0, OldText: "", NewText: "hello");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { step }, "M", FixedUtc);

        Assert.Contains("await TypeAsync(\"hello\");", src);
        Assert.Contains("// step 1: insert 5 char(s) @ pos 0", src);
        AssertNoSyntaxErrors(src);
    }

    [Fact]
    public void Generate_MultiLineInsertion_UsesVerbatimAndDoublesQuotes()
    {
        var step = new TextEditStep(0, 0, "", "line1\nline2 \"quoted\"");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { step }, "M", FixedUtc);

        // Verbatim form starts with @" and embeds raw newlines.
        Assert.Contains("await TypeAsync(@\"line1\nline2 \"\"quoted\"\"\");", Normalize(src));
    }

    [Fact]
    public void Generate_BackslashInSingleLineText_EscapesProperly()
    {
        var step = new TextEditStep(0, 0, "", @"C:\path\file.cs");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { step }, "M", FixedUtc);

        // Regular string form: each '\' becomes '\\'.
        Assert.Contains(@"await TypeAsync(""C:\\path\\file.cs"");", src);
    }

    [Fact]
    public void Generate_CommandStepWithDteName_EmitsExecuteCommandAsync()
    {
        var cmd = new RecordedStep.CommandStep(Guid.NewGuid(), 7u, "Edit.Copy");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { cmd }, "M", FixedUtc);

        Assert.Contains("await ExecuteCommandAsync(\"Edit.Copy\");", src);
        Assert.Contains("// step 1: command Edit.Copy", src);
        Assert.DoesNotContain("RunCommandAsync", src);
        AssertNoSyntaxErrors(src);
    }

    [Fact]
    public void Generate_CommandStepWithoutName_EmitsRunCommandAsync()
    {
        var group = new Guid("11111111-2222-3333-4444-555555555555");
        var cmd = new RecordedStep.CommandStep(group, 42u, Name: null);

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { cmd }, "M", FixedUtc);

        Assert.Contains(
            "await RunCommandAsync(new System.Guid(\"11111111-2222-3333-4444-555555555555\"), 42u);",
            src);
        Assert.DoesNotContain("ExecuteCommandAsync", src);
        AssertNoSyntaxErrors(src);
    }

    [Fact]
    public void Generate_CommandStepWithMalformedName_FallsBackToRunCommandAsync()
    {
        // Localised / non-DTE-shaped names must NOT be passed to DTE.ExecuteCommand —
        // the generator falls back to GUID/ID dispatch and surfaces the raw name in
        // the comment for diagnostic value.
        var group = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var cmd = new RecordedStep.CommandStep(group, 9u, "Some Localised Label");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { cmd }, "M", FixedUtc);

        Assert.Contains("await RunCommandAsync(new System.Guid(\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"), 9u);", src);
        Assert.Contains("(raw name: Some Localised Label)", src);
    }

    [Fact]
    public void Generate_PureDeletion_EmitsDeletionTodoComment()
    {
        var step = new TextEditStep(OldPosition: 5, OldLength: 3, OldText: "abc", NewText: "");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { step }, "M", FixedUtc);

        Assert.Contains("// step 1: delete 3 char(s) @ pos 5", src);
        Assert.Contains("// TODO(m4-deletion-replay)", src);
        Assert.Contains("await ExecuteCommandAsync(\"Edit.Delete\");", src);
        Assert.DoesNotContain("await TypeAsync(", src);
    }

    [Fact]
    public void Generate_Replacement_EmitsDeletionTodoAndInsertion()
    {
        var step = new TextEditStep(OldPosition: 5, OldLength: 3, OldText: "abc", NewText: "XYZ");

        string src = CSharpCodeGenerator.Generate(new RecordedStep[] { step }, "M", FixedUtc);

        Assert.Contains("// step 1: replace 3 char(s) with 3 char(s) @ pos 5", src);
        Assert.Contains("// TODO(m4-deletion-replay)", src);
        Assert.Contains("await ExecuteCommandAsync(\"Edit.Delete\");", src);
        Assert.Contains("await TypeAsync(\"XYZ\");", src);
    }

    [Fact]
    public void Generate_HeaderIncludesUtcTimestampInIso8601()
    {
        string src = CSharpCodeGenerator.Generate(Array.Empty<RecordedStep>(), "M", FixedUtc);

        Assert.Contains("// Recorded: 2025-04-30T21:48:37Z", src);
    }

    [Fact]
    public void Generate_HeaderConvertsLocalTimeToUtc()
    {
        // Caller may pass DateTime.Now (Kind=Local); generator must normalise to UTC.
        var local = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();
        string src = CSharpCodeGenerator.Generate(Array.Empty<RecordedStep>(), "M", local);

        Assert.Contains("// Recorded: 2025-01-01T12:00:00Z", src);
    }

    [Fact]
    public void Generate_HeaderIncludesStepCount()
    {
        var steps = new RecordedStep[]
        {
            new RecordedStep.CommandStep(Guid.Empty, 1, "Edit.Copy"),
            new RecordedStep.CommandStep(Guid.Empty, 2, "Edit.Paste"),
            new RecordedStep.CommandStep(Guid.Empty, 3, "Edit.Cut"),
        };

        string src = CSharpCodeGenerator.Generate(steps, "M", FixedUtc);

        Assert.Contains("// Steps: 3", src);
    }

    [Fact]
    public void Generate_StepCommentsAreOneBased()
    {
        var steps = new RecordedStep[]
        {
            new TextEditStep(0, 0, "", "a"),
            new TextEditStep(1, 0, "", "b"),
        };

        string src = CSharpCodeGenerator.Generate(steps, "M", FixedUtc);

        Assert.Contains("// step 1:", src);
        Assert.Contains("// step 2:", src);
        Assert.DoesNotContain("// step 0:", src);
    }

    [Fact]
    public void Generate_OutputIsParseableAsCSharpScript()
    {
        // Mixed step types stress-test the syntactic correctness of the emitted source.
        var steps = new RecordedStep[]
        {
            new TextEditStep(0, 0, "", "hello\nworld"),
            new RecordedStep.CommandStep(Guid.NewGuid(), 5u, "Edit.Copy"),
            new RecordedStep.CommandStep(Guid.NewGuid(), 6u, null),
            new TextEditStep(11, 0, "", @"path\to\file"),
            new TextEditStep(20, 5, "hello", "GOODBYE"),
        };

        string src = CSharpCodeGenerator.Generate(steps, "Mixed", FixedUtc);

        AssertNoSyntaxErrors(src);
    }

    [Fact]
    public void Generate_SnapshotMatchesGoldenFile()
    {
        var group = new Guid("11111111-2222-3333-4444-555555555555");
        var steps = new RecordedStep[]
        {
            new TextEditStep(OldPosition: 0, OldLength: 0, OldText: "", NewText: "Hello"),
            new RecordedStep.CommandStep(Guid.NewGuid(), 7u, "Edit.Copy"),
            new TextEditStep(OldPosition: 10, OldLength: 0, OldText: "", NewText: "Line1\nLine2 \"quoted\""),
            new RecordedStep.CommandStep(group, 42u, Name: null),
            new TextEditStep(OldPosition: 20, OldLength: 3, OldText: "abc", NewText: ""),
        };

        string actual = CSharpCodeGenerator.Generate(steps, "SampleMacro", FixedUtc);
        string expected = ReadGoldenFile("sample.csx");

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public void Generate_Header_IncludesTriggerGuidance()
    {
        // Verify that the trigger guidance section is included in the header
        string src = CSharpCodeGenerator.Generate(Array.Empty<RecordedStep>(), "TestMacro", FixedUtc);

        // Check for the guidance comment block
        Assert.Contains("To make this macro run automatically", src);
        Assert.Contains("remove the leading \"// EXAMPLE: \" prefix", src);
        
        // Check for all four example trigger lines
        Assert.Contains("// EXAMPLE: // @trigger Build.SolutionBuildDone", src);
        Assert.Contains("// EXAMPLE: // @trigger Document.Saved when filename=*.cs", src);
        Assert.Contains("// EXAMPLE: // @trigger BeforeCommand File.Save", src);
        Assert.Contains("// EXAMPLE: // @trigger AfterCommand Build.BuildSolution", src);
        
        // Check for the reference to Tools menu
        Assert.Contains("See Tools → Options → Macros → Available Triggers", src);
    }

    [Fact]
    public void QuoteRegular_EscapesControlCharsToUnicodeLiterals()
    {
        // Bell (\a) has a named escape; an arbitrary low-control char (0x01) must
        // fall through to \uXXXX form.
        string quoted = CSharpCodeGenerator.QuoteRegular("\u0001");

        Assert.Equal("\"\\u0001\"", quoted);
    }

    [Fact]
    public void QuoteVerbatim_DoublesQuotesOnly()
    {
        string quoted = CSharpCodeGenerator.QuoteVerbatim("a\"b\\c");

        // Backslashes are literal in verbatim strings; only " is escaped (doubled).
        Assert.Equal("@\"a\"\"b\\c\"", quoted);
    }

    private static string ReadGoldenFile(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Codegen", "golden", name);
        Assert.True(File.Exists(path), $"Golden file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static void AssertNoSyntaxErrors(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source),
            new CSharpParseOptions(kind: SourceCodeKind.Script, languageVersion: LanguageVersion.Latest));

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(
            errors.Length == 0,
            "Generated source has syntax errors:\n" + string.Join("\n", errors.Select(e => e.ToString())) +
            "\n\nSource:\n" + source);
    }
}
