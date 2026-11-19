using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Events;

/// <summary>
/// The one place events from every source meet. It owns the rules the island applies to all of them
/// — priority, deduplication, expiry, capacity — so a source only has to say what happened.
/// <para>
/// The bus is deliberately not a queue of everything ever seen: it keeps a short, bounded feed of
/// what is still relevant. <see cref="Top"/> is what the capsule shows; <see cref="Feed"/> is what
/// the "Today" list reads.
/// </para>
/// </summary>
public sealed class IslandEventBus : IDisposable
{
    /// <summary>
    /// How long an event may occupy the capsule when the source does not say. See
    /// <see cref="IslandEvent.DefaultCapsuleFor(IslandEventKind)"/> for the per-kind values.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(6);

    /// <summary>How many events the feed keeps before the oldest are dropped.</summary>
    public const int Capacity = 60;

    /// <summary>How long an event stays readable in the feed, independent of capsule occupancy.</summary>
    public static readonly TimeSpan FeedRetention = TimeSpan.FromHours(12);

    /// <summary>Deduplication keys are forgotten after this long, so the table cannot grow forever.</summary>
    private static readonly TimeSpan DedupMemory = TimeSpan.FromHours(1);

    /// <summary>How often expiry is checked when no event arrives to trigger a check.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly List<IslandEvent> _events = [];
    private readonly Dictionary<(string Source, string Key), DateTimeOffset> _lastSeen = [];
    private readonly Dictionary<string, EventSourceState> _sourceStates = [];
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Action<Action>? _post;
    private readonly List<IIslandEventSource> _sources = [];
    private readonly Dictionary<IIslandEventSource, Handler> _stateHandlers = [];
    private ITimer? _sweep;
    private volatile bool _disposed;

    /// <summary>
    /// A source's state callback paired with the id it belongs to, so the subscription can be removed
    /// again on dispose (an anonymous lambda could not).
    /// </summary>
    private sealed record Handler(string SourceId, Action<EventSourceState> Callback);

    /// <param name="time">Clock, injectable for tests.</param>
    /// <param name="post">
    /// Marshals to the UI thread when the bus lives in the app; null runs callbacks inline (tests).
    /// </param>
    public IslandEventBus(TimeProvider? time = null, Action<Action>? post = null)
    {
        _time = time ?? TimeProvider.System;
        _post = post;

        // Started here rather than in AddAsync: the capsule must expire on its own even when every
        // event came through Publish (a hook, a test), not from a registered source.
        _sweep = _time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    /// <summary>Raised when <see cref="Top"/> or <see cref="Feed"/> changes, on the posting thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when a source's health changes.</summary>
    public event Action<string, EventSourceState>? SourceStateChanged;

    /// <summary>The events still relevant, newest first.</summary>
    public IReadOnlyList<IslandEvent> Feed
    {
        get
        {
            lock (_gate)
            {
                PruneLocked();
                return _events.AsEnumerable().Reverse().ToList();
            }
        }
    }

    /// <summary>
    /// The one event the capsule should show: highest priority among those still allowed to occupy
    /// the capsule, then most recent. Null when nothing is worth interrupting for any more.
    /// <para>
    /// An expired event is never simply deleted: it stays in <see cref="Feed"/> so it can still be
    /// read, it just stops winning the capsule. That separation is why a failure cannot hold the
    /// island hostage while remaining visible in the list.
    /// </para>
    /// </summary>
    public IslandEvent? Top
    {
        get
        {
            lock (_gate)
            {
                return TopLocked(_time.GetUtcNow());
            }
        }
    }

    private IslandEvent? TopLocked(DateTimeOffset now) => _events
        .Where(e => e.Priority > 0 && e.IsCapsuleFresh(now))
        .OrderByDescending(e => e.Priority)
        .ThenByDescending(e => e.ReceivedAt)
        .FirstOrDefault();

    public IReadOnlyList<IIslandEventSource> Sources
    {
        get
        {
            lock (_gate)
            {
                return _sources.ToList();
            }
        }
    }

    public EventSourceState StateOf(string sourceId)
    {
        lock (_gate)
        {
            return _sourceStates.TryGetValue(sourceId, out var state) ? state : EventSourceState.Disabled;
        }
    }

    /// <summary>
    /// Adds and starts a source. Never throws on the source's behalf: a source that fails to start is
    /// logged and marked <see cref="EventSourceStatus.Error"/>, and the remaining sources keep working.
    /// </summary>
    public async Task AddAsync(IIslandEventSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _sources.Add(source);
        }

        // The source's StateChanged carries no id, so each source gets its own forwarding handler; the
        // bus needs the id to tell a broken IM integration from a healthy one.
        var forwardState = new Handler(source.Id, state => OnSourceStateChanged(source.Id, state));
        lock (_gate)
        {
            _stateHandlers[source] = forwardState;
        }

        source.Event += OnSourceEvent;
        source.StateChanged += forwardState.Callback;
        OnSourceStateChanged(source.Id, source.State);

        try
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("events", $"source '{source.Id}' failed to start: {ex.GetType().Name}");
            OnSourceStateChanged(source.Id, new EventSourceState(EventSourceStatus.Error, "Could not start"));
        }
    }

    /// <summary>
    /// Publishes an event. Enforces the source's deduplication window, stamps the receive time and
    /// raises <see cref="Changed"/>. Returns false when the event was suppressed as a duplicate.
    /// </summary>
    public bool Publish(IslandEvent pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (string.IsNullOrWhiteSpace(pending.Source) || _disposed)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (pending.DedupKey is { Length: > 0 } key)
            {
                var slot = (pending.Source, key);
                var cooldown = CooldownOf(pending.Source);
                if (cooldown > TimeSpan.Zero
                    && _lastSeen.TryGetValue(slot, out var last)
                    && now - last < cooldown)
                {
                    return false;
                }

                _lastSeen[slot] = now;
            }

            _events.Add(pending with { ReceivedAt = now });
            if (_events.Count > Capacity)
            {
                _events.RemoveRange(0, _events.Count - Capacity);
            }

            PruneLocked();

            // Remember what the capsule now shows, so the sweep can tell "went from something to
            // nothing" apart from "was already empty".
            _lastTop = TopLocked(now);
        }

        Raise();
        return true;
    }

    /// <summary>Drops everything, e.g. when the user clears the feed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _lastSeen.Clear();
        }

        Raise();
    }

    /// <summary>Removes one event by identity, e.g. when the user dismissed it.</summary>
    public bool Dismiss(IslandEvent target)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool removed;
        lock (_gate)
        {
            removed = _events.Remove(target);
        }

        if (removed)
        {
            Raise();
        }

        return removed;
    }

    private TimeSpan CooldownOf(string sourceId)
    {
        // The source's own declaration wins. Must be called under the gate; it only reads.
        var source = _sources.FirstOrDefault(s => s.Id == sourceId);
        return source?.DedupCooldown ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Drops events that no longer belong in the feed at all, and forgets old deduplication keys.
    /// Must be called under the gate.
    /// <para>
    /// Capsule expiry is deliberately NOT applied here. An event that may no longer occupy the
    /// capsule (see <see cref="IslandEvent.IsCapsuleFresh"/>) still belongs in the list; only age
    /// (<see cref="FeedRetention"/>) or capacity removes it. Conflating the two is how a status
    /// capsule turns into a place where a two-hour-old error is still flashing.
    /// </para>
    /// </summary>
    private void PruneLocked()
    {
        var now = _time.GetUtcNow();
        _events.RemoveAll(e => now - e.ReceivedAt >= FeedRetention);

        if (_lastSeen.Count == 0)
        {
            return;
        }

        List<(string, string)>? stale = null;
        foreach (var (slot, seenAt) in _lastSeen)
        {
            if (now - seenAt >= DedupMemory)
            {
                (stale ??= []).Add(slot);
            }
        }

        if (stale is not null)
        {
            foreach (var slot in stale)
            {
                _lastSeen.Remove(slot);
            }
        }
    }

    private void OnSourceEvent(IslandEvent e)
    {
        if (_disposed)
        {
            return;
        }

        Publish(e);
    }

    private void OnSourceStateChanged(string sourceId, EventSourceState state)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_sourceStates.TryGetValue(sourceId, out var existing) || existing != state;
            _sourceStates[sourceId] = state;
        }

        if (changed)
        {
            Post(() => SourceStateChanged?.Invoke(sourceId, state));
        }
    }

    /// <summary>
    /// What the capsule showed at the last sweep. <see cref="IslandEvent"/> is a record, so this is a
    /// value comparison: a different event of the same kind and text at a different time counts as a
    /// change, and an unchanged top does not re-raise.
    /// </summary>
    private IslandEvent? _lastTop;

    private void Sweep()
    {
        if (_disposed)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            var before = _events.Count;
            PruneLocked();
            var top = TopLocked(_time.GetUtcNow());
            changed = before != _events.Count || top != _lastTop;
            _lastTop = top;
        }

        if (changed)
        {
            Raise();
        }
    }

    private void Raise() => Post(() => Changed?.Invoke());

    private void Post(Action action)
    {
        if (_post is null)
        {
            action();
        }
        else
        {
            _post(action);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<IIslandEventSource> sources;
        lock (_gate)
        {
            _sweep?.Dispose();
            _sweep = null;
            sources = _sources.ToList();
            _sources.Clear();
        }

        foreach (var source in sources)
        {
            source.Event -= OnSourceEvent;
            if (_stateHandlers.Remove(source, out var handler))
            {
                source.StateChanged -= handler.Callback;
            }

            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("events", $"source '{source.Id}' threw while disposing: {ex.GetType().Name}");
            }
        }
    }
}
