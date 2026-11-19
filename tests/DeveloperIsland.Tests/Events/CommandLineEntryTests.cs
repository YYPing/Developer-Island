using DeveloperIsland.Core.Events.Sources;
using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests.Events;

/// <summary>
/// Covers the two command-line entry points' logic. The reading path is tested through a real
/// stream because a GUI-subsystem executable's standard input behaves differently from a console
/// app's, and a hook that reads nothing writes no report and says nothing about it.
/// </summary>
public class CommandLineEntryTests
{
    [Fact]
    public void A_hook_payload_is_read_from_a_stream()
    {
        const string payload = """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\work\\app"}""";
        using var reader = new StringReader(payload);

        var text = HookInput.Read(reader, TimeSpan.FromSeconds(1));

        Assert.Equal(payload, text);
        Assert.Equal(AgentHookKind.NeedsPermission, AgentHookParser.Parse(text, DateTimeOffset.UnixEpoch)!.Kind);
    }

    [Fact]
    public void A_payload_larger_than_one_buffer_is_read_completely()
    {
        // Force at least two reads: the reader loop only continues while the buffer is filled.
        var payload = new string('x', 20_000);
        using var reader = new StringReader(payload);

        Assert.Equal(payload.Length, HookInput.Read(reader, TimeSpan.FromSeconds(1)).Length);
    }

    [Fact]
    public void An_empty_stream_yields_nothing_rather_than_hanging()
    {
        using var reader = new StringReader(string.Empty);

        Assert.Equal(string.Empty, HookInput.Read(reader, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_closed_stream_yields_nothing_rather_than_throwing()
    {
        var reader = new StringReader("something");
        reader.Dispose();

        Assert.Equal(string.Empty, HookInput.Read(reader, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_report_is_parsed_from_the_command_line()
    {
        var parsed = CommandLineReports.ParseCommandLine(
            ["--report", "pipeline", "Build", "exit 1", "--failed"],
            out var invalid);

        Assert.False(invalid);
        Assert.Equal("pipeline", parsed!.Value.Kind);
        Assert.Equal("Build", parsed.Value.Title);
        Assert.Equal("exit 1", parsed.Value.Detail);
        Assert.True(parsed.Value.Failed);
    }

    [Fact]
    public void A_flag_is_never_taken_as_the_detail_text()
    {
        var parsed = CommandLineReports.ParseCommandLine(
            ["--report", "pipeline", "Build", "--failed"],
            out _);

        Assert.Equal(string.Empty, parsed!.Value.Detail);
        Assert.True(parsed.Value.Failed);
    }

    [Fact]
    public void A_report_without_a_detail_is_allowed()
    {
        var parsed = CommandLineReports.ParseCommandLine(["--report", "agent", "Done"], out var invalid);

        Assert.False(invalid);
        Assert.Equal(string.Empty, parsed!.Value.Detail);
        Assert.False(parsed.Value.Failed);
    }

    [Fact]
    public void Too_few_values_is_reported_as_invalid_rather_than_silently_starting_the_app()
    {
        var parsed = CommandLineReports.ParseCommandLine(["--report", "pipeline"], out var invalid);

        Assert.Null(parsed);
        Assert.True(invalid);
    }

    [Fact]
    public void Without_report_the_parser_stands_down_so_the_app_can_start()
    {
        var parsed = CommandLineReports.ParseCommandLine(["--demo"], out var invalid);

        Assert.Null(parsed);
        Assert.False(invalid, "a missing --report must not be treated as a usage error");
    }

    [Theory]
    [InlineData("pipeline")]
    [InlineData("agent")]
    [InlineData("message")]
    public void Every_kind_the_parser_accepts_is_one_the_island_writes(string kind)
    {
        // The two lists live in different files; a kind accepted by one and refused by the other is
        // a report that is parsed correctly and then thrown away.
        Assert.Contains(kind, LocalReports.Kinds);
    }

    [Fact]
    public void An_unknown_kind_is_not_silently_accepted()
    {
        Assert.DoesNotContain("nonsense", LocalReports.Kinds);
    }
}
