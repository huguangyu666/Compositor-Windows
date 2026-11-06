namespace Compositor.App;

internal static class UiLanguage
{
    internal static string Resolve(string? language) => language is not null
        && (language.Equals("zh", StringComparison.OrdinalIgnoreCase) || language.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
        ? "zh-CN" : "en-US";
}
