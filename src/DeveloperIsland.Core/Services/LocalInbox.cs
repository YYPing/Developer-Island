using System.Text.Json;
using DeveloperIsland.Core.Events;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.Services;

public sealed record InboxItem(string Id, string Source, string Title, string Summary, DateTimeOffset At, string? Link = null, bool Read = false);
public sealed record LocalReport(string Kind, string Title, string Detail, bool Failed = false);

/// <summary>Bounded local inbox. Read means acknowledged here, not read in the remote application.</summary>
public sealed class LocalInbox
{
    private readonly string? _path;
    private readonly List<InboxItem> _items;
    public LocalInbox(string? path)
    {
        _path = path;
        try { _items = path is not null && File.Exists(path) ? JsonSerializer.Deserialize<List<InboxItem>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { _items = []; }
    }
    public IReadOnlyList<InboxItem> Items => _items.ToList();
    public int UnreadCount => _items.Count(i => !i.Read);
    public bool Add(InboxItem item)
    {
        if (_items.Any(i => i.Id == item.Id)) return false;
        _items.Insert(0, item with { Summary = item.Summary[..Math.Min(item.Summary.Length, 2000)] });
        if (_items.Count > 200) _items.RemoveRange(200, _items.Count - 200);
        Save();
        return true;
    }
    public void MarkAllRead()
    {
        for (var i = 0; i < _items.Count; i++) _items[i] = _items[i] with { Read = true };
        Save();
    }
    private void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_items));
        File.Move(_path + ".tmp", _path, true);
    }
}

public static class LocalReports
{
    /// <summary>The report kinds the command line accepts, and the module each one belongs to.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["message", "pipeline", "agent"];

    public static void Write(string directory, LocalReport report)
    {
        if (!Kinds.Contains(report.Kind)) throw new ArgumentException("Unknown report kind.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report));
        File.Move(path + ".tmp", path);
    }

    /// <summary>
    /// The module a report belongs to. Kept next to <see cref="Write"/> so the command line and the
    /// reader can never disagree about what a kind means.
    /// </summary>
    public static ModuleId ModuleOf(LocalReport report) => report.Kind switch
    {
        "message" => ModuleId.Inbox,
        "agent" => ModuleId.Agents,
        _ => ModuleId.Pipeline,
    };

    /// <summary>
    /// The event a report raises. Message reports get no event here: they go through the inbox,
    /// whose own publish carries the message's identity and unread state.
    /// </summary>
    public static IslandEvent? ToEvent(LocalReport report, ModuleId module, string? dedupKey = null)
    {
        if (module == ModuleId.Inbox)
        {
            return null;
        }

        var kind = module == ModuleId.Agents
            ? (report.Failed ? IslandEventKind.AiFailed : IslandEventKind.AiFinished)
            : (report.Failed ? IslandEventKind.PipelineFailed : IslandEventKind.PipelineFinished);

        return new IslandEvent(
            Source: module == ModuleId.Agents ? AgentHookEventSource : "pipeline",
            Kind: kind,
            Priority: IslandEvent.PriorityOf(kind),
            Title: report.Title,
            Detail: report.Detail,
            Module: module,
            DedupKey: dedupKey);
    }

    /// <summary>
    /// The source id for local agent reports, so they rate-limit like the hook source instead of
    /// sharing the pipeline's slot.
    /// </summary>
    public const string AgentHookEventSource = "agent-report";
}
