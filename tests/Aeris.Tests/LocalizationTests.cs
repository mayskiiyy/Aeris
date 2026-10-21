namespace Aeris.Tests;

public class LocalizationTests
{
    [Fact]
    public void EveryLanguageHasTheSameKeysAsEnglish()
    {
        var english = Loc.Dictionary(Loc.Fallback).Keys.ToHashSet();
        foreach (var language in Loc.Languages)
        {
            var keys = Loc.Dictionary(language.Code).Keys.ToHashSet();
            Assert.Empty(english.Except(keys).Select(k => $"{language.Code}: missing {k}"));
            Assert.Empty(keys.Except(english).Select(k => $"{language.Code}: extra {k}"));
        }
    }

    [Fact]
    public void FormatPlaceholdersMatchEnglish()
    {
        var english = Loc.Dictionary(Loc.Fallback);
        static string Shape(string text) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        foreach (var language in Loc.Languages)
            foreach (var (key, text) in Loc.Dictionary(language.Code))
                if (!key.StartsWith("Option.Days", StringComparison.Ordinal))
                    Assert.True(Shape(english[key]) == Shape(text), $"{language.Code}: {key} placeholders differ");
    }

    [Theory]
    [InlineData("ru", 1, 0)]
    [InlineData("ru", 3, 1)]
    [InlineData("ru", 5, 2)]
    [InlineData("ru", 11, 2)]
    [InlineData("ru", 21, 0)]
    [InlineData("pl", 22, 1)]
    [InlineData("pl", 12, 2)]
    [InlineData("fr", 0, 0)]
    [InlineData("en", 0, 1)]
    public void PluralForms(string code, long count, int form)
    {
        Loc.Load(code);
        try { Assert.Equal(form, Loc.PluralForm(count)); }
        finally { Loc.Load(Loc.Fallback); }
    }
}

public class StringUsageTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void EveryKeyUsedInTheSourceExists()
    {
        var english = Loc.Dictionary(Loc.Fallback);
        var missing = new List<string>();
        int found = 0;
        string src = System.IO.Path.Combine(Repo(), "src");
        foreach (string file in System.IO.Directory.EnumerateFiles(src, "*.*", System.IO.SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains(@"\Languages\")) continue;
            string text = System.IO.File.ReadAllText(file);
            var pattern = file.EndsWith(".xaml") ? @"DynamicResource ([A-Za-z]+\.[A-Za-z.]+)" : file.EndsWith(".cs") ? @"Loc\.(?:Get|Format|Plural)\(""([^""{}]+)""" : null;
            if (pattern == null) continue;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, pattern))
                if (++found > 0 && !english.ContainsKey(match.Groups[1].Value)) missing.Add($"{System.IO.Path.GetFileName(file)}: {match.Groups[1].Value}");
        }
        Assert.Empty(missing);
        Assert.True(found > 300, $"only {found} keys found; is the source path right?");
    }
}
