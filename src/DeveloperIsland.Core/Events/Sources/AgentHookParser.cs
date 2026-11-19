using System.Text.Json;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.Events.Sources;

/// <summary>
/// The vocabulary a Claude Code hook reports, reduced to what the island actually needs to say.
/// Names follow Claude Code's hook events so a reader can match them against its documentation.
/// </summary>
public enum AgentHookKind
{
    /// <summary>SessionStart.</summary>
    Started,

    /// <summary>PermissionRequest: a tool is about to run and Claude Code is asking.</summary>
    NeedsPermission,

    /// <summary>Notification with idle_prompt: Claude finished and you have not typed since.</summary>
    WaitingForInput,

    /// <summary>Stop: the main agent finished responding.</summary>
    Finished,

    /// <summary>StopFailure: the turn ended on an API error.</summary>
    Failed,

    /// <summary>PostToolUseFailure: a tool that started executing failed.</summary>
    ToolFailed,

    /// <summary>SessionEnd.</summary>
    Ended,
}

/// <summary>
/// One report from a Claude Code hook. Deliberately narrow: the hook command receives the full event
/// payload from Claude Code, including prompts and paths, and drops nearly all of it here. Only the
/// kind, the project folder name and the session id cross into the island.
/// </summary>
/// <param name="SessionId">
/// Claude Code's session id, used to deduplicate and to tell two concurrent sessions apart. Never shown.
/// </param>
/// <param name="Project">The project's folder name (not its full path), as a short label.</param>
/// <param name="Tool">
/// The tool name for a tool failure (an error is much easier to act on when it says which tool).
/// Never the tool's input or output.
/// </param>
public sealed record AgentHookReport(
    AgentHookKind Kind,
    string? SessionId,
    string? Project,
    DateTimeOffset At,
    string? Tool = null)
{
    public IslandEvent ToEvent() => new(
        Source: ClaudeCodeEventSource.SourceId,
        Kind: ToEventKind(Kind),
        Priority: PriorityOf(Kind),
        Title: TitleOf(Kind, Project, Tool),
        Detail: Project,
        Module: ModuleId.Agents,
        DedupKey: SessionId is { Length: > 0 } id ? $"{Kind}:{id}" : null);

    private static IslandEventKind ToEventKind(AgentHookKind kind) => kind switch
    {
        AgentHookKind.Started => IslandEventKind.AiRunning,
        AgentHookKind.NeedsPermission => IslandEventKind.AiWaitingForInput,
        AgentHookKind.WaitingForInput => IslandEventKind.AiWaitingForInput,
        AgentHookKind.Finished => IslandEventKind.AiFinished,
        AgentHookKind.Failed or AgentHookKind.ToolFailed => IslandEventKind.AiFailed,
        // A session ending is not an interruption: there is nothing for the user to do about it.
        _ => IslandEventKind.Info,
    };

    private static int PriorityOf(AgentHookKind kind) => kind switch
    {
        AgentHookKind.Failed or AgentHookKind.ToolFailed => 50,
        AgentHookKind.NeedsPermission or AgentHookKind.WaitingForInput => 40,
        AgentHookKind.Finished => 30,
        AgentHookKind.Started => 10,
        _ => 0,
    };

    private static string TitleOf(AgentHookKind kind, string? project, string? tool) => kind switch
    {
        AgentHookKind.Started => Localization.Loc.T(Localization.Text.ActivityClaudeStarted),
        AgentHookKind.NeedsPermission => Localization.Loc.T(Localization.Text.ActivityClaudeNeedsPermission),
        AgentHookKind.WaitingForInput => Localization.Loc.T(Localization.Text.ActivityClaudeWaiting),
        AgentHookKind.Finished => Localization.Loc.T(Localization.Text.ActivityClaudeFinished),
        AgentHookKind.Failed => Localization.Loc.T(Localization.Text.ActivityClaudeApiError),
        // Naming the tool is the difference between "something broke" and something actionable.
        AgentHookKind.ToolFailed when tool is { Length: > 0 } =>
            Localization.Loc.T(Localization.Text.ActivityActivityToolFailed, tool),
        AgentHookKind.ToolFailed => Localization.Loc.T(Localization.Text.ActivityClaudeApiError),
        _ => Localization.Loc.T(Localization.Text.ActivityClaudeSessionEnded),
    };
}

/// <summary>
/// Parses what a hook command receives on stdin, following Claude Code's documented hook input.
/// Everything the island does not need (prompt text, assistant messages, tool inputs, file paths)
/// is dropped here and never stored.
/// </summary>
public static class AgentHookParser
{
    /// <summary>
    /// Reads one hook payload. Returns null when the payload is not a hook event or carries no kind
    /// the island cares about, so an unrecognized report can never disturb Claude Code.
    /// </summary>
    public static AgentHookReport? Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hookName = String(root, "hook_event_name");
            if (hookName is null)
            {
                return null;
            }

            var kind = Map(hookName, String(root, "notification_type"));
            if (kind is null)
            {
                return null;
            }

            return new AgentHookReport(
                kind.Value,
                String(root, "session_id"),
                ProjectFolderName(String(root, "cwd")),
                now,
                String(root, "tool_name"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentHookKind? Map(string hookName, string? notificationType) => hookName switch
    {
        "SessionStart" => AgentHookKind.Started,
        "SessionEnd" => AgentHookKind.Ended,
        "PermissionRequest" => AgentHookKind.NeedsPermission,
        "Stop" => AgentHookKind.Finished,
        "StopFailure" => AgentHookKind.Failed,
        "PostToolUseFailure" => AgentHookKind.ToolFailed,
        "Notification" => notificationType switch
        {
            "permission_prompt" => AgentHookKind.NeedsPermission,
            "idle_prompt" => AgentHookKind.WaitingForInput,
            "agent_needs_input" => AgentHookKind.WaitingForInput,

            // Everything else Claude Code notifies about (auth, MCP elicitation, plugin notices,
            // quota resumes) is not something the capsule should interrupt for.
            _ => null,
        },

        // PostToolUse and every other event are deliberately absent: routine tool use, prompts and
        // model switches are noise at the capsule's level.
        _ => null,
    };

    /// <summary>
    /// The project's folder name only. Claude Code sends the full working directory; taking the last
    /// segment keeps a home directory name and a client path out of the island and out of its logs.
    /// </summary>
    public static string? ProjectFolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        var name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
