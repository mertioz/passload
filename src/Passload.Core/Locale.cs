using System.Globalization;
using System.Text.Json;

namespace Passload.Core;

public sealed class Locale
{
    public CultureInfo Culture { get; }
    public string Name => Culture.Name;
    public string NativeName { get; }

    private readonly Lazy<IReadOnlyDictionary<string, string>> _messages;
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> WebMessages =
        new(() => Read<Dictionary<string, Dictionary<string, string>>>("Passload.locales.json"));

    public static Locale English { get; } = new("en-US", "English");
    public static IReadOnlyList<Locale> Supported { get; } = Array.AsReadOnly<Locale>(
    [
        English, new("fr-FR", "Français"), new("de-DE", "Deutsch"), new("es-ES", "Español"),
        new("ja-JP", "日本語"), new("zh-CN", "简体中文")
    ]);

    public static Locale Current { get; private set; } = Match(CultureInfo.CurrentUICulture.Name) ?? English;

    private Locale(string name, string nativeName)
    {
        Culture = CultureInfo.GetCultureInfo(name);
        NativeName = nativeName;
        _messages = new(() => Read<Dictionary<string, string>>($"Passload.Locale.{name}.json"));
    }

    public static Locale? Match(string? language)
    {
        var prefix = language?.Split('-', '_')[0];
        return Supported.FirstOrDefault(locale =>
            string.Equals(locale.Culture.TwoLetterISOLanguageName, prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static void Use(string? language) =>
        Current = Match(language) ?? Match(CultureInfo.CurrentUICulture.Name) ?? English;

    public IReadOnlyDictionary<string, string> Messages => _messages.Value;

    public static string Text(string key, params object?[] arguments) => Current.Get(key, arguments);

    public string Get(string key, params object?[] arguments) =>
        Format(Messages.TryGetValue(key, out var translation) ? translation : English.Messages[key], arguments);

    public string Web(string key, params object?[] arguments) =>
        Format(WebMessages.Value[Name].TryGetValue(key, out var translation) ? translation : WebMessages.Value[English.Name][key], arguments);

    private string Format(string message, object?[] arguments) =>
        arguments.Length == 0 ? message : string.Format(Culture, message, arguments);

    private static T Read<T>(string resource)
    {
        using var stream = typeof(Locale).Assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<T>(stream)!;
    }
}
