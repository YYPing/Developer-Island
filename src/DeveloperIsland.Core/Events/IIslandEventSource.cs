namespace DeveloperIsland.Core.Events;

/// <summary>
/// Anything that can put events on the bus: a Claude Code hook, an IM poller, a CI watcher, a
/// reminder timer. Implementations own their own transport and polling; the bus owns priority,
/// deduplication and expiry, so a new source never reimplements the island's behavior.
/// </summary>
public interface IIslandEventSource : IDisposable
{
    /// <summary>Stable id, also the settings key that turns this source off.</summary>
    string Id { get; }

    /// <summary>
    /// Minimum time between two events with the same <see cref="IslandEvent.DedupKey"/>. Zero
    /// means every event is distinct.
    /// </summary>
    TimeSpan DedupCooldown => TimeSpan.FromMinutes(1);

    /// <summary>
    /// Starts producing. The source raises <see cref="Event"/> from whatever thread it likes; the
    /// bus marshals. Must not throw for a missing installation or a missing credential: report that
    /// through <see cref="StateChanged"/> instead, so one broken source never hurts the rest.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised for every event this source produces.</summary>
    event Action<IslandEvent>? Event;

    /// <summary>Raised when the source's own health changes (connected, missing credential, error).</summary>
    event Action<EventSourceState>? StateChanged;

    /// <summary>Current health, for the settings page and the module's honest empty state.</summary>
    EventSourceState State { get; }
}

/// <summary>The same four honest states the modules use, for a source rather than a panel.</summary>
public enum EventSourceStatus
{
    /// <summary>Turned off in Settings. Nothing runs.</summary>
    Disabled,

    /// <summary>Starting; no verdict yet.</summary>
    Loading,

    /// <summary>Working and connected.</summary>
    Ready,

    /// <summary>A prerequisite is missing (no credential, no bot configured).</summary>
    Unavailable,

    /// <summary>Reading failed; the source will retry on its own.</summary>
    Error,
}

/// <summary>A source's health plus a short, user-facing explanation for the non-ready states.</summary>
public sealed record EventSourceState(EventSourceStatus Status, string? Message = null)
{
    public static readonly EventSourceState Disabled = new(EventSourceStatus.Disabled);
    public static readonly EventSourceState Loading = new(EventSourceStatus.Loading);
    public static readonly EventSourceState Ready = new(EventSourceStatus.Ready);

    public bool IsReady => Status == EventSourceStatus.Ready;
}
