using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.Events;

/// <summary>
/// What an event means to the user, independent of who sent it. The island never branches on the
/// source, only on this: a Claude Code hook, a Telegram message and a failed CI run all reach the
/// capsule the same way.
/// </summary>
public enum IslandEventKind
{
    /// <summary>Nothing to act on; the island may mention it once.</summary>
    Info,

    /// <summary>An agent is working. Never interrupts; only shown in the feed.</summary>
    AiRunning,

    /// <summary>An agent needs a person (permission prompt, a question). Interrupts.</summary>
    AiWaitingForInput,

    /// <summary>An agent finished a reply. Worth a glance, not an alarm.</summary>
    AiFinished,

    /// <summary>An agent or a tool failed. Interrupts.</summary>
    AiFailed,

    /// <summary>Someone wrote to you. Interrupts when the sender counts as important.</summary>
    MessageReceived,

    /// <summary>A build, test run or deployment failed. Interrupts.</summary>
    PipelineFailed,

    /// <summary>A build, test run or deployment succeeded. Quiet.</summary>
    PipelineFinished,

    /// <summary>A reminder you asked for (a task is due, a timer ended).</summary>
    Reminder,
}

/// <summary>
/// One thing worth showing, from any source. Immutable: a source publishes a new event rather than
/// mutating one, so the bus can deduplicate and expire without races.
/// <para>
/// An event has two lifetimes, because they are genuinely different things: <see cref="CapsuleFor"/>
/// is how long it may occupy the capsule, and the feed keeps it until capacity pushes it out. A
/// failure should stop hijacking the capsule within seconds but stay readable all day; a message
/// should flash once and remain in the list.
/// </para>
/// </summary>
/// <param name="Source">
/// Stable id of the producer ("claude", "telegram", "pipeline"), used for rate limiting and for the
/// settings switch that turns a source off. Never shown to the user on its own.
/// </param>
/// <param name="Priority">
/// Higher wins the capsule. Ranges in use: 0 (info), 10 (agent running, pipeline finished),
/// 20 (message), 30 (agent finished, reminder), 40 (agent waiting), 50 (failure).
/// </param>
/// <param name="CapsuleFor">
/// How long the event may occupy the capsule. Null uses the default for its kind, which is chosen so
/// nothing can hold the island hostage; the event stays in the feed either way.
/// </param>
/// <param name="DedupKey">
/// Two events with the same source and key inside the cooldown collapse to the first one. Null means
/// every event is distinct.
/// </param>
/// <param name="ActionUri">Where a click should lead, when the source can express it.</param>
public sealed record IslandEvent(
    string Source,
    IslandEventKind Kind,
    int Priority,
    string Title,
    string? Detail = null,
    ModuleId? Module = null,
    TimeSpan? CapsuleFor = null,
    string? DedupKey = null,
    string? ActionUri = null)
{
    /// <summary>When the bus accepted the event (set by the bus, not the source).</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    public static int PriorityOf(IslandEventKind kind) => kind switch
    {
        IslandEventKind.AiFailed or IslandEventKind.PipelineFailed => 50,
        IslandEventKind.AiWaitingForInput => 40,
        IslandEventKind.AiFinished or IslandEventKind.Reminder => 30,
        IslandEventKind.MessageReceived => 20,
        IslandEventKind.AiRunning or IslandEventKind.PipelineFinished => 10,
        _ => 0,
    };

    /// <summary>
    /// Default capsule occupancy per kind. A failure gets longer than a routine finish, and nothing
    /// gets more than a few seconds: the capsule is a glance, not a notification center.
    /// </summary>
    public static TimeSpan DefaultCapsuleFor(IslandEventKind kind) => kind switch
    {
        IslandEventKind.AiFailed or IslandEventKind.PipelineFailed => TimeSpan.FromSeconds(8),
        IslandEventKind.AiWaitingForInput => TimeSpan.FromSeconds(8),
        IslandEventKind.MessageReceived => TimeSpan.FromSeconds(6),
        IslandEventKind.AiFinished or IslandEventKind.Reminder => TimeSpan.FromSeconds(5),
        IslandEventKind.AiRunning or IslandEventKind.PipelineFinished => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(4),
    };

    public TimeSpan EffectiveCapsuleFor => CapsuleFor ?? DefaultCapsuleFor(Kind);

    /// <summary>Whether the event may still occupy the capsule at <paramref name="now"/>.</summary>
    public bool IsCapsuleFresh(DateTimeOffset now) => now - ReceivedAt < EffectiveCapsuleFor;
}
