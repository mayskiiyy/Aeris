using System.Globalization;
using System.IO;
using System.Windows;

namespace Aeris;

public record Language(string Code, string Tag, string Name);

public static class Loc
{
    public const string Fallback = "en";

    public static IReadOnlyList<Language> Languages { get; } =
    [
        new("en", "EN", "English"),
        new("de", "DE", "Deutsch"),
        new("ru", "RU", "Русский"),
        new("uk", "UA", "Українська"),
        new("fr", "FR", "Français"),
        new("es", "ES", "Español"),
        new("pt-BR", "PT", "Português (Brasil)"),
        new("pl", "PL", "Polski"),
    ];

    private static IReadOnlyDictionary<string, string> strings = new Dictionary<string, string>();

    public static Language Current { get; private set; } = Languages[0];
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Fallback);
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeris", "language.txt");

    public static string Preferred()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string saved = File.ReadAllText(FilePath).Trim();
                if (Find(saved) is { } language) return language.Code;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var system = CultureInfo.CurrentUICulture;
        return (Find(system.Name) ?? Find(system.TwoLetterISOLanguageName) ?? Languages[0]).Code;
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Current.Code);
    }

    public static void Apply(ResourceDictionary resources, string code)
    {
        var language = Find(code) ?? Languages[0];
        var fallback = Dictionary(Fallback);
        var selected = language.Code == Fallback ? fallback : Dictionary(language.Code);
        var merged = resources.MergedDictionaries;
        while (merged.Count > 1) merged.RemoveAt(merged.Count - 1);
        merged.Add(fallback);
        if (selected != fallback) merged.Add(selected);

        var table = new Dictionary<string, string>();
        foreach (var source in new[] { fallback, selected })
            foreach (var key in source.Keys.OfType<string>())
                if (source[key] is string text) table[key] = text;
        strings = table;

        Current = language;
        Culture = CultureInfo.GetCultureInfo(language.Code);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Culture;
    }

    public static string Get(string key) => strings.TryGetValue(key, out var text) ? text : key;

    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    public static string Plural(string key, long count)
    {
        string[] forms = Get(key).Split('|');
        return string.Format(Culture, forms[Math.Min(PluralForm(count), forms.Length - 1)], count);
    }

    private static int PluralForm(long count)
    {
        long ones = count % 10, tens = count % 100;
        return Current.Code switch
        {
            "ru" or "uk" => ones == 1 && tens != 11 ? 0 : ones is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2,
            "pl" => count == 1 ? 0 : ones is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2,
            "fr" or "pt-BR" => count is 0 or 1 ? 0 : 1,
            _ => count == 1 ? 0 : 1,
        };
    }

    private static Language? Find(string code) =>
        Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? Languages.FirstOrDefault(l => string.Equals(l.Code.Split('-')[0], code, StringComparison.OrdinalIgnoreCase));

    private static ResourceDictionary Dictionary(string code) =>
        new() { Source = new Uri($"/Aeris;component/Languages/{code}.xaml", UriKind.Relative) };
}
