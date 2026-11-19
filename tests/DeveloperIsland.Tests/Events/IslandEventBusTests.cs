using DeveloperIsland.Core.Events;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests.Events;

public class IslandEventBusTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

    private IslandEventBus NewBus() => new(_time);

    private static IslandEvent Event(
        IslandEventKind kind = IslandEventKind.Info,
        string source = "test",
        string title = "Something happened",
        TimeSpan? capsuleFor = null,
        string? dedupKey = null) =>
        new(source, kind, IslandEvent.PriorityOf(kind), title, CapsuleFor: capsuleFor, DedupKey: dedupKey);

    [Fact]
    public void Top_picks_the_highest_priority_event()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.AiRunning, title: "agent running"));
        bus.Publish(Event(IslandEventKind.PipelineFailed, title: "CI failed"));
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "new message"));

        Assert.Equal("CI failed", bus.Top?.Title);
    }

    [Fact]
    public void Top_prefers_the_most_recent_when_priorities_tie()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "first"));
        _time.Advance(TimeSpan.FromSeconds(1));
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "second"));

        Assert.Equal("second", bus.Top?.Title);
    }

    [Fact]
    public void An_expired_event_leaves_the_capsule_but_stays_in_the_feed()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.PipelineFailed, title: "CI failed"));

        // Well past the 8 s a failure may occupy the capsule.
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(bus.Top);
        Assert.Contains(bus.Feed, e => e.Title == "CI failed");
    }

    [Fact]
    public void A_lower_priority_event_takes_the_capsule_once_the_top_one_expires()
    {
        using var bus = NewBus();

        // A message (20) outranks a running agent (10) while its own window lasts.
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "new message", capsuleFor: TimeSpan.FromSeconds(5)));
        bus.Publish(Event(IslandEventKind.AiRunning, title: "agent running", capsuleFor: TimeSpan.FromSeconds(20)));

        Assert.Equal("new message", bus.Top?.Title);

        _time.Advance(TimeSpan.FromSeconds(10));

        // The message's capsule window is over; the agent is still within its longer one, so it takes
        // over instead of the capsule going empty.
        Assert.Equal("agent running", bus.Top?.Title);
    }

    [Fact]
    public void Zero_priority_events_never_take_the_capsule_but_stay_readable()
    {
        using var bus = NewBus();
        bus.Publish(new IslandEvent("test", IslandEventKind.Info, 0, "just so you know"));

        Assert.Null(bus.Top);
        Assert.Single(bus.Feed);
    }

    [Fact]
    public void Deduplication_collapses_repeats_of_the_same_key()
    {
        using var bus = NewBus();
        Publish(bus, "claude", IslandEventKind.AiWaitingForInput, "waiting", "session-1");

        var second = Publish(bus, "claude", IslandEventKind.AiWaitingForInput, "waiting", "session-1");

        Assert.False(second);
        Assert.Single(bus.Feed);
    }

    [Fact]
    public void Deduplication_lapses_after_the_cooldown()
    {
        using var bus = NewBus();
        Publish(bus, "claude", IslandEventKind.AiWaitingForInput, "waiting", "session-1");

        _time.Advance(TimeSpan.FromMinutes(2));

        var second = Publish(bus, "claude", IslandEventKind.AiWaitingForInput, "waiting", "session-1");

        Assert.True(second);
        Assert.Equal(2, bus.Feed.Count);
    }

    [Fact]
    public void Different_dedup_keys_are_both_kept()
    {
        using var bus = NewBus();
        Publish(bus, "telegram", IslandEventKind.MessageReceived, "Alice wrote", "alice");
        Publish(bus, "telegram", IslandEventKind.MessageReceived, "Bob wrote", "bob");

        Assert.Equal(2, bus.Feed.Count);
    }

    [Fact]
    public void The_same_key_from_different_sources_is_not_a_duplicate()
    {
        using var bus = NewBus();
        Publish(bus, "telegram", IslandEventKind.MessageReceived, "from telegram", "same-key");
        Publish(bus, "discord", IslandEventKind.MessageReceived, "from discord", "same-key");

        Assert.Equal(2, bus.Feed.Count);
    }

    [Fact]
    public void Events_without_a_key_are_never_deduplicated()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "one"));
        bus.Publish(Event(IslandEventKind.MessageReceived, title: "two"));

        Assert.Equal(2, bus.Feed.Count);
    }

    [Fact]
    public void The_feed_is_bounded_by_capacity()
    {
        using var bus = NewBus();
        for (var i = 0; i < IslandEventBus.Capacity + 15; i++)
        {
            bus.Publish(Event(IslandEventKind.Info, title: $"event {i}"));

            // Keep them all fresh so retention does not remove them instead.
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(IslandEventBus.Capacity, bus.Feed.Count);

        // The oldest were dropped, the newest kept.
        Assert.Contains(bus.Feed, e => e.Title == $"event {IslandEventBus.Capacity + 14}");
        Assert.DoesNotContain(bus.Feed, e => e.Title == "event 0");
    }

    [Fact]
    public void The_feed_drops_events_past_retention()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.Info, title: "old news"));

        _time.Advance(IslandEventBus.FeedRetention + TimeSpan.FromMinutes(1));

        Assert.Empty(bus.Feed);
    }

    [Fact]
    public void Changed_fires_when_an_event_arrives()
    {
        using var bus = NewBus();
        var raised = 0;
        bus.Changed += () => raised++;

        bus.Publish(Event());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Changed_fires_when_the_capsule_expires_on_its_own()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.MessageReceived, capsuleFor: TimeSpan.FromSeconds(2)));

        var raisedAfterPublish = 0;
        bus.Changed += () => raisedAfterPublish++;

        // The sweep timer is the only thing that can notice; no new event arrives.
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(raisedAfterPublish > 0, "the bus must report that the capsule emptied on its own");
        Assert.Null(bus.Top);
    }

    [Fact]
    public void Clear_empties_the_feed()
    {
        using var bus = NewBus();
        bus.Publish(Event(title: "one"));
        bus.Publish(Event(title: "two"));

        bus.Clear();

        Assert.Empty(bus.Feed);
        Assert.Null(bus.Top);
    }

    [Fact]
    public void Dismiss_removes_only_the_named_event()
    {
        using var bus = NewBus();
        bus.Publish(Event(IslandEventKind.Info, title: "one"));
        bus.Publish(Event(IslandEventKind.Info, title: "two"));
        var target = bus.Feed.Single(e => e.Title == "one");

        Assert.True(bus.Dismiss(target));
        Assert.Single(bus.Feed);
        Assert.Equal("two", bus.Feed[0].Title);
    }

    [Fact]
    public void An_event_with_a_blank_source_is_rejected()
    {
        using var bus = NewBus();

        Assert.False(bus.Publish(new IslandEvent("  ", IslandEventKind.PipelineFailed, 50, "bad")));
        Assert.Empty(bus.Feed);
    }

    [Fact]
    public async Task A_source_that_fails_to_start_is_marked_and_the_bus_survives()
    {
        using var bus = NewBus();
        var broken = new FakeSource("broken") { FailOnStart = true };

        await bus.AddAsync(broken);

        Assert.Equal(EventSourceStatus.Error, bus.StateOf("broken").Status);

        // The bus still works for everyone else.
        Assert.True(bus.Publish(Event(IslandEventKind.PipelineFailed, source: "other", title: "still here")));
    }

    [Fact]
    public async Task A_started_source_reaches_the_bus_through_its_event()
    {
        using var bus = NewBus();
        var source = new FakeSource("telegram", state: EventSourceState.Ready);
        await bus.AddAsync(source);

        source.Raise(new IslandEvent("telegram", IslandEventKind.MessageReceived, 20, "Alice wrote"));

        Assert.Equal(EventSourceStatus.Ready, bus.StateOf("telegram").Status);
        Assert.Equal("Alice wrote", bus.Top?.Title);
    }

    [Fact]
    public async Task A_source_reports_its_own_unavailable_state_without_throwing()
    {
        using var bus = NewBus();
        var source = new FakeSource("feishu", state: new EventSourceState(EventSourceStatus.Unavailable, "No app credential"));

        await bus.AddAsync(source);

        Assert.Equal(EventSourceStatus.Unavailable, bus.StateOf("feishu").Status);
        Assert.Equal("No app credential", bus.StateOf("feishu").Message);
    }

    [Fact]
    public async Task Disposing_the_bus_disposes_its_sources()
    {
        var bus = NewBus();
        var source = new FakeSource("telegram");
        await bus.AddAsync(source);

        bus.Dispose();

        Assert.True(source.Disposed);
    }

    [Fact]
    public void Publishing_after_dispose_is_ignored()
    {
        var bus = NewBus();
        bus.Dispose();

        Assert.False(bus.Publish(Event(IslandEventKind.PipelineFailed, title: "too late")));
    }

    private static bool Publish(IslandEventBus bus, string source, IslandEventKind kind, string title, string key) =>
        bus.Publish(new IslandEvent(source, kind, IslandEvent.PriorityOf(kind), title, DedupKey: key));

    private sealed class FakeSource(string id, EventSourceState? state = null) : IIslandEventSource
    {
        public string Id => id;

        public TimeSpan DedupCooldown => TimeSpan.FromMinutes(1);

        public EventSourceState State { get; private set; } = state ?? EventSourceState.Loading;

        public bool FailOnStart { get; init; }

        public bool Disposed { get; private set; }

        public event Action<IslandEvent>? Event;

        public event Action<EventSourceState>? StateChanged;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            FailOnStart ? throw new InvalidOperationException("cannot start") : Task.CompletedTask;

        public void Raise(IslandEvent e) => Event?.Invoke(e);

        public void RaiseState(EventSourceState next)
        {
            State = next;
            StateChanged?.Invoke(next);
        }

        public void Dispose() => Disposed = true;
    }
}
