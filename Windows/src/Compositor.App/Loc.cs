using Microsoft.Windows.ApplicationModel.Resources;

namespace Compositor.App;

/// <summary>
/// Code-behind strings. XAML chrome comes through x:Uid; anything built in code reads its
/// resource here. A missing entry falls back to <paramref name="fallback"/> (or the key), so
/// an incomplete translation degrades to English instead of showing raw keys.
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    public static string T(string key) => T(key, key);

    public static string T(string key, string fallback)
    {
        try
        {
            // GetString throws NamedResource-not-found on a missing key; log it and fall
            // back so one untranslated string can never take the window down.
            return Loader.GetString(key) is { Length: > 0 } value ? value : fallback;
        }
        catch (Exception ex)
        {
            App.Log($"missing resource: {key} ({ex.Message})");
            return fallback;
        }
    }

    /// <summary>A positional-format status or dialog string: resw value holds {0}-style holes.</summary>
    public static string F(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>Display names for manifest enums (blend modes, adjustments, color ranges):
    /// keyed Raw_{kind}_{raw}, falling back to the raw name the resolver produced.</summary>
    public static string Name(string kind, string raw) => T($"Raw_{kind}_{raw}", raw);
}
