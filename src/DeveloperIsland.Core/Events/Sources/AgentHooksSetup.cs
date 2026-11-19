using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Events.Sources;

/// <summary>The subset of Claude Code's hook events the island subscribes to, and why each one.</summary>
public sealed record AgentHookSubscription(string EventName, string? Matcher, string Reason, bool Async = true);

/// <summary>What state Claude Code's settings are in with respect to the island's hooks.</summary>
public enum AgentHooksConnection
{
    /// <summary>Claude Code's settings do not contain our hooks.</summary>
    NotConnected,

    /// <summary>All of our hooks are present.</summary>
    Connected,

    /// <summary>Some of our hooks are present but others were removed or edited by hand.</summary>
    Partial,

    /// <summary>Claude Code's settings could not be read or written.</summary>
    Unreadable,
}

public sealed record AgentHooksInspection(AgentHooksConnection Connection, string? Message = null)
{
    public bool IsConnected => Connection == AgentHooksConnection.Connected;
}

/// <summary>
/// Connects Developer Island to Claude Code's lifecycle by editing only the <c>hooks</c> entries it
/// owns in <c>~/.claude/settings.json</c>. Everything else in the file is preserved, a backup is
/// written before the first change, and hooks the user set up themselves are never touched: our
/// entries are recognized by the <c>--claude-hook</c> argument they carry.
/// <para>
/// Every hook is marked <c>async</c> so Claude Code never waits on us. Writing one small file is fast
/// anyway, but a synchronous hook would still put our process on Claude Code's critical path, and no
/// status capsule justifies that.
/// </para>
/// </summary>
public static class AgentHooksSetup
{
    /// <summary>The command-line argument the hook command runs with.</summary>
    public const string Argument = "--claude-hook";

    /// <summary>
    /// The events the island listens to. Matchers follow Claude Code's documented values; an event
    /// without matcher support gets none, because Claude Code silently ignores one that is set.
    /// </summary>
    public static readonly IReadOnlyList<AgentHookSubscription> Subscriptions =
    [
        new("SessionStart", "startup|resume", "Claude Code started"),
        new("PermissionRequest", null, "Claude Code is asking for permission"),
        new("Notification", "permission_prompt", "A permission prompt has waited six seconds"),
        new("Notification", "idle_prompt", "Claude Code is waiting for you"),
        new("Stop", null, "Claude Code finished replying"),
        new("StopFailure", null, "The turn ended on an API error"),
        new("PostToolUseFailure", null, "A tool failed"),
        new("SessionEnd", null, "The session ended"),
    ];

    public static string SettingsPath(string? configDir = null)
    {
        var dir = configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(dir, "settings.json");
    }

    /// <summary>The command Claude Code runs for one hook. Quoted: the path can contain spaces.</summary>
    public static string CommandFor(string executable) => $"\"{executable}\" {Argument}";

    /// <summary>Whether a configured command is ours (as opposed to one the user set up).</summary>
    public static bool IsOurs(string? command) => command is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(command.Trim(), "^\"[^\"]+\" --claude-hook$");

    public static AgentHooksInspection Inspect(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new AgentHooksInspection(AgentHooksConnection.NotConnected);
            }

            var root = Read(settingsPath);
            if (root?["hooks"] is not JsonObject hooks)
            {
                return new AgentHooksInspection(AgentHooksConnection.NotConnected);
            }

            var found = CountOurs(hooks);
            return found switch
            {
                0 => new AgentHooksInspection(AgentHooksConnection.NotConnected),
                var n when n >= Subscriptions.Count => new AgentHooksInspection(AgentHooksConnection.Connected),
                _ => new AgentHooksInspection(
                    AgentHooksConnection.Partial,
                    $"Only {found} of {Subscriptions.Count} hooks are present"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new AgentHooksInspection(AgentHooksConnection.Unreadable, ex.Message);
        }
    }

    /// <summary>
    /// Adds our hooks, idempotently. Existing entries of ours are replaced so a moved executable is
    /// picked up; other hooks in the same event are kept.
    /// </summary>
    public static AgentHooksInspection Connect(string settingsPath, string executable)
    {
        try
        {
            var root = Read(settingsPath) ?? new JsonObject();
            root["hooks"] ??= new JsonObject();
            var hooks = (JsonObject)root["hooks"]!;

            foreach (var eventName in Subscriptions.Select(s => s.EventName).Distinct())
            {
                if (hooks[eventName] is JsonArray existing) RemoveOurGroups(existing);
            }

            foreach (var subscription in Subscriptions)
            {
                var groups = hooks[subscription.EventName] as JsonArray;
                if (groups is null)
                {
                    groups = [];
                    hooks[subscription.EventName] = groups;
                }

                groups.Add(new JsonObject
                {
                    ["matcher"] = subscription.Matcher,
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = CommandFor(executable),
                            ["async"] = subscription.Async,
                        },
                    },
                });
            }

            Write(settingsPath, root);
            var inspection = Inspect(settingsPath);
            return inspection.IsConnected ? inspection : new AgentHooksInspection(AgentHooksConnection.Partial, "Written, but the file did not read back as expected");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new AgentHooksInspection(AgentHooksConnection.Unreadable, ex.Message);
        }
    }

    /// <summary>Removes only our hooks, leaving every other hook and setting in place.</summary>
    public static AgentHooksInspection Disconnect(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new AgentHooksInspection(AgentHooksConnection.NotConnected);
            }

            var root = Read(settingsPath);
            if (root?["hooks"] is not JsonObject hooks)
            {
                return new AgentHooksInspection(AgentHooksConnection.NotConnected);
            }

            foreach (var eventName in Subscriptions.Select(s => s.EventName).Distinct().ToList())
            {
                if (hooks[eventName] is not JsonArray groups)
                {
                    continue;
                }

                RemoveOurGroups(groups);

                if (groups.Count == 0)
                {
                    hooks.Remove(eventName);
                }
            }

            Write(settingsPath, root);
            return new AgentHooksInspection(AgentHooksConnection.NotConnected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new AgentHooksInspection(AgentHooksConnection.Unreadable, ex.Message);
        }
    }

    private static void RemoveOurGroups(JsonArray groups)
    {
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] is not JsonObject group)
            {
                continue;
            }

            // Remove only our handlers; a group the user built that happens to contain one of ours
            // keeps its other handlers.
            if (group["hooks"] is JsonArray handlers)
            {
                for (var h = handlers.Count - 1; h >= 0; h--)
                {
                    if (handlers[h] is JsonObject handler && IsOurs(handler["command"]?.GetValue<string>()))
                    {
                        handlers.RemoveAt(h);
                    }
                }

                if (handlers.Count == 0)
                {
                    groups.RemoveAt(i);
                }
            }
        }
    }

    private static int CountOurs(JsonObject hooks) => Subscriptions.Count(subscription =>
        hooks[subscription.EventName] is JsonArray groups && groups.OfType<JsonObject>().Any(group =>
            group["matcher"]?.GetValue<string>() == subscription.Matcher &&
            group["hooks"] is JsonArray handlers && handlers.OfType<JsonObject>().Any(h =>
                h["type"]?.GetValue<string>() == "command" &&
                IsOurs(h["command"]?.GetValue<string>()) &&
                h["async"]?.GetValue<bool>() == subscription.Async)));

    private static JsonObject? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("Settings must contain a JSON object.");
    }

    private static void Write(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // One backup, before the first change, so a user can always get their original file back.
        if (File.Exists(path))
        {
            var backup = path + ".before-developer-island";
            if (!File.Exists(backup))
            {
                try
                {
                    File.Copy(path, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("events", $"could not back up Claude Code settings: {ex.GetType().Name}");
                }
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(options));
        File.Move(temp, path, overwrite: true);
    }
}
