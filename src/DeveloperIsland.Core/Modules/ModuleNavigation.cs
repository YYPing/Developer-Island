namespace DeveloperIsland.Core.Modules;

public enum NavigationGroup { Now, AI, Work, Life }

public static class ModuleNavigation
{
    public static NavigationGroup GroupOf(ModuleId module) => module switch
    {
        ModuleId.Claude or ModuleId.Codex or ModuleId.AiChat or ModuleId.Agents => NavigationGroup.AI,
        ModuleId.Git or ModuleId.GitHub or ModuleId.Pipeline or ModuleId.System or ModuleId.Awake => NavigationGroup.Work,
        ModuleId.Tasks or ModuleId.Weather or ModuleId.Markets => NavigationGroup.Life,
        _ => NavigationGroup.Now,
    };
}
