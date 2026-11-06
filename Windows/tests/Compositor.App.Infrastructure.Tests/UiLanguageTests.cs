using Compositor.App;
using Xunit;

namespace Compositor.App.Infrastructure.Tests;

public sealed class UiLanguageTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hans-SG", "zh-CN")]
    [InlineData("zh-TW", "zh-CN")]
    [InlineData("zh-HK", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("ZH-cn", "zh-CN")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("ja-JP", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void UsesAnAvailableTranslationForTheWindowsUiLanguage(string? language, string expected)
        => Assert.Equal(expected, UiLanguage.Resolve(language));
}
