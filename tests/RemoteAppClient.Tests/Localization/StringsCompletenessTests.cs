using System.Reflection;
using System.Text.RegularExpressions;

namespace RemoteAppClient.Tests.Localization;

/// <summary>
/// Every string exists in both languages with the same placeholders, and every accessor property has an
/// entry. A missing Hungarian entry silently falls back to English at runtime, and a placeholder that only
/// one language has throws at the moment the message is needed - neither is something a build catches.
/// </summary>
public class StringsCompletenessTests
{
    public static IEnumerable<object[]> Tables()
    {
        // The server's table is internal; reach it through its assembly rather than opening it up for the test.
        yield return new object[] { typeof(RemoteServer.Services.ClockSkewTracker).Assembly.GetType("RemoteServer.Localization.Strings", throwOnError: true)! };
        yield return new object[] { typeof(RemoteClient.Localization.Strings) };
    }

    private static Dictionary<string, Dictionary<string, string>> TranslationsOf(Type strings)
    {
        var prop = strings.GetProperty("Translations", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException(strings.FullName + " has no Translations table");
        return (Dictionary<string, Dictionary<string, string>>)prop.GetValue(null)!;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Both_languages_have_the_same_keys(Type strings)
    {
        var t = TranslationsOf(strings);
        Assert.Contains("en", t.Keys);
        Assert.Contains("hu", t.Keys);
        var onlyEn = t["en"].Keys.Except(t["hu"].Keys).Order().ToList();
        var onlyHu = t["hu"].Keys.Except(t["en"].Keys).Order().ToList();
        Assert.True(onlyEn.Count == 0 && onlyHu.Count == 0,
            $"{strings.Name}: only in en: [{string.Join(", ", onlyEn)}]; only in hu: [{string.Join(", ", onlyHu)}]");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Placeholders_match_between_the_languages(Type strings)
    {
        var t = TranslationsOf(strings);
        var mismatches = new List<string>();
        foreach (var (key, en) in t["en"])
        {
            if (!t["hu"].TryGetValue(key, out var hu)) continue;
            var pe = Placeholders(en); var ph = Placeholders(hu);
            if (!pe.SetEquals(ph)) mismatches.Add($"{key}: en {{{string.Join(",", pe)}}} vs hu {{{string.Join(",", ph)}}}");
        }
        Assert.True(mismatches.Count == 0, strings.Name + ":\n" + string.Join("\n", mismatches));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_accessor_has_an_entry(Type strings)
    {
        var t = TranslationsOf(strings);
        var missing = strings.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string) && p.Name != "Language")
            .Select(p => p.Name)
            .Where(name => !t["en"].ContainsKey(name) || !t["hu"].ContainsKey(name))
            .Order().ToList();
        Assert.True(missing.Count == 0, $"{strings.Name}: no translation for [{string.Join(", ", missing)}]");
    }

    private static HashSet<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{[^{}]+\}").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
}
