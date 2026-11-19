using System.Text.Json;
using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Events;
using DeveloperIsland.Core.Events.Sources;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Services;
using DeveloperIsland.Core.Settings;
using Microsoft.UI.Dispatching;

namespace DeveloperIsland;

/// <summary>Owns integration lifetime. Each operation fails independently; settings changes cancel old work.</summary>
internal sealed class IntegrationHost : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly bool _demo;
    private readonly DispatcherQueueTimer _timer;
    private readonly ClaudeCodeEventSource _claude;
    private CancellationTokenSource _work = new();
    private bool _polling;
    private DateTimeOffset _nextPoll;
    private long _telegramOffset;
    private readonly Dictionary<ModuleId, string> _text = [];
    private readonly Dictionary<ModuleId, string> _errors = [];
    private readonly List<ChatTurn> _conversation = [];
    public IslandEventBus Bus { get; }
    public LocalInbox Inbox { get; }
    public IslandAdapters Adapters { get; }
    public event Action? Changed;
    public bool IsDemo => _demo;
    private bool _disposed;
    private readonly HashSet<ModuleId> _refreshing = [];
    public AppSettings Settings => _settings.Current;
    private string _localPipeline = "";
    private string _lastRepository = "";

    /// <summary>
    /// Decides which CI outcomes are news. The first look at a repository is a baseline, so five runs
    /// that finished before the island started are not announced on startup.
    /// </summary>
    private readonly CiRunWatcher _ci = new();
    public string Text(ModuleId module) => (module == ModuleId.Pipeline && _localPipeline.Length > 0 ? "Last local report: " + _localPipeline + "\n\n" : "") + _text.GetValueOrDefault(module, module switch
    {
        ModuleId.AiChat => "Ask a question. Set your endpoint and model below.",
        ModuleId.Agents => "还没有收到 agent 报告。",
        _ => "Ready. Refresh to load.",
    });
    public string Summary(ModuleId module) => module switch
    {
        ModuleId.Inbox => $"{Inbox.UnreadCount} unread here",
        ModuleId.Feed => $"{Bus.Feed.Count} recent events",
        ModuleId.AiChat => ChatBusy ? "Thinking…" : "Ask AI",
        _ => Text(module).Split('\n')[0],
    };
    public bool HasContent(ModuleId module) => module switch
    {
        ModuleId.Inbox => Inbox.UnreadCount > 0,
        ModuleId.Feed => Bus.Feed.Count > 0,
        ModuleId.AiChat => ChatBusy || _conversation.Count > 0,
        _ => _text.ContainsKey(module),
    };
    public string ChatText => string.Join("\n\n", _conversation.Select(t => (t.Role == "user" ? "我 · " : "AI · ") + t.Content));

    /// <summary>
    /// The last weather reading, or null before the first refresh. Exposed structurally so the panel
    /// can lay it out as a card instead of parsing it back out of <see cref="Text"/>.
    /// </summary>
    public WeatherReading? Weather { get; private set; }

    /// <summary>The last quotes, exposed for the same reason as <see cref="Weather"/>.</summary>
    public IReadOnlyList<MarketReading> Markets { get; private set; } = [];

    /// <summary>When the quotes were fetched, for the panel's "as of" line.</summary>
    public DateTimeOffset MarketsFetchedAt { get; private set; }

    /// <summary>The message shown when a refresh failed, so the panel can render it without a card.</summary>
    public string? LastError(ModuleId module) => _errors.GetValueOrDefault(module);
    public bool ChatBusy { get; private set; }

    public IntegrationHost(DispatcherQueue dispatcher, SettingsStore settings, ISecretStore secrets, bool demo)
    {
        _settings = settings;
        _secrets = secrets;
        _demo = demo;
        Adapters = new IslandAdapters(_http);
        Inbox = new LocalInbox(demo ? null : Path.Combine(AppPaths.Root, "inbox.json"));
        if (!demo && File.Exists(OffsetPath)) long.TryParse(File.ReadAllText(OffsetPath), out _telegramOffset);
        Bus = new IslandEventBus(post: a => dispatcher.TryEnqueue(() => a()));
        Bus.Changed += () => Changed?.Invoke();
        _claude = new ClaudeCodeEventSource(new AgentHookInbox(EventBridge.HookDirectoryFor(demo)));
        _claude.Event += e => dispatcher.TryEnqueue(() => { _text[ModuleId.Agents] = e.Title + "\n" + e.Detail + "\n" + DateTimeOffset.Now.ToString("g"); Changed?.Invoke(); });
        if (!demo) _ = Bus.AddAsync(_claude);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        Apply();
    }

    private string OffsetPath => Path.Combine(AppPaths.Root, "telegram-offset.txt");
    public void Apply()
    {
        _work.Cancel();
        _work.Dispose();
        _work = new CancellationTokenSource();
        _claude.Enabled = Settings.AgentsEnabled && !_demo;
        if (_lastRepository != Settings.PipelineRepository || !Settings.PipelineEnabled)
        {
            // Another repository (or the module was off): its runs are history, not news.
            _ci.Reset();
            _lastRepository = Settings.PipelineRepository;
        }
        foreach (var e in Bus.Feed.Where(e => e.Module is { } m && !Settings.IsEnabled(m))) Bus.Dismiss(e);
        _nextPoll = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    public void Save(Action<AppSettings> change) => _settings.Update(change);
    public void SaveSecret(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _secrets.Delete(key); else _secrets.Save(key, value.Trim());
        if (key == "telegram-bot") { _telegramOffset = 0; if (!_demo && File.Exists(OffsetPath)) File.Delete(OffsetPath); }
        Apply();
    }
    public void ClearChat() { _conversation.Clear(); Changed?.Invoke(); }
    public async Task SendChatAsync(string prompt)
    {
        if (ChatBusy || !Settings.AiChatEnabled || string.IsNullOrWhiteSpace(prompt)) return;
        ChatBusy = true;
        var ct = _work.Token;
        var turns = _conversation.TakeLast(12).Append(new ChatTurn("user", prompt)).ToList();
        _text[ModuleId.AiChat] = "Thinking…";
        Changed?.Invoke();
        try
        {
            if (_demo) throw new InvalidOperationException("演示模式不会发送请求。");
            var answer = await Adapters.ChatAsync(Settings.ChatEndpoint, Settings.ChatModel, _secrets.Load("chat-api-key"), turns, ct);
            ct.ThrowIfCancellationRequested();
            _conversation.Add(new ChatTurn("user", prompt));
            _conversation.Add(new ChatTurn("assistant", answer));
            if (_conversation.Count > 24) _conversation.RemoveRange(0, _conversation.Count - 24);
            _text[ModuleId.AiChat] = "Conversation stays in memory. Clear to forget.";
        }
        catch (OperationCanceledException) { _text[ModuleId.AiChat] = "Cancelled."; }
        catch (Exception ex) { _text[ModuleId.AiChat] = Error(ex); }
        finally { ChatBusy = false; Changed?.Invoke(); }
    }

    public async Task RefreshAsync(ModuleId module)
    {
        if (_disposed || !Settings.IsEnabled(module) || !_refreshing.Add(module)) return;
        var ct = _work.Token;
        _text[module] = "加载中…";
        _errors.Remove(module);
        Changed?.Invoke();
        try
        {
            // Demo mode shows invented readings for the two modules whose whole point is the visual
            // (the atmosphere and the range bars); everything that would touch a network or an account
            // stays paused. Without this the demo's weather and markets panels were permanently empty,
            // which made the product look like it could not do the thing it is built to do.
            if (_demo && module is ModuleId.Weather or ModuleId.Markets)
            {
                _text[module] = module == ModuleId.Weather
                    ? LoadWeather(DemoWeather())
                    : LoadMarkets(DemoMarkets());
                return;
            }

            if (_demo) throw new InvalidOperationException("演示模式：外部集成已暂停。");

            var text = module switch
            {
                ModuleId.Weather => LoadWeather(await Adapters.WeatherAsync(Settings.WeatherLocation, ct)),
                ModuleId.Markets => LoadMarkets(await Adapters.MarketsAsync(Settings.MarketSymbols, ct)),
                ModuleId.Pipeline => await PipelineAsync(ct),
                ModuleId.Inbox => await TelegramAsync(ct),
                _ => Text(module),
            };
            ct.ThrowIfCancellationRequested();
            _text[module] = text;
        }
        catch (OperationCanceledException)
        {
            _text[module] = "已取消。刷新重试。";
            _errors[module] = _text[module];
        }
        catch (Exception ex)
        {
            _text[module] = Error(ex);
            _errors[module] = _text[module];
        }
        finally { _refreshing.Remove(module); }
        if (!_disposed) Changed?.Invoke();
    }

    /// <summary>Invented weather for demo mode, shaped like a real reading so the panel renders fully.</summary>
    private static WeatherReading DemoWeather() => new(
        "杭州 · 浙江省",
        10.4,
        2.3,
        "10:24（北京时间）",
        Core.Services.WeatherCondition.Clear,
        IsDay: true);

    /// <summary>Invented quotes for demo mode: one of each direction, each with a day range.</summary>
    private static IReadOnlyList<MarketReading> DemoMarkets() =>
    [
        new("600519", "贵州茅台", "1460.21", "17.55", "1.22", "2026-11-19", "15:00", 1432.10, 1464.99),
        new("000001", "平安银行", "12.04", "-0.16", "-1.31", "2026-11-19", "15:00", 11.95, 12.26),
        new("AAPL", "苹果", "352.45", "0.54", "0.15", "2026-11-18", "16:00", 351.47, 356.42),
    ];

    /// <summary>Stores the reading for the panel's card layout and returns the plain-text form.</summary>
    private string LoadWeather(WeatherReading reading)
    {
        Weather = reading;
        return FormatWeather(reading);
    }

    private string LoadMarkets(IReadOnlyList<MarketReading> quotes)
    {
        Markets = quotes;
        MarketsFetchedAt = DateTimeOffset.Now;
        return FormatMarkets(quotes);
    }
    private static string Error(Exception ex) => ex is InvalidOperationException
        ? ex.Message
        : $"暂时不可用（{ex.GetType().Name}）。请检查设置、凭据和网络，然后刷新重试。";
    private static string FormatWeather(WeatherReading w) => $"{w.Location}\n{w.Temperature:0.#} °C · 风速 {w.Wind:0.#} km/h\n数据时间 {w.ObservedAt}\nOpen-Meteo · 模型估算";
    private static string FormatMarkets(IReadOnlyList<MarketReading> items) => items.Count == 0 ? "没有返回行情数据。请检查股票代码。" :
        string.Join("\n", items.Select(q =>
        {
            // A rising quote carries a plus sign so the direction is readable without colour.
            var change = q.Change.Length > 0 && !q.Change.StartsWith('-') ? "+" + q.Change : q.Change;
            var percent = q.ChangePercent.Length > 0
                ? (q.ChangePercent.StartsWith('-') ? q.ChangePercent : "+" + q.ChangePercent) + "%"
                : string.Empty;
            var move = string.Join("  ", new[] { change, percent }.Where(s => s.Length > 0));
            return $"{q.Name} ({q.Symbol})\n{q.Price}  {move}\n{q.Date} {q.Time}";
        })) + "\n腾讯行情 · 可能有延迟，非实时成交价\n抓取: " + DateTimeOffset.Now.ToString("g");

    private async Task<string> TelegramAsync(CancellationToken ct)
    {
        if (!Settings.TelegramEnabled) return "本地收件箱就绪。Telegram Bot 未开启。";
        var token = _secrets.Load("telegram-bot");
        if (string.IsNullOrWhiteSpace(token)) return "填入 Telegram Bot Token 即可连接。本地收件箱始终可用。";
        var items = await Adapters.TelegramAsync(token, _telegramOffset, ct);
        ct.ThrowIfCancellationRequested();
        foreach (var m in items)
        {
            if (m.ChatId != 0) AddMessage(new InboxItem("telegram:" + m.UpdateId, "Telegram Bot", m.Title, m.Text, m.At, m.Link));
            _telegramOffset = Math.Max(_telegramOffset, m.UpdateId + 1);
        }
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(OffsetPath, _telegramOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "Telegram Bot connected · only messages visible to this bot. Read state is local to this island.";
    }

    public void AddMessage(InboxItem item)
    {
        if (!Settings.InboxEnabled || !Inbox.Add(item)) return;
        Publish(ModuleId.Inbox, IslandEventKind.MessageReceived, item.Title, item.Summary, item.Id);
        Changed?.Invoke();
    }
    public void MarkRead() { Inbox.MarkAllRead(); Changed?.Invoke(); }
    private void Publish(ModuleId module, IslandEventKind kind, string title, string detail, string? key = null) =>
        Bus.Publish(new IslandEvent(module.ToString(), kind, IslandEvent.PriorityOf(kind), title, detail, module, DedupKey: key));

    private async Task<string> PipelineAsync(CancellationToken ct)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Settings.PipelineRepository))
        {
            try
            {
                var gh = ProcessRunner.FindExecutable("gh") ?? throw new InvalidOperationException("Install gh and run gh auth login.");
                var result = await ProcessRunner.RunAsync(gh, ["run", "list", "--repo", Settings.PipelineRepository, "--limit", "5", "--json", "databaseId,displayTitle,status,conclusion,updatedAt,url"], cancellationToken: ct);
                if (result.ExitCode != 0) throw new InvalidOperationException("gh could not read runs. Check repository and gh auth login.");
                ct.ThrowIfCancellationRequested();

                var runs = CiRun.ParseList(result.Output);

                // The first poll after startup (or after pointing at another repository) only records
                // what already finished; only runs that finish while the island watches are announced.
                foreach (var run in _ci.Observe(runs))
                {
                    Publish(ModuleId.Pipeline, run.Conclusion == "failure" ? IslandEventKind.PipelineFailed : IslandEventKind.PipelineFinished,
                        run.Title, run.Conclusion ?? string.Empty, run.Key);
                }

                foreach (var run in runs)
                {
                    parts.Add(run.Describe());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { parts.Add(Error(ex)); }
        }
        else parts.Add("填 owner/repo 才能读 GitHub Actions。本地命令上报可直接使用。");
        if (Settings.DockerEnabled)
        {
            try
            {
                var docker = ProcessRunner.FindExecutable("docker") ?? throw new InvalidOperationException("Docker CLI is not installed.");
                var result = await ProcessRunner.RunAsync(docker, ["ps", "-a", "--format", "{{.Names}} · {{.Status}}"], cancellationToken: ct);
                parts.Add(result.ExitCode == 0 ? "Docker\n" + result.Output : "Docker daemon unavailable.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { parts.Add(Error(ex)); }
        }
        return string.Join("\n\n", parts) + "\nChecked: " + DateTimeOffset.Now.ToString("g");
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            // Local reports are read in every mode: the report entry point is how another tool tells
            // the island what a command did, and it costs no network. Demo mode stops here, so a
            // demonstration never polls GitHub, Telegram, Open-Meteo or Stooq.
            DrainReports();
            if (_demo || DateTimeOffset.UtcNow < _nextPoll) return;
            _nextPoll = DateTimeOffset.UtcNow.AddMinutes(1);
            await Task.WhenAll(new[] { ModuleId.Inbox, ModuleId.Pipeline, ModuleId.Weather, ModuleId.Markets }.Select(RefreshAsync));
        }
        finally { _polling = false; }
    }
    /// <summary>Where this instance's <c>--report</c> runs leave their files.</summary>
    private string ReportDirectory => EventBridge.ReportDirectoryFor(_demo);

    private void DrainReports()
    {
        // Demo runs drain their own queue: a demo island must not consume the real island's reports.
        if (!Directory.Exists(ReportDirectory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(ReportDirectory, "*.json").Take(100))
            {
                try
                {
                    var report = JsonSerializer.Deserialize<LocalReport>(File.ReadAllText(path));
                    if (report is null || !LocalReports.Kinds.Contains(report.Kind)) { File.Delete(path); continue; }
                    var module = LocalReports.ModuleOf(report);
                    if (Settings.IsEnabled(module))
                    {
                        var at = File.GetLastWriteTime(path);
                        var line = report.Title + "\n" + report.Detail + "\n" + at.ToString("g");
                        if (module == ModuleId.Inbox)
                        {
                            AddMessage(new InboxItem(Path.GetFileName(path), "上报", report.Title, report.Detail, at));
                        }
                        else
                        {
                            if (module == ModuleId.Pipeline) _localPipeline = line;
                            _text[module] = report.Title + "\n" + report.Detail + "\n" + at.ToString("g");
                            if (LocalReports.ToEvent(report, module, Path.GetFileName(path)) is { } raised)
                            {
                                Bus.Publish(raised);
                            }
                        }
                    }
                    File.Delete(path);
                }
                catch (JsonException) { File.Delete(path); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _text[ModuleId.Feed] = "读不到本地上报文件。"; }
    }
    public void Dispose()
    {
        _disposed = true;
        Changed = null;
        _timer.Stop();
        _work.Cancel();
        _work.Dispose();
        Bus.Dispose();
        _claude.Dispose();
        _http.Dispose();
    }
}
