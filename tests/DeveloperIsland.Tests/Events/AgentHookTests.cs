using DeveloperIsland.Core.Events;
using DeveloperIsland.Core.Events.Sources;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests.Events;

public class AgentHookTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_permission_prompt_becomes_a_waiting_event_with_the_agent_priority()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"Notification","notification_type":"permission_prompt","session_id":"s1","cwd":"C:\\work\\developer-island"}""",
            _time.GetUtcNow());

        Assert.NotNull(report);
        Assert.Equal(AgentHookKind.NeedsPermission, report!.Kind);

        var e = report.ToEvent();
        Assert.Equal(IslandEventKind.AiWaitingForInput, e.Kind);
        Assert.Equal(40, e.Priority);
        Assert.Equal("Claude Code needs permission", e.Title);
    }

    [Fact]
    public void Only_the_project_folder_name_survives_never_the_full_path()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"Stop","session_id":"s1","cwd":"C:\\Users\\someone\\secret-client\\app"}""",
            _time.GetUtcNow());

        Assert.NotNull(report);
        Assert.Equal("app", report!.Project);

        // The full path must not appear anywhere the island would store or show it.
        var e = report.ToEvent();
        Assert.DoesNotContain("secret-client", e.Title);
        Assert.DoesNotContain("secret-client", e.Detail ?? string.Empty);
    }

    [Fact]
    public void Prompts_and_transcripts_are_dropped_because_the_report_has_nowhere_to_put_them()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"Stop","session_id":"s1","cwd":"/home/me/app","prompt":"my api key is sk-secret","transcript_path":"/tmp/t.jsonl"}""",
            _time.GetUtcNow());

        Assert.NotNull(report);

        // The report type carries exactly four fields; none of them can hold prompt content.
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("sk-secret", serialized);
        Assert.DoesNotContain("transcript", serialized);
    }

    [Theory]
    [InlineData("SessionStart", AgentHookKind.Started)]
    [InlineData("Stop", AgentHookKind.Finished)]
    [InlineData("SessionEnd", AgentHookKind.Ended)]
    public void Claude_codes_hook_names_map_to_kinds(string hookName, AgentHookKind expected)
    {
        var report = AgentHookParser.Parse($$"""{"hook_event_name":"{{hookName}}","session_id":"s1","cwd":"/a/b"}""", _time.GetUtcNow());

        Assert.Equal(expected, report!.Kind);
    }

    [Fact]
    public void An_idle_notification_is_a_wait()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"Notification","notification_type":"idle_prompt","session_id":"s1","cwd":"/a/b"}""",
            _time.GetUtcNow());

        Assert.Equal(AgentHookKind.WaitingForInput, report!.Kind);
        Assert.Equal("Claude Code is waiting", report.ToEvent().Title);
    }

    [Fact]
    public void A_successful_tool_use_never_reaches_the_island()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"/a/b","tool_name":"Read"}""",
            _time.GetUtcNow());

        Assert.Null(report);
    }

    [Fact]
    public void A_failed_tool_names_the_tool_so_the_alert_is_actionable()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"PostToolUseFailure","session_id":"s1","cwd":"/a/b","tool_name":"Bash"}""",
            _time.GetUtcNow());

        Assert.Equal(AgentHookKind.ToolFailed, report!.Kind);
        var e = report.ToEvent();
        Assert.Equal(50, e.Priority);
        Assert.Equal("Bash failed", e.Title);
    }

    [Fact]
    public void An_api_error_ending_the_turn_is_a_failure()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"StopFailure","session_id":"s1","cwd":"/a/b","error":"rate_limit"}""",
            _time.GetUtcNow());

        Assert.Equal(AgentHookKind.Failed, report!.Kind);
        Assert.Equal("Claude Code hit an API error", report.ToEvent().Title);
    }

    [Fact]
    public void PermissionRequest_signals_the_moment_Claude_asks()
    {
        var report = AgentHookParser.Parse(
            """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"/a/b","tool_name":"Bash"}""",
            _time.GetUtcNow());

        Assert.Equal(AgentHookKind.NeedsPermission, report!.Kind);
    }

    [Theory]
    [InlineData("auth_success")]
    [InlineData("elicitation_dialog")]
    [InlineData("plugin_notification")]
    [InlineData("quota_auto_resume_fired")]
    public void Notifications_that_are_not_about_a_person_are_ignored(string type)
    {
        var report = AgentHookParser.Parse(
            $$"""{"hook_event_name":"Notification","notification_type":"{{type}}","session_id":"s1","cwd":"/a/b"}""",
            _time.GetUtcNow());

        Assert.Null(report);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"hook_event_name":"SomeUnknownHook"}""")]
    [InlineData("[]")]
    public void Malformed_or_irrelevant_payloads_are_ignored_rather_than_thrown(string payload)
    {
        Assert.Null(AgentHookParser.Parse(payload, _time.GetUtcNow()));
    }

    [Fact]
    public void An_ended_session_is_kept_out_of_the_capsule_but_still_reported()
    {
        var e = new AgentHookReport(AgentHookKind.Ended, "s1", "app", _time.GetUtcNow()).ToEvent();

        Assert.Equal(IslandEventKind.Info, e.Kind);
        Assert.Equal(0, e.Priority);
    }

    [Fact]
    public void The_dedup_key_separates_two_sessions_of_the_same_kind()
    {
        var one = new AgentHookReport(AgentHookKind.Finished, "session-a", "app", _time.GetUtcNow()).ToEvent();
        var two = new AgentHookReport(AgentHookKind.Finished, "session-b", "app", _time.GetUtcNow()).ToEvent();

        Assert.NotEqual(one.DedupKey, two.DedupKey);
    }

    [Fact]
    public void Reports_round_trip_through_the_inbox()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        var at = _time.GetUtcNow();

        Assert.True(inbox.Write(new AgentHookReport(AgentHookKind.NeedsPermission, "s1", "app", at)));
        Assert.True(inbox.Write(new AgentHookReport(AgentHookKind.Finished, "s2", "other", at.AddSeconds(1))));

        var drained = inbox.Drain(at.AddSeconds(2));

        Assert.Equal(2, drained.Count);
        Assert.Equal(AgentHookKind.NeedsPermission, drained[0].Kind);
        Assert.Equal("s1", drained[0].SessionId);
        Assert.Equal("app", drained[0].Project);
    }

    [Fact]
    public void Draining_removes_the_files_so_nothing_is_reported_twice()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        inbox.Write(new AgentHookReport(AgentHookKind.Finished, "s1", "app", _time.GetUtcNow()));

        Assert.Single(inbox.Drain(_time.GetUtcNow()));
        Assert.Empty(inbox.Drain(_time.GetUtcNow()));
    }

    [Fact]
    public void A_stale_report_is_discarded_rather_than_replayed_late()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        var old = _time.GetUtcNow() - AgentHookInbox.MaxAge - TimeSpan.FromMinutes(1);
        inbox.Write(new AgentHookReport(AgentHookKind.NeedsPermission, "s1", "app", old));

        Assert.Empty(inbox.Drain(_time.GetUtcNow()));
    }

    [Fact]
    public void A_report_from_the_future_is_also_discarded()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        inbox.Write(new AgentHookReport(AgentHookKind.NeedsPermission, "s1", "app", _time.GetUtcNow().AddHours(1)));

        Assert.Empty(inbox.Drain(_time.GetUtcNow()));
    }

    [Fact]
    public void Writing_to_an_unusable_folder_returns_false_instead_of_throwing()
    {
        // A file where a folder would have to be: creating the directory must fail.
        using var folder = new TempFolder();
        var blocked = Path.Combine(folder.Path, "blocked");
        File.WriteAllText(blocked, "not a directory");

        var inbox = new AgentHookInbox(blocked);

        Assert.False(inbox.Write(new AgentHookReport(AgentHookKind.Finished, "s1", "app", _time.GetUtcNow())));
    }

    [Fact]
    public void Stale_reports_from_a_previous_run_are_cleared_at_start()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        inbox.Write(new AgentHookReport(AgentHookKind.NeedsPermission, "s1", "app", _time.GetUtcNow()));

        inbox.ClearStale();

        Assert.Empty(inbox.Drain(_time.GetUtcNow()));
    }

    [Fact]
    public async Task The_source_drains_the_inbox_and_raises_events()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        var source = new ClaudeCodeEventSource(inbox, _time);
        var seen = new List<IslandEvent>();
        source.Event += e => seen.Add(e);

        await source.StartAsync(TestContext.Current.CancellationToken);

        inbox.Write(new AgentHookReport(AgentHookKind.Finished, "s1", "app", _time.GetUtcNow()));
        source.Drain();

        var e = Assert.Single(seen);
        Assert.Equal(IslandEventKind.AiFinished, e.Kind);
        Assert.Equal("app", e.Detail);
    }

    [Fact]
    public async Task The_source_reports_ready_once_started()
    {
        using var folder = new TempFolder();
        var source = new ClaudeCodeEventSource(new AgentHookInbox(folder.Path), _time);

        await source.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EventSourceStatus.Ready, source.State.Status);
    }

    [Fact]
    public async Task The_source_stops_reporting_after_disposal()
    {
        using var folder = new TempFolder();
        var inbox = new AgentHookInbox(folder.Path);
        var source = new ClaudeCodeEventSource(inbox, _time);
        var seen = 0;
        source.Event += _ => seen++;

        await source.StartAsync(TestContext.Current.CancellationToken);
        source.Dispose();

        inbox.Write(new AgentHookReport(AgentHookKind.Finished, "s1", "app", _time.GetUtcNow()));
        source.Drain();

        Assert.Equal(0, seen);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di-tests-" + Guid.NewGuid().ToString("N")[..10]);
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path))
                {
                    System.IO.Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
