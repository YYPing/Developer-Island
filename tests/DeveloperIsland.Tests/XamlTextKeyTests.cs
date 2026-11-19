using System.Text.RegularExpressions;
using DeveloperIsland.Core.Localization;

namespace DeveloperIsland.Tests;

/// <summary>
/// XAML refers to a translation by name in a string, so a typo is not a compile error: the attached
/// property falls back to the enum's default value and the element quietly shows "Developer Island"
/// instead of its label. These tests read the real XAML files and check every key against the enum.
/// </summary>
public class XamlTextKeyTests
{
    /// <summary>The XAML sources, found from the test's own output directory upward.</summary>
    private static string UiDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeveloperIsland.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "DeveloperIsland", "UI");
        }
    }

    /// <summary>Collects every key name referenced from XAML with a simple, tolerant scan.</summary>
    private static List<(string File, string Key)> ReferencedKeys()
    {
        var found = new List<(string, string)>();
        if (!Directory.Exists(UiDirectory))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(UiDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "ui:LocText\\.TextKey\\s*=\\s*\"([A-Za-z0-9_]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                found.Add((Path.GetFileName(file), match.Groups[1].Value));
            }
        }

        return found;
    }

    [Fact]
    public void The_scan_finds_the_keys_that_were_converted()
    {
        // Guards the test itself: a broken pattern would make every assertion below vacuous.
        var keys = ReferencedKeys();

        Assert.True(keys.Count > 50, $"expected many localized XAML keys, found {keys.Count}");
    }

    [Fact]
    public void Every_key_used_in_xaml_exists_in_the_text_enum()
    {
        var unknown = ReferencedKeys()
            .Where(k => !Enum.TryParse<Text>(k.Key, ignoreCase: false, out _) || !Enum.IsDefined(typeof(Text), k.Key))
            .Select(k => $"{k.File}: {k.Key}")
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0, "XAML refers to keys that do not exist: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_key_used_in_xaml_resolves_in_both_languages()
    {
        var failures = new List<string>();
        foreach (var language in new[] { UiLanguage.English, UiLanguage.ChineseSimplified })
        {
            Loc.Use(language);
            foreach (var (file, key) in ReferencedKeys())
            {
                if (!Enum.TryParse<Text>(key, out var text))
                {
                    continue;
                }

                try
                {
                    if (string.IsNullOrWhiteSpace(Loc.T(text)))
                    {
                        failures.Add($"{file}: {key} is empty in {language}");
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    failures.Add($"{file}: {key} has no {language} text");
                }
            }
        }

        Loc.ResetForTests();
        Assert.True(failures.Count == 0, string.Join(", ", failures.Distinct()));
    }
}
