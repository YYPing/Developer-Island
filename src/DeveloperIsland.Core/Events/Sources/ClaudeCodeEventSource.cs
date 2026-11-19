using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Events.Sources;

/// <summary>
/// Turns Claude Code's hook reports into island events. It owns no transport of its own: the hook
/// command writes into an <see cref="AgentHookInbox"/> and this source drains it on a timer, which
/// keeps the hook off the island's message loop and makes the feature testable without Claude Code.
/// <para>
/// The source never reads prompts or code. It sees a kind, a session id and a project folder name.
/// </para>
/// </summary>
public sealed class ClaudeCodeEventSource : IIslandEventSource
{
    public const string SourceId = "claude";

    /// <summary>
    /// How often the inbox is drained. Claude Code hooks fire at human pace, so a second of latency is
    /// invisible, while a shorter interval would wake the process for nothing.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly AgentHookInbox _inbox;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private int _draining;
    private bool _disposed;

    public ClaudeCodeEventSource(AgentHookInbox inbox, TimeProvider? time = null)
    {
        _inbox = inbox;
        _time = time ?? TimeProvider.System;
    }

    public bool Enabled { get; set; } = true;

    public string Id => SourceId;

    /// <summary>
    /// One report per session and kind is enough: Claude Code raises the same notification repeatedly
    /// while it waits, and the island must not flash for each one.
    /// </summary>
    public TimeSpan DedupCooldown => TimeSpan.FromMinutes(2);

    public EventSourceState State { get; private set; } = EventSourceState.Loading;

    public event Action<IslandEvent>? Event;

    public event Action<EventSourceState>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(EventSourceState.Ready);

        // Anything already in the folder belongs to a previous run: report it late and it reads as a
        // lie ("Claude Code is waiting" for a session that ended an hour ago).
        _inbox.ClearStale();

        _timer = _time.CreateTimer(_ => Drain(), null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One drain pass. Internal so tests can drive it without waiting for the timer.
    /// </summary>
    internal void Drain()
    {
        if (_disposed || !Enabled || Interlocked.Exchange(ref _draining, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var report in _inbox.Drain(_time.GetUtcNow()))
            {
                try
                {
                    Event?.Invoke(report.ToEvent());
                }
                catch (Exception ex)
                {
                    Log.Warn("events", $"a hook report could not be raised: {ex.GetType().Name}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }
    }

    private void SetState(EventSourceState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
