using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests;

/// <summary>
/// The CI history rule: the first look at a repository is a baseline, and only outcomes that happen
/// while the island watches are announced. These tests exist because the wiring once read the run
/// list without ever consulting it, which announced five old results on every start.
/// </summary>
public class CiRunWatcherTests
{
    private static CiRun Run(long id, string? conclusion, string title = "CI", string updatedAt = "2026-11-18T09:00:00Z") =>
        new(id, title, conclusion is null ? "in_progress" : "completed", conclusion, updatedAt, "https://github.test/run/" + id);

    private static string Json(params string[] runs) => "[" + string.Join(",", runs) + "]";

    private static string RunJson(long id, string status = "completed", string? conclusion = "success", string title = "Build") =>
        $$"""{"databaseId":{{id}},"displayTitle":"{{title}}","status":"{{status}}","conclusion":{{(conclusion is null ? "null" : "\"" + conclusion + "\"")}},"updatedAt":"2026-11-18T09:0{{id % 10}}:00Z","url":"https://github.test/run/{{id}}"}""";

    [Fact]
    public void The_first_look_is_a_baseline_and_announces_nothing()
    {
        var watcher = new CiRunWatcher();

        var news = watcher.Observe([Run(1, "success"), Run(2, "failure"), Run(3, "success")]);

        Assert.Empty(news);
        Assert.True(watcher.IsPrimed);
        Assert.Equal(3, watcher.RememberedCount);
    }

    [Fact]
    public void A_run_that_finishes_after_the_baseline_is_announced_once()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([Run(1, "success"), Run(2, "failure")]);

        // Run 3 was not in the baseline: it is the one outcome worth telling the user about.
        Assert.Equal([3L], watcher.Observe([Run(3, "failure"), Run(1, "success"), Run(2, "failure")]).Select(r => r.Id));

        // Seen once is enough, however often the list is polled.
        Assert.Empty(watcher.Observe([Run(3, "failure"), Run(1, "success"), Run(2, "failure")]));
    }

    [Fact]
    public void A_run_that_was_still_going_at_the_baseline_becomes_news_when_it_finishes()
    {
        var watcher = new CiRunWatcher();

        // Run 2 was still going at the first look, so it was not part of the baseline…
        Assert.Empty(watcher.Observe([Run(1, "success"), Run(2, null)]));

        // …and its outcome is exactly the news the capsule exists for.
        Assert.Equal([2L], watcher.Observe([Run(1, "success"), Run(2, "failure")]).Select(r => r.Id));
    }

    [Fact]
    public void A_rerun_that_ends_differently_is_announced_again()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([Run(1, "failure")]);

        var news = watcher.Observe([Run(1, "success")]);

        Assert.Equal([1L], news.Select(r => r.Id));
        Assert.Equal("success", news[0].Conclusion);
    }

    [Fact]
    public void Bookkeeping_timestamp_changes_alone_never_flash_the_capsule_again()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([Run(1, "failure", updatedAt: "2026-11-18T09:00:00Z")]);

        // GitHub bumps updatedAt after the fact; the outcome is the same run.
        Assert.Empty(watcher.Observe([Run(1, "failure", updatedAt: "2026-11-18T11:30:00Z")]));
    }

    [Fact]
    public void Pointing_at_another_repository_makes_its_history_a_baseline_again()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([Run(1, "failure")]);
        Assert.Single(watcher.Observe([Run(9, "failure")]));

        watcher.Reset();

        Assert.False(watcher.IsPrimed);
        Assert.Empty(watcher.Observe([Run(9, "failure"), Run(10, "success")]));
    }

    [Fact]
    public void Runs_still_going_or_without_a_conclusion_are_ignored()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([]);

        Assert.Empty(watcher.Observe([Run(1, null), Run(2, null)]));
        Assert.Equal(0, watcher.RememberedCount);
    }

    [Fact]
    public void The_remembered_set_stays_bounded()
    {
        var watcher = new CiRunWatcher();
        watcher.Observe([]);

        for (var i = 1; i <= CiRunWatcher.MaxRemembered + 50; i++)
        {
            watcher.Observe([Run(i, "success")]);
        }

        Assert.Equal(CiRunWatcher.MaxRemembered, watcher.RememberedCount);
    }

    [Fact]
    public void Gh_output_is_parsed_including_runs_without_a_conclusion()
    {
        var runs = CiRun.ParseList(Json(RunJson(11, "completed", "failure"), RunJson(12, "in_progress", null), RunJson(13, "completed", "success")));

        Assert.Equal(3, runs.Count);
        Assert.Equal(11, runs[0].Id);
        Assert.Equal("failure", runs[0].Conclusion);
        Assert.True(runs[0].IsTerminal);
        Assert.False(runs[1].IsTerminal);
        Assert.Equal("Build", runs[1].Title);
        Assert.Equal("https://github.test/run/12", runs[1].Url);
    }

    [Fact]
    public void One_odd_entry_does_not_hide_the_others()
    {
        var runs = CiRun.ParseList(Json("""{"displayTitle":"no id","status":"completed","conclusion":"success"}""", RunJson(21)));

        var run = Assert.Single(runs);
        Assert.Equal(21, run.Id);
    }

    [Fact]
    public void A_document_that_is_not_a_run_list_raises_the_error_the_panel_reports()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CiRun.ParseList("""{"message":"Not Found"}"""));
    }

    [Fact]
    public void A_run_is_described_with_its_source_time_and_link()
    {
        var runs = CiRun.ParseList(Json(RunJson(31, "completed", "failure")));
        var text = runs[0].Describe();

        Assert.Contains("Build · completed failure", text);
        Assert.Contains("https://github.test/run/31", text);
    }
}
