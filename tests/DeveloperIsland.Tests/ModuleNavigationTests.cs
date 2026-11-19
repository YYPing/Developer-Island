using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Tests;

/// <summary>
/// The four navigation groups replace nine flat tabs. A module that silently falls into the default
/// group is a module the user cannot find, so every module is checked to be in a group on purpose.
/// </summary>
public class ModuleNavigationTests
{
    [Fact]
    public void Every_module_is_reachable_from_some_group()
    {
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            var group = ModuleNavigation.GroupOf(module);
            Assert.True(Enum.IsDefined(group), $"{module} has no navigation group");
        }
    }

    [Fact]
    public void The_original_ten_modules_keep_their_home()
    {
        // Upstream's modules must stay reachable after the regroupping; losing Music or System in a
        // navigation change is exactly the regression a user notices weeks later.
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Music));
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Focus));
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Calendar));
        Assert.Equal(NavigationGroup.AI, ModuleNavigation.GroupOf(ModuleId.Claude));
        Assert.Equal(NavigationGroup.AI, ModuleNavigation.GroupOf(ModuleId.Codex));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.Git));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.GitHub));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.System));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.Awake));
        Assert.Equal(NavigationGroup.Life, ModuleNavigation.GroupOf(ModuleId.Tasks));
    }

    [Fact]
    public void The_new_modules_sit_with_what_they_belong_to()
    {
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Feed));
        Assert.Equal(NavigationGroup.AI, ModuleNavigation.GroupOf(ModuleId.AiChat));
        Assert.Equal(NavigationGroup.AI, ModuleNavigation.GroupOf(ModuleId.Agents));
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Inbox));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.Pipeline));
        Assert.Equal(NavigationGroup.Life, ModuleNavigation.GroupOf(ModuleId.Weather));
        Assert.Equal(NavigationGroup.Life, ModuleNavigation.GroupOf(ModuleId.Markets));
    }

    [Fact]
    public void No_group_is_empty_so_the_navigation_never_shows_a_dead_section()
    {
        foreach (var group in Enum.GetValues<NavigationGroup>())
        {
            Assert.Contains(Enum.GetValues<ModuleId>(), m => ModuleNavigation.GroupOf(m) == group);
        }
    }

    [Fact]
    public void Life_holds_only_tasks_weather_and_markets()
    {
        // The user asked for exactly these three; anything else here would be health or finance creep.
        var life = Enum.GetValues<ModuleId>()
            .Where(m => ModuleNavigation.GroupOf(m) == NavigationGroup.Life)
            .ToHashSet();

        Assert.Equal([ModuleId.Tasks, ModuleId.Weather, ModuleId.Markets], life.Order().ToArray());
    }
}
