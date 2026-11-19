using System.Net;
using System.Text.Json;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Services;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task Chat_sends_selected_model_conversation_and_bearer_to_configured_endpoint()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("POST", request.Method.Method);
            Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("my-model", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(3, json.RootElement.GetProperty("messages").GetArrayLength());
            Assert.Equal("Follow up", json.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
            return Json("""{"choices":[{"message":{"content":"Answer"}}]}""");
        }));
        var answer = await new IslandAdapters(http).ChatAsync("https://example.test/v1/", "my-model", "secret",
            [new("user", "Hi"), new("assistant", "Hello"), new("user", "Follow up")], TestContext.Current.CancellationToken);
        Assert.Equal("Answer", answer);
    }

    [Fact]
    public async Task Local_chat_works_without_key_and_remote_plaintext_is_rejected_before_sending()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json("""{"choices":[{"message":{"content":"Local"}}]}"""));
        }));
        var adapter = new IslandAdapters(http);
        Assert.Equal("Local", await adapter.ChatAsync("http://localhost:11434/v1", "local", null, [new("user", "Hi")], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ChatAsync("http://example.com/v1", "x", "secret", [], TestContext.Current.CancellationToken));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Weather_resolves_a_chinese_city_to_its_station_and_keeps_the_observation_time()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;

            // 1. The province list, 2. that province's cities, 3. the station's observation.
            if (uri.EndsWith("/rest/province", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""[{"code":"AZJ","name":"浙江省"}]"""));
            }

            if (uri.Contains("/rest/province/AZJ", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""[{"code":"HIieJ","province":"浙江省","city":"杭州"}]"""));
            }

            Assert.Contains("stationid=HIieJ", uri, StringComparison.Ordinal);
            return Task.FromResult(Json("""
            {"data":{
              "real":{
                "publish_time":"2026-11-19 17:40",
                "weather":{"temperature":10.4,"humidity":53,"rain":0,"info":"-","img":"9999"},
                "wind":{"direct":"西北风","speed":2.3},
                "sunriseSunset":{"sunrise":"2026-11-19 06:27","sunset":"2026-11-19 17:01"}
              },
              "predict":{"detail":[{
                "date":"2026-11-19",
                "day":{"weather":{"info":"9999","img":"9999"}},
                "night":{"weather":{"info":"晴","img":"0"}}
              }]}
            }}
            """));
        }));

        var result = await new IslandAdapters(http).WeatherAsync("杭州", TestContext.Current.CancellationToken);

        Assert.Equal("杭州 · 浙江省", result.Location);
        Assert.Equal(10.4, result.Temperature);
        Assert.Equal(2.3, result.Wind);
        Assert.Equal("2026-11-19 17:40（北京时间）", result.ObservedAt);

        // The observation block leaves its condition blank, so the answer comes from the forecast half
        // that "now" belongs to: 17:40 is after sunset, so the night half ("晴") applies.
        Assert.Equal(Core.Services.WeatherCondition.Clear, result.Condition);
        Assert.False(result.IsDay, "17:40 is after the 17:01 sunset at this station");
    }

    [Fact]
    public async Task An_unknown_city_says_so_instead_of_asking_the_wrong_station()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            return uri.EndsWith("/rest/province", StringComparison.Ordinal)
                ? Task.FromResult(Json("""[{"code":"AZJ","name":"浙江省"}]"""))
                : Task.FromResult(Json("""[{"code":"HIieJ","province":"浙江省","city":"杭州"}]"""));
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IslandAdapters(http).WeatherAsync("不存在的城市", TestContext.Current.CancellationToken));

        Assert.Contains("不存在的城市", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_keep_the_source_timestamp_and_skip_symbols_with_no_quote()
    {
        // A real quote plus a symbol the exchange does not know. The unknown one must not appear as a
        // zero price invented by the parser.
        var fields = new string[40];
        fields[0] = "1";
        fields[1] = "贵州茅台";
        fields[2] = "600519";
        fields[3] = "1460.21";
        fields[30] = "20261119154325";
        fields[31] = "17.55";
        fields[32] = "1.22";

        var body = "v_sh600519=\"" + string.Join("~", fields) + "\";\nv_sh999999=\"1~\";";

        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(body));
        Assert.Equal("1460.21", quote.Price);
        Assert.Equal("2026-11-19", quote.Date);
        Assert.Equal("15:43:25", quote.Time);
    }

    [Fact]
    public async Task Telegram_uses_offset_and_does_not_invent_private_chat_links()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Contains("/bottest/getUpdates?timeout=0&offset=123", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"ok":true,"result":[{"update_id":123,"message":{"message_id":2,"chat":{"id":42,"type":"private","username":"sam","first_name":"Sam"},"date":1700000000,"text":"Hello"}},{"update_id":124,"channel_post":{"message_id":3,"chat":{"id":-1001,"type":"channel","title":"News","username":"publicnews"},"date":1700000001,"caption":"Image"}}]}"""));
        }));
        var items = await new IslandAdapters(http).TelegramAsync("test", 123, TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].Link);
        Assert.Equal("Hello", items[0].Text);
        Assert.Equal("https://t.me/publicnews/3", items[1].Link);
        Assert.Equal(124, items[1].UpdateId);
    }

    [Fact]
    public void Local_unread_persists_acknowledgement_deduplicates_and_bounds_history()
    {
        var directory = Path.Combine(Path.GetTempPath(), "island-inbox-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(directory, "inbox.json");
            var inbox = new LocalInbox(path);
            var item = new InboxItem("1", "Local", "Title", "Text", DateTimeOffset.UtcNow);
            Assert.True(inbox.Add(item));
            Assert.False(inbox.Add(item));
            Assert.Equal(1, new LocalInbox(path).UnreadCount);
            inbox.MarkAllRead();
            Assert.Equal(0, new LocalInbox(path).UnreadCount);
            for (var i = 2; i <= 205; i++) inbox.Add(item with { Id = i.ToString() });
            Assert.Equal(200, new LocalInbox(path).Items.Count);
            Assert.Equal("205", new LocalInbox(path).Items[0].Id);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Four_groups_cover_every_old_and_new_module_and_switches_are_independent()
    {
        Assert.Equal(4, Enum.GetValues<NavigationGroup>().Length);
        Assert.Equal(NavigationGroup.Now, ModuleNavigation.GroupOf(ModuleId.Music));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.System));
        Assert.Equal(NavigationGroup.Work, ModuleNavigation.GroupOf(ModuleId.Awake));
        Assert.Equal(new[] { ModuleId.Tasks, ModuleId.Weather, ModuleId.Markets }, Enum.GetValues<ModuleId>().Where(m => ModuleNavigation.GroupOf(m) == NavigationGroup.Life));
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            var settings = new AppSettings();
            settings.SetEnabled(module, false);
            Assert.False(settings.IsEnabled(module));
            Assert.All(Enum.GetValues<ModuleId>().Where(m => m != module), m => Assert.True(settings.IsEnabled(m)));
            settings.SetEnabled(module, true);
            Assert.True(settings.IsEnabled(module));
        }
    }

    [Fact]
    public void Report_entry_writes_complete_json_without_temporary_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "island-report-" + Guid.NewGuid());
        try
        {
            LocalReports.Write(directory, new LocalReport("pipeline", "Build", "exit 1", true));
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.EndsWith(".json", file);
            var report = JsonSerializer.Deserialize<LocalReport>(File.ReadAllText(file))!;
            Assert.True(report.Failed);
            Assert.Equal("exit 1", report.Detail);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
