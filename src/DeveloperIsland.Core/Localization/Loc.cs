using System.Globalization;

namespace DeveloperIsland.Core.Localization;

/// <summary>
/// The one place a string is resolved. Everything that shows text asks here rather than carrying an
/// English sentence, which is why changing the language needs no restart and no view rebuild.
/// <para>
/// A static entry point rather than an injected service on purpose: text is needed from view models,
/// XAML converters, tray menus and background services alike, and threading a dependency through all
/// of them would buy nothing. The cost is that tests must reset it, which <see cref="Use"/> allows.
/// </para>
/// </summary>
public static class Loc
{
    private static readonly EnglishText EnglishTable = new();
    private static readonly ChineseText ChineseTable = new();

    private static ITextTable _current = EnglishTable;

    /// <summary>Raised after the language changed, so open windows can re-read their text.</summary>
    public static event Action? LanguageChanged;

    public static UiLanguage Language { get; private set; } = UiLanguage.English;

    /// <summary>The table in use. Exposed so a converter can resolve keys without a static call per item.</summary>
    public static ITextTable Table => _current;

    /// <summary>
    /// Switches the language. <see cref="UiLanguage.System"/> resolves against Windows' UI language
    /// and falls back to English: a machine set to Japanese gets English, not Chinese.
    /// </summary>
    public static void Use(UiLanguage language)
    {
        var resolved = Resolve(language);
        if (resolved == Language && _current is not null)
        {
            return;
        }

        Language = resolved;
        _current = resolved == UiLanguage.ChineseSimplified ? ChineseTable : EnglishTable;
        LanguageChanged?.Invoke();
    }

    /// <summary>Maps a saved setting to the language that will actually be shown.</summary>
    public static UiLanguage Resolve(UiLanguage language) => language switch
    {
        UiLanguage.System => SystemPrefers() ? UiLanguage.ChineseSimplified : UiLanguage.English,
        _ => language,
    };

    /// <summary>
    /// Whether Windows itself is in Simplified Chinese. Only the primary UI language counts: a
    /// Chinese keyboard layout on an English system must not turn the island Chinese.
    /// </summary>
    private static bool SystemPrefers()
    {
        try
        {
            var tag = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return string.Equals(tag, "zh", StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>One string.</summary>
    public static string T(Text text) => _current[text];

    /// <summary>
    /// One string with its placeholders filled. Placeholders are <c>{0}</c>, <c>{1}</c> as usual, so
    /// a translation may reorder them, which Chinese often needs.
    /// </summary>
    public static string T(Text text, params object?[] args)
    {
        var format = _current[text];
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    /// <summary>
    /// The display name of a module. Kept here so the catalog stays free of presentation concerns and
    /// every caller gets the same translation.
    /// </summary>
    public static string Module(Modules.ModuleId module) => module switch
    {
        Modules.ModuleId.Claude => T(Text.ModuleClaude),
        Modules.ModuleId.Codex => T(Text.ModuleCodex),
        Modules.ModuleId.Music => T(Text.ModuleMusic),
        Modules.ModuleId.Git => T(Text.ModuleGit),
        Modules.ModuleId.GitHub => T(Text.ModuleGitHub),
        Modules.ModuleId.Focus => T(Text.ModuleFocus),
        Modules.ModuleId.Calendar => T(Text.ModuleCalendar),
        Modules.ModuleId.Tasks => T(Text.ModuleTasks),
        Modules.ModuleId.System => T(Text.ModuleSystem),
        Modules.ModuleId.Awake => T(Text.ModuleAwake),
        Modules.ModuleId.Feed => T(Text.ModuleFeed),
        Modules.ModuleId.AiChat => T(Text.ModuleAiChat),
        Modules.ModuleId.Agents => T(Text.ModuleAgents),
        Modules.ModuleId.Inbox => T(Text.ModuleInbox),
        Modules.ModuleId.Pipeline => T(Text.ModulePipeline),
        Modules.ModuleId.Weather => T(Text.ModuleWeather),
        Modules.ModuleId.Markets => T(Text.ModuleMarkets),
        _ => module.ToString(),
    };

    /// <summary>The display name of a navigation group.</summary>
    public static string Group(Modules.NavigationGroup group) => group switch
    {
        Modules.NavigationGroup.Now => T(Text.GroupNow),
        Modules.NavigationGroup.AI => T(Text.GroupAi),
        Modules.NavigationGroup.Work => T(Text.GroupWork),
        Modules.NavigationGroup.Life => T(Text.GroupLife),
        _ => group.ToString(),
    };

    /// <summary>Restores English. For tests, which must never leak a language into the next one.</summary>
    internal static void ResetForTests()
    {
        Language = UiLanguage.English;
        _current = EnglishTable;
    }
}
