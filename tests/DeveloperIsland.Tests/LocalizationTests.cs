using System.Globalization;
using DeveloperIsland.Core.Localization;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Tests;

/// <summary>
/// The point of these tests is coverage, not wording: a string with no translation is the failure
/// that actually reaches a user, as an English sentence in the middle of a Chinese panel.
/// </summary>
public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Loc.Use(UiLanguage.English);

    public void Dispose() => Loc.ResetForTests();

    [Fact]
    public void Every_text_entry_has_both_an_English_and_a_Chinese_value()
    {
        // Both tables throw on a missing entry, so enumerating the enum exercises every case.
        var english = new EnglishText();
        var chinese = new ChineseText();
        var missing = new List<string>();

        foreach (var text in Enum.GetValues<Text>())
        {
            try
            {
                Assert.False(string.IsNullOrWhiteSpace(english[text]));
            }
            catch (ArgumentOutOfRangeException)
            {
                missing.Add($"English:{text}");
            }

            try
            {
                Assert.False(string.IsNullOrWhiteSpace(chinese[text]));
            }
            catch (ArgumentOutOfRangeException)
            {
                missing.Add($"Chinese:{text}");
            }
        }

        Assert.True(missing.Count == 0, "missing translations: " + string.Join(", ", missing));
    }

    [Fact]
    public void Switching_language_changes_what_is_shown()
    {
        Loc.Use(UiLanguage.English);
        Assert.Equal("Now", Loc.T(Text.GroupNow));

        Loc.Use(UiLanguage.ChineseSimplified);
        Assert.Equal("现在", Loc.T(Text.GroupNow));
    }

    [Fact]
    public void Switching_language_raises_the_change_event_so_open_windows_can_repaint()
    {
        Loc.Use(UiLanguage.English);
        var raised = 0;
        void Handler() => raised++;

        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Use(UiLanguage.ChineseSimplified);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Asking_for_the_same_language_again_does_not_raise_the_event()
    {
        Loc.Use(UiLanguage.ChineseSimplified);
        var raised = 0;
        void Handler() => raised++;

        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Use(UiLanguage.ChineseSimplified);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
        }

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Follow_the_system_resolves_to_a_real_language_never_to_System()
    {
        Assert.NotEqual(UiLanguage.System, Loc.Resolve(UiLanguage.System));
    }

    [Fact]
    public void A_machine_in_neither_language_gets_English_rather_than_Chinese()
    {
        // The fallback matters: a wrong guess shows Chinese to a user who cannot read it.
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal(UiLanguage.English, Loc.Resolve(UiLanguage.System));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void A_Chinese_windows_resolves_to_Chinese()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal(UiLanguage.ChineseSimplified, Loc.Resolve(UiLanguage.System));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Placeholders_are_filled_and_may_be_reordered_by_a_translation()
    {
        Loc.Use(UiLanguage.ChineseSimplified);

        // The Chinese wording puts the tool name first, which is why this goes through string.Format
        // rather than concatenation.
        var text = Loc.T(Text.ActivityActivityToolFailed, "Bash");

        Assert.Contains("Bash", text);
        Assert.DoesNotContain("{0}", text);
    }

    [Fact]
    public void Every_module_has_a_name_in_both_languages()
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.ChineseSimplified })
        {
            Loc.Use(language);
            foreach (var module in Enum.GetValues<ModuleId>())
            {
                Assert.False(string.IsNullOrWhiteSpace(Loc.Module(module)), $"{module} has no name in {language}");
            }
        }
    }

    [Fact]
    public void Every_navigation_group_has_a_name_in_both_languages()
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.ChineseSimplified })
        {
            Loc.Use(language);
            foreach (var group in Enum.GetValues<NavigationGroup>())
            {
                Assert.False(string.IsNullOrWhiteSpace(Loc.Group(group)), $"{group} has no name in {language}");
            }
        }
    }

    [Fact]
    public void The_chinese_names_use_the_vocabulary_the_user_chose()
    {
        Loc.Use(UiLanguage.ChineseSimplified);

        // The user picked these terms explicitly: 灵动岛 for the capsule, 现在/工作/生活 for the groups.
        Assert.Equal("现在", Loc.Group(NavigationGroup.Now));
        Assert.Equal("工作", Loc.Group(NavigationGroup.Work));
        Assert.Equal("生活", Loc.Group(NavigationGroup.Life));

        // Product names stay as they are; translating them would make them harder to recognize.
        Assert.Equal("Claude Code", Loc.Module(ModuleId.Claude));
        Assert.Equal("GitHub", Loc.Module(ModuleId.GitHub));
    }

    [Fact]
    public void The_language_setting_defaults_to_following_windows()
    {
        // A fresh settings object must not force English onto a Chinese machine.
        var settings = new Core.Settings.AppSettings();

        Assert.Equal(UiLanguage.System, settings.Language);
    }
}
