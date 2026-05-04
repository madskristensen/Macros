using Macros.Engine.Recording;
using Xunit;

namespace Macros.Tests.Recording;

/// <summary>
/// Tests for the <see cref="TextEditStep"/> record and the <see cref="ITextEditSink"/>
/// bridge interface — the only pieces of the text-edit observer pipeline that have no
/// Visual Studio dependency.
/// </summary>
/// <remarks>
/// We deliberately do NOT attempt to unit-test <c>TextEditObserver</c> /
/// <c>TextEditObserverProvider</c> here. Both depend on <c>IWpfTextView</c> +
/// <c>ITextBuffer.Changed</c>, which require a hosted Visual Studio process to construct.
/// Those code paths can only be smoke-tested manually inside an experimental hive.
/// </remarks>
public sealed class TextEditStepTests
{
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
