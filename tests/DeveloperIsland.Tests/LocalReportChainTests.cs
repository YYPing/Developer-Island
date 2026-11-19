using System.Text.Json;
using DeveloperIsland.Core.Events;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests;

/// <summary>
/// The <c>--report</c> entry point and the reader that turns its files into island events. Every test
/// works in its own temporary folder, so nothing here reads or writes the user's real inbox or feed.
/// </summary>
public class LocalReportChainTests
{
    [Fact]
    public void A_command_report_becomes_an_event_in_the_module_the_kind_names()
    {
        var report = new LocalReport("pipeline", "Build", "exit 1", Failed: true);

        var module = LocalReports.ModuleOf(report);
        var raised = LocalReports.ToEvent(report, module, "report-1");

        Assert.Equal(ModuleId.Pipeline, module);
        Assert.NotNull(raised);
        Assert.Equal(IslandEventKind.PipelineFailed, raised!.Kind);
        Assert.Equal(IslandEvent.PriorityOf(IslandEventKind.PipelineFailed), raised.Priority);
        Assert.Equal("Build", raised.Title);
        Assert.Equal("exit 1", raised.Detail);
        Assert.Equal(ModuleId.Pipeline, raised.Module);
        Assert.Equal("report-1", raised.DedupKey);
    }

    [Fact]
    public void An_agent_report_is_a_finish_or_a_failure_on_the_agent_source()
    {
        var ok = LocalReports.ToEvent(new LocalReport("agent", "Agent finished", "Ready for review"), ModuleId.Agents);
        var bad = LocalReports.ToEvent(new LocalReport("agent", "Agent failed", "", Failed: true), ModuleId.Agents);

        Assert.Equal(IslandEventKind.AiFinished, ok!.Kind);
        Assert.Equal(IslandEventKind.AiFailed, bad!.Kind);
        Assert.Equal(LocalReports.AgentHookEventSource, ok.Source);
        Assert.NotEqual("pipeline", ok.Source);
    }

    [Fact]
    public void A_message_report_goes_through_the_inbox_instead_of_raising_its_own_event()
    {
        var report = new LocalReport("message", "Reminder", "Review the release notes");

        Assert.Equal(ModuleId.Inbox, LocalReports.ModuleOf(report));
        Assert.Null(LocalReports.ToEvent(report, ModuleId.Inbox));
    }

    [Fact]
    public void The_report_entry_point_rejects_an_unknown_kind_instead_of_writing_it()
    {
        using var folder = new TempFolder();

        Assert.Throws<ArgumentException>(() => LocalReports.Write(folder.Path, new LocalReport("deploy", "Deploy", "")));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void A_written_report_is_complete_json_before_it_is_visible_to_the_reader()
    {
        using var folder = new TempFolder();

        LocalReports.Write(folder.Path, new LocalReport("pipeline", "Tests", "Passed", Failed: false));

        var file = Assert.Single(Directory.GetFiles(folder.Path));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
        var read = JsonSerializer.Deserialize<LocalReport>(File.ReadAllText(file))!;
        Assert.Equal("pipeline", read.Kind);
        Assert.False(read.Failed);
    }

    /// <summary>
    /// Mirrors what <c>DeveloperIsland.exe --report</c> does, without starting the app: write the file,
    /// then read it the way the host's drain pass does.
    /// </summary>
    [Fact]
    public async Task A_report_on_disk_reaches_the_bus_with_the_bus_own_rules()
    {
        using var folder = new TempFolder();
        using var bus = new IslandEventBus();

        LocalReports.Write(folder.Path, new LocalReport("pipeline", "Tests", "Passed"));

        var drained = 0;
        foreach (var path in Directory.EnumerateFiles(folder.Path, "*.json"))
        {
            var report = JsonSerializer.Deserialize<LocalReport>(File.ReadAllText(path))!;
            var module = LocalReports.ModuleOf(report);
            if (LocalReports.ToEvent(report, module, Path.GetFileName(path)) is { } raised)
            {
                Assert.True(bus.Publish(raised));
                drained++;
            }

            File.Delete(path);
        }

        Assert.Equal(1, drained);
        var published = Assert.Single(bus.Feed);
        Assert.Equal("Tests", published.Title);
        // A success is quiet: it is readable in the feed but does not take the capsule.
        Assert.Equal(IslandEventKind.PipelineFinished, published.Kind);
        Assert.NotEqual(IslandEvent.PriorityOf(IslandEventKind.PipelineFailed), published.Priority);
        Assert.Empty(Directory.GetFiles(folder.Path));

        await Task.CompletedTask;
    }

    [Fact]
    public void A_failed_report_is_the_one_that_takes_the_capsule()
    {
        using var bus = new IslandEventBus();
        var report = new LocalReport("pipeline", "Tests", "exit 1", Failed: true);

        bus.Publish(LocalReports.ToEvent(report, LocalReports.ModuleOf(report))!);

        Assert.Equal("Tests", bus.Top?.Title);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di-report-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
