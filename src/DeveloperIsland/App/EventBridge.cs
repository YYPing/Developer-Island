using DeveloperIsland.Core.Events.Sources;
using DeveloperIsland.Core.Services;

namespace DeveloperIsland;

/// <summary>
/// The command-line entry points that feed the running island without opening a window:
/// <c>--claude-hook</c> (Claude Code's hooks) and <c>--report</c> (any local command).
/// <para>
/// Both write into a folder the island drains, and that folder is per instance kind: a demo
/// instance must not consume (and delete) reports the real island was about to read.
/// </para>
/// </summary>
internal static class EventBridge
{
    /// <summary>Where a running island of this kind picks up hook reports.</summary>
    public static string HookDirectory => HookDirectoryFor(false);

    /// <summary>
    /// Where reports land. <paramref name="demo"/> follows the <c>--demo</c> flag, so a demo run and
    /// the real island keep their own queues.
    /// </summary>
    public static string ReportDirectory => ReportDirectoryFor(false);

    public static string HookDirectoryFor(bool demo) => Path.Combine(AppPaths.Root, demo ? "agent-hooks.demo" : "agent-hooks");

    public static string ReportDirectoryFor(bool demo) => Path.Combine(AppPaths.Root, demo ? "reports.demo" : "reports");

    public static int? Run(string[] args)
    {
        var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        if (args.Contains("--claude-hook"))
        {
            try
            {
                var input = HookInput.Read(Console.In, TimeSpan.FromSeconds(2));
                var report = AgentHookParser.Parse(input, DateTimeOffset.UtcNow);
                if (report is not null)
                {
                    new AgentHookInbox(HookDirectoryFor(false)).Write(report);
                }
            }
            catch
            {
                // A reporting hook must never block or fail the agent.
            }

            return 0;
        }

        var parsed = CommandLineReports.ParseCommandLine(args, out var invalid);
        if (parsed is not { } values)
        {
            return invalid ? 2 : null;
        }

        // An unknown kind is refused loudly rather than written as a report nobody reads.
        if (!LocalReports.Kinds.Contains(values.Kind))
        {
            Console.Error.WriteLine(CommandLineReports.UsageText());
            return 2;
        }

        try
        {
            LocalReports.Write(
                ReportDirectoryFor(demo),
                new LocalReport(values.Kind, values.Title, values.Detail, values.Failed));
            return 0;
        }
        catch
        {
            return 2;
        }
    }
}
