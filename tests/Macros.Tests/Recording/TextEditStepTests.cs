using System.Reflection;
using Macros.Engine.Recording;
using Xunit;

namespace Macros.Tests.Recording;

/// <summary>
/// Shape and equality tests for the <see cref="TextEditStep"/> record and the
/// <see cref="ITextEditSink"/> bridge interface. These are the only types in the
/// text-edit observer pipeline that have no Visual Studio dependency, so they're the only
/// pieces we can meaningfully cover from a plain xUnit project.
/// </summary>
/// <remarks>
/// We deliberately do NOT attempt to unit-test <c>TextEditObserver</c> /
/// <c>TextEditObserverProvider</c> here. Both depend on <c>IWpfTextView</c> +
/// <c>ITextBuffer.Changed</c>, which require a hosted Visual Studio process to construct.
/// Those code paths are exercised end-to-end by <c>Macros.IntegrationTests</c>.
/// </remarks>
public sealed class TextEditStepTests
{
    [Fact]
    public void TextEditStep_IsPublicSealedRecord()
    {
        var t = typeof(TextEditStep);

        Assert.True(t.IsPublic, "TextEditStep must be public so the VSIX assembly can construct it.");
        Assert.True(t.IsSealed, "TextEditStep must be sealed; subclassing would defeat the discriminated-union model used by the code generator.");
        Assert.True(t.IsClass, "TextEditStep must be a record class (reference type), not a record struct.");

        // Records compile to a class with a synthesised <Clone>$ method.
        Assert.NotNull(t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TextEditStep_ExposesPositionalProperties()
    {
        var step = new TextEditStep(OldPosition: 5, OldLength: 2, OldText: "ab", NewText: "X");

        Assert.Equal(5, step.OldPosition);
        Assert.Equal(2, step.OldLength);
        Assert.Equal("ab", step.OldText);
        Assert.Equal("X", step.NewText);
    }

    [Fact]
    public void TextEditStep_TwoEqualValuedInstances_AreEqual()
    {
        var a = new TextEditStep(10, 0, "", "hello");
        var b = new TextEditStep(10, 0, "", "hello");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TextEditStep_DifferingProperty_MakesInstancesUnequal()
    {
        var baseStep = new TextEditStep(10, 0, "", "hello");

        Assert.NotEqual(baseStep, new TextEditStep(11, 0, "", "hello"));
        Assert.NotEqual(baseStep, new TextEditStep(10, 1, "", "hello"));
        Assert.NotEqual(baseStep, new TextEditStep(10, 0, "x", "hello"));
        Assert.NotEqual(baseStep, new TextEditStep(10, 0, "", "world"));
    }

    [Fact]
    public void ITextEditSink_IsPublicInterface()
    {
        var t = typeof(ITextEditSink);

        Assert.True(t.IsInterface, "ITextEditSink must be an interface.");
        Assert.True(t.IsPublic, "ITextEditSink must be public so observers across assemblies can call it.");
    }

    [Fact]
    public void ITextEditSink_DeclaresExpectedMembers()
    {
        var t = typeof(ITextEditSink);

        var isCapturing = t.GetProperty(nameof(ITextEditSink.IsCapturing));
        Assert.NotNull(isCapturing);
        Assert.Equal(typeof(bool), isCapturing!.PropertyType);
        Assert.True(isCapturing.CanRead);

        var onTextEdit = t.GetMethod(nameof(ITextEditSink.OnTextEdit), new[] { typeof(TextEditStep) });
        Assert.NotNull(onTextEdit);
        Assert.Equal(typeof(void), onTextEdit!.ReturnType);
    }

    [Fact]
    public void ITextEditSink_CanBeImplementedAndCalled()
    {
        var sink = new RecordingFake();

        Assert.False(sink.IsCapturing);

        sink.IsCapturingFlag = true;
        var step = new TextEditStep(0, 0, "", "x");
        ((ITextEditSink)sink).OnTextEdit(step);

        Assert.True(sink.IsCapturing);
        Assert.Single(sink.Captured);
        Assert.Same(step, sink.Captured[0]);
    }

    private sealed class RecordingFake : ITextEditSink
    {
        public bool IsCapturingFlag;
        public bool IsCapturing => IsCapturingFlag;
        public System.Collections.Generic.List<TextEditStep> Captured { get; } = new();
        public void OnTextEdit(TextEditStep step) => Captured.Add(step);
    }
}
