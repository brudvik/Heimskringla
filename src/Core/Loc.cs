using Godot;

namespace Heimskringla.Core;

/// <summary>
/// Localization of player-facing text. Strings live as keys in translations/*.csv (columns en, nb)
/// and are looked up through Godot's TranslationServer. English is the fallback language.
/// </summary>
public static class Loc
{
    /// <summary>Supported languages: setting value, and the name shown in the language picker (in its own language).</summary>
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("nb", "Norsk") };

    /// <summary>Translated text for a key. Unknown keys are returned unchanged.</summary>
    public static string T(string key) => TranslationServer.Translate(key);

    /// <summary>Translated text with string.Format arguments, e.g. Loc.F("TOAST_SAVED", seed).</summary>
    public static string F(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>The active language code ("en" or "nb").</summary>
    public static string Current => TranslationServer.GetLocale().StartsWith("nb") ? "nb" : "en";

    /// <summary>
    /// Sets the language from Settings.Language: "" follows the system language, otherwise "en"/"nb".
    /// Norwegian system locales (nb, no, nn) map to Bokmål; everything else falls back to English.
    /// </summary>
    public static void Apply(string setting)
    {
        string lang = OS.GetEnvironment("HK_LANG");   // test switch: HK_LANG=en|nb overrides the setting
        if (string.IsNullOrEmpty(lang)) lang = setting;
        if (string.IsNullOrEmpty(lang))
        {
            string os = OS.GetLocaleLanguage();
            lang = os is "nb" or "no" or "nn" ? "nb" : "en";
        }
        TranslationServer.SetLocale(lang);
    }
}
