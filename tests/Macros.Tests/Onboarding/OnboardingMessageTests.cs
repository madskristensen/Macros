using Macros.Onboarding;
using Xunit;

namespace Macros.Tests.Onboarding;

/// <summary>
/// Pure-formatter tests for the onboarding InfoBar message. The bar's display logic touches
/// VS InfoBar APIs and can only be smoke-tested manually; here we just lock the wording.
/// </summary>
public sealed class OnboardingMessageTests
{
    [Fact]
    public void NonZeroCount_MentionsSampleCountAndHotkeys()
    {
        string message = OnboardingInfoBar.BuildOnboardingMessage(15);

        Assert.Contains("15", message);
        Assert.Contains("Ctrl+Shift+R", message);
        Assert.Contains("Ctrl+Shift+P", message);
    }

    [Fact]
    public void ZeroCount_OmitsSampleHintButKeepsHotkeys()
    {
        string message = OnboardingInfoBar.BuildOnboardingMessage(0);

        Assert.DoesNotContain("samples", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ctrl+Shift+R", message);
        Assert.Contains("Ctrl+Shift+P", message);
    }
}
