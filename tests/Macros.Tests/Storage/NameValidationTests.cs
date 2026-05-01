using Macros.Engine.Storage;
using Xunit;

namespace Macros.Tests.Storage;

/// <summary>
/// Pins every clause of the macro-name validation contract documented on
/// <see cref="FileSystemMacroStorage.IsValidName(string)"/>. The implementation is the
/// only gate between user-supplied names and on-disk file paths, so each rule gets
/// both a positive and a negative case here.
/// </summary>
public sealed class NameValidationTests
{
    private static readonly FileSystemMacroStorage Storage =
        new("X:\\fake-global");

    [Theory]
    [InlineData("Hello")]
    [InlineData("Format-On-Save")]
    [InlineData("Build_And_Run")]
    [InlineData("With Single Spaces")]
    [InlineData("123")]
    [InlineData("a")]
    public void Valid_AcceptsCommonNames(string name)
    {
        Assert.True(Storage.IsValidName(name), $"Expected '{name}' to be valid.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Invalid_NullOrEmpty(string? name)
    {
        Assert.False(Storage.IsValidName(name!));
    }

    [Fact]
    public void Valid_SixtyChars()
    {
        var name = new string('a', 60);
        Assert.True(Storage.IsValidName(name));
    }

    [Fact]
    public void Invalid_SixtyOneChars()
    {
        var name = new string('a', 61);
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData("Has<bracket")]
    [InlineData("Has>bracket")]
    [InlineData("Has:colon")]
    [InlineData("Has\"quote")]
    [InlineData("Has/slash")]
    [InlineData("Has\\backslash")]
    [InlineData("Has|pipe")]
    [InlineData("Has?question")]
    [InlineData("Has*star")]
    public void Invalid_PathIllegalChars(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData("Tab\there")]
    [InlineData("Newline\nhere")]
    [InlineData("Bell\u0007here")]
    public void Invalid_AsciiControlChars(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData(" Leading")]
    [InlineData("Trailing ")]
    [InlineData("Two  Spaces")]
    public void Invalid_LeadingTrailingOrConsecutiveSpaces(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM0")]
    [InlineData("COM5")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void Invalid_ReservedWindowsNames(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("Current")]
    [InlineData("CURRENT")]
    public void Invalid_ReservedCurrent_CaseInsensitive(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData(".hidden")]
    [InlineData(".vs")]
    public void Invalid_LeadingDot(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Theory]
    [InlineData("Foo.bar")]   // punctuation other than - and _
    [InlineData("Foo+bar")]
    [InlineData("Foo!bar")]
    [InlineData("Foo,bar")]
    [InlineData("Foo;bar")]
    [InlineData("Foo'bar")]
    [InlineData("Foo(bar")]
    public void Invalid_DisallowedPunctuation(string name)
    {
        Assert.False(Storage.IsValidName(name));
    }

    [Fact]
    public void Valid_HyphensUnderscoresDigitsLetters_AllAccepted()
    {
        Assert.True(Storage.IsValidName("a-b_c-1_2"));
    }
}
