using System.Text.Json.Nodes;
using DeveloperIsland.Core.Events.Sources;

namespace DeveloperIsland.Tests.Events;

public class AgentHooksSetupTests
{
    private const string Executable = @"C:\Program Files\Developer Island\DeveloperIsland.exe";

    [Fact]
    public void Connecting_into_a_missing_file_creates_it_with_every_subscription()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, ".claude", "settings.json");

        var result = AgentHooksSetup.Connect(path, Executable);

        Assert.True(result.IsConnected);
        var root = Read(path);
        var hooks = root!["hooks"]!.AsObject();

        // Every event we subscribe to is there, with one handler per subscription.
        var expectedHandlers = AgentHooksSetup.Subscriptions.Count;
        var actual = hooks.Sum(kv => kv.Value!.AsArray().Sum(g => g!["hooks"]!.AsArray().Count));
        Assert.Equal(expectedHandlers, actual);
    }

    [Fact]
    public void Every_installed_hook_is_async_so_Claude_Code_never_waits_on_us()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");

        AgentHooksSetup.Connect(path, Executable);

        foreach (var group in Read(path)!["hooks"]!.AsObject().SelectMany(kv => kv.Value!.AsArray()))
        {
            foreach (var handler in group!["hooks"]!.AsArray())
            {
                Assert.True(handler!["async"]!.GetValue<bool>(), "a synchronous hook would block Claude Code");
            }
        }
    }

    [Fact]
    public void Connecting_twice_does_not_duplicate_hooks()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");

        AgentHooksSetup.Connect(path, Executable);
        AgentHooksSetup.Connect(path, Executable);

        var first = CountOurs(path);
        AgentHooksSetup.Connect(path, Executable);
        AgentHooksSetup.Connect(path, Executable);

        Assert.Equal(AgentHooksSetup.Subscriptions.Count, first);
        Assert.Equal(first, CountOurs(path));
        Assert.True(AgentHooksSetup.Inspect(path).IsConnected);
    }

    [Fact]
    public void Duplicate_handlers_cannot_hide_a_missing_subscription()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        AgentHooksSetup.Connect(path, Executable);
        var root = Read(path)!;
        root["hooks"]!["Stop"]!.AsArray().Clear();
        var notifications = root["hooks"]!["Notification"]!.AsArray();
        for (var i = 0; i < 10; i++) notifications.Add(notifications[0]!.DeepClone());
        File.WriteAllText(path, root.ToJsonString());
        Assert.Equal(AgentHooksConnection.Partial, AgentHooksSetup.Inspect(path).Connection);
        AgentHooksSetup.Connect(path, @"D:\Moved\DeveloperIsland.exe");
        Assert.Equal(AgentHooksSetup.Subscriptions.Count, CountOurs(path));
        Assert.DoesNotContain("Program Files", File.ReadAllText(path));
        Assert.True(AgentHooksSetup.Inspect(path).IsConnected);
    }

    [Fact]
    public void A_users_own_hooks_and_settings_are_preserved()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """
        {
          "model": "opus",
          "statusLine": { "type": "command", "command": "my-status.sh" },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-guard.sh" } ] }
            ],
            "Stop": [
              { "matcher": null, "hooks": [ { "type": "command", "command": "my-notify.sh" } ] }
            ]
          }
        }
        """);

        AgentHooksSetup.Connect(path, Executable);

        var root = Read(path)!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("my-status.sh", root["statusLine"]!["command"]!.GetValue<string>());

        // The user's own handlers survive, in the very event we also subscribe to.
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Contains(stop, g => ContainsCommand(g, "my-notify.sh"));
        Assert.Contains(stop, g => ContainsCommand(g, AgentHooksSetup.Argument));
        Assert.Contains(root["hooks"]!["PreToolUse"]!.AsArray(), g => ContainsCommand(g, "my-guard.sh"));
    }

    [Fact]
    public void Disconnecting_removes_our_hooks_and_keeps_the_users()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "matcher": null, "hooks": [ { "type": "command", "command": "my-notify.sh" } ] } ]
          }
        }
        """);

        AgentHooksSetup.Connect(path, Executable);
        var result = AgentHooksSetup.Disconnect(path);

        Assert.Equal(AgentHooksConnection.NotConnected, result.Connection);
        Assert.Equal(0, CountOurs(path));

        var root = Read(path)!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Contains(root["hooks"]!["Stop"]!.AsArray(), g => ContainsCommand(g, "my-notify.sh"));
    }

    [Fact]
    public void Disconnecting_when_never_connected_is_harmless()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");

        var result = AgentHooksSetup.Disconnect(path);

        Assert.Equal(AgentHooksConnection.NotConnected, result.Connection);
    }

    [Fact]
    public void Removing_one_of_our_hooks_by_hand_is_reported_as_partial_not_connected()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        AgentHooksSetup.Connect(path, Executable);

        // Simulate the user deleting a single handler.
        var root = Read(path)!;
        var stop = root["hooks"]!["Stop"]!.AsArray();
        stop.Clear();
        File.WriteAllText(path, root.ToJsonString());

        var inspection = AgentHooksSetup.Inspect(path);

        Assert.Equal(AgentHooksConnection.Partial, inspection.Connection);
        Assert.Contains("of", inspection.Message);
    }

    [Fact]
    public void An_unreadable_file_is_reported_rather_than_thrown()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, "{ this is not json");

        var inspection = AgentHooksSetup.Inspect(path);

        Assert.Equal(AgentHooksConnection.Unreadable, inspection.Connection);
    }

    [Fact]
    public void A_backup_of_the_original_is_kept_before_the_first_change()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"model":"opus"}""");

        AgentHooksSetup.Connect(path, Executable);

        var backup = path + ".before-developer-island";
        Assert.True(File.Exists(backup));
        Assert.Contains("opus", File.ReadAllText(backup));

        // A second connect must not overwrite the original backup with an already-hooked file.
        AgentHooksSetup.Connect(path, Executable);
        Assert.DoesNotContain("--claude-hook", File.ReadAllText(backup));
    }

    [Fact]
    public void Event_names_without_matcher_support_get_no_matcher()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        AgentHooksSetup.Connect(path, Executable);

        // Stop carries no matcher in the subscription table; writing one would be silently ignored.
        Assert.Null(AgentHooksSetup.Subscriptions.Single(s => s.EventName == "Stop" && s.Matcher is null).Matcher);

        var root = Read(path)!;
        var stopGroup = root["hooks"]!["Stop"]!.AsArray()[0]!;
        Assert.Null(stopGroup["matcher"]);
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude\settings.json")]
    [InlineData("/home/me/.claude/settings.json")]
    public void The_default_settings_path_honours_CLAUDE_CONFIG_DIR(string _)
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", @"D:\custom-claude");
            Assert.Equal(Path.Combine(@"D:\custom-claude", "settings.json"), AgentHooksSetup.SettingsPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    private static JsonNode? Read(string path) => JsonNode.Parse(File.ReadAllText(path));

    private static bool ContainsCommand(JsonNode? group, string fragment) =>
        group?["hooks"]?.AsArray().Any(h => h?["command"]?.GetValue<string>().Contains(fragment, StringComparison.Ordinal) == true) == true;

    private static int CountOurs(string path) =>
        Read(path)!["hooks"]!.AsObject()
            .SelectMany(kv => kv.Value!.AsArray())
            .SelectMany(g => g!["hooks"]!.AsArray())
            .Count(h => AgentHooksSetup.IsOurs(h?["command"]?.GetValue<string>()));

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "di-hooks-" + Guid.NewGuid().ToString("N")[..10]);
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
            }
        }
    }
}
