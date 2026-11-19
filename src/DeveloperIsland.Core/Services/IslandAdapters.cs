using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;

namespace DeveloperIsland.Core.Services;

public sealed record ChatTurn(string Role, string Content);
/// <summary>
/// One weather reading. <paramref name="Condition"/> is the sky as a small closed set, so the panel
/// can pick an animation without knowing WMO codes; <paramref name="IsDay"/> keeps a clear night from
/// showing a sun.
/// </summary>
public sealed record WeatherReading(
    string Location,
    double Temperature,
    double Wind,
    string ObservedAt,
    WeatherCondition Condition = WeatherCondition.Unknown,
    bool IsDay = true);

/// <summary>What the sky is doing, reduced from the WMO code to what the panel animates.</summary>
public enum WeatherCondition
{
    /// <summary>The source sent no code, or one this app does not map.</summary>
    Unknown,
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunder,
}
/// <summary>
/// One quote. <paramref name="Name"/> is the Chinese/English display name the source returns,
/// <paramref name="Change"/> and <paramref name="ChangePercent"/> are the day's move.
/// </summary>
public sealed record MarketReading(
    string Symbol,
    string Name,
    string Price,
    string Change,
    string ChangePercent,
    string Date,
    string Time,
    double Low = 0,
    double High = 0)
{
    /// <summary>Whether the source reported a usable day range, which the range bar needs.</summary>
    public bool HasRange => High > Low && Low > 0;
}
public sealed record BotMessage(long UpdateId, long ChatId, int MessageId, string Title, string Text, DateTimeOffset At, string? Link);

/// <summary>HTTP adapters with an injectable transport. No credentials are kept in settings or URLs outside Telegram's required API path.</summary>
public sealed class IslandAdapters(HttpClient http)
{
    public async Task<string> ChatAsync(string endpoint, string model, string? key, IReadOnlyList<ChatTurn> turns, CancellationToken ct)
    {
        var uri = new Uri(endpoint.TrimEnd('/') + "/chat/completions");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("Use HTTPS, or HTTP on localhost.");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Set a model first.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { model, messages = turns.Select(t => new { role = t.Role, content = t.Content }), stream = false });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("The model returned no text.");
    }

    /// <summary>
    /// Weather for a Chinese city name, from the China Meteorological Administration's public service
    /// (nmc.cn). A domestic source is used on purpose: it answers in Chinese, takes a Chinese city
    /// name directly, needs no key, and reports the conditions a Chinese user is actually shown.
    /// <para>
    /// Two requests: the province's city list, to turn "杭州" into the station code the API wants,
    /// then that station's observation. The lookup is cached per run, so refreshing the panel does not
    /// re-download the city list every time.
    /// </para>
    /// </summary>
    public async Task<WeatherReading> WeatherAsync(string location, CancellationToken ct)
    {
        var wanted = (location ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            throw new InvalidOperationException("先在设置里填城市，例如：杭州。");
        }

        var station = await FindStationAsync(wanted, ct)
            ?? throw new InvalidOperationException($"没找到「{wanted}」。请填市级名称，例如：杭州、上海、北京。");

        using var data = await GetJsonAsync("https://www.nmc.cn/rest/weather?stationid=" + Uri.EscapeDataString(station.Code), ct);

        if (!data.RootElement.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("real", out var real) || real.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{station.City} 暂时没有返回实时数据。稍后刷新重试。");
        }

        var weather = real.GetProperty("weather");
        var wind = real.GetProperty("wind");
        var isDay = IsDay(real);

        return new WeatherReading(
            station.Province.Length > 0 ? $"{station.City} · {station.Province}" : station.City,
            Number(weather, "temperature"),
            Number(wind, "speed"),
            Text(real, "publish_time") + "（北京时间）",
            ConditionFrom(real, payload, isDay),
            isDay);
    }

    /// <summary>
    /// The sky right now. The observation block often leaves its condition blank (the service writes
    /// "-" and the placeholder code 9999), while the forecast block for the current half-day always
    /// carries a word. So: the observation if it says anything, else the forecast for "this half of
    /// the day", else measurable rain, else unknown. Nothing here invents a condition.
    /// </summary>
    private static WeatherCondition ConditionFrom(JsonElement real, JsonElement payload, bool isDay)
    {
        var weather = real.GetProperty("weather");
        var observed = ConditionOf(Text(weather, "info"), Text(weather, "img"));
        if (observed != WeatherCondition.Unknown)
        {
            return observed;
        }

        if (payload.TryGetProperty("predict", out var predict) && predict.ValueKind == JsonValueKind.Object
            && predict.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Array
            && detail.GetArrayLength() > 0)
        {
            // detail[0] is today, and its day/night halves are what "now" belongs to.
            var today = detail[0];
            var half = today.TryGetProperty(isDay ? "day" : "night", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;

            if (half.ValueKind == JsonValueKind.Object && half.TryGetProperty("weather", out var forecast) && forecast.ValueKind == JsonValueKind.Object)
            {
                var word = ConditionOf(Text(forecast, "info"), Text(forecast, "img"));
                if (word != WeatherCondition.Unknown)
                {
                    return word;
                }
            }
        }

        // Last resort: a rain gauge reading of zero or more is real data, and rain with no other
        // information is at least not a clear sky.
        var rain = Number(weather, "rain");
        return rain > 0 ? WeatherCondition.Rain : WeatherCondition.Unknown;
    }

    private static double Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private readonly Dictionary<string, CmaStation> _stations = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds the station whose city name matches, searching every province's list once per run. The
    /// lists are small and the result is cached, so the cost is paid on the first refresh only.
    /// </summary>
    private async Task<CmaStation?> FindStationAsync(string city, CancellationToken ct)
    {
        lock (_stations)
        {
            if (_stations.TryGetValue(city, out var cached))
            {
                return cached;
            }
        }

        using var provinces = await GetJsonAsync("https://www.nmc.cn/rest/province", ct);
        if (provinces.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var province in provinces.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var code = Text(province, "code");
            if (code.Length == 0)
            {
                continue;
            }

            var name = Text(province, "name");
            using var cities = await GetJsonAsync($"https://www.nmc.cn/rest/province/{Uri.EscapeDataString(code)}", ct);
            if (cities.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in cities.RootElement.EnumerateArray())
            {
                var cityName = Text(entry, "city");
                var stationCode = Text(entry, "code");
                if (cityName.Length == 0 || stationCode.Length == 0)
                {
                    continue;
                }

                // "杭州" and "杭州市" are the same place; the list spells some cities with the suffix.
                var trimmed = cityName.TrimEnd('市', '县', '区');
                var station = new CmaStation(stationCode, cityName, name);

                lock (_stations)
                {
                    _stations[cityName] = station;
                    _stations[trimmed] = station;
                }

                if (trimmed == city.TrimEnd('市', '县', '区'))
                {
                    return station;
                }
            }
        }

        return null;
    }

    private sealed record CmaStation(string Code, string City, string Province);

    /// <summary>
    /// Maps the CMA's Chinese sky words to the small set the panel animates. The words are what the
    /// service actually sends; the numeric <c>img</c> code is a fallback for entries whose text is a
    /// placeholder ("9999" means "not reported").
    /// </summary>
    public static WeatherCondition ConditionOf(string? info, string? imageCode)
    {
        var text = (info ?? string.Empty).Trim();

        if (text.Contains("雷", StringComparison.Ordinal)) return WeatherCondition.Thunder;
        if (text.Contains("雪", StringComparison.Ordinal)) return WeatherCondition.Snow;
        if (text.Contains("雨", StringComparison.Ordinal)) return WeatherCondition.Rain;
        if (text.Contains("雾", StringComparison.Ordinal) || text.Contains("霾", StringComparison.Ordinal)) return WeatherCondition.Fog;
        if (text.Contains("阴", StringComparison.Ordinal)) return WeatherCondition.Cloudy;
        if (text.Contains("云", StringComparison.Ordinal)) return WeatherCondition.PartlyCloudy;
        if (text.Contains("晴", StringComparison.Ordinal)) return WeatherCondition.Clear;

        return FromCmaImageCode(imageCode);
    }

    /// <summary>
    /// The CMA picture code: 0 clear, 1-2 cloudy, 3 overcast, 4-8 rain, 9-13 snow, 14-19 mixed,
    /// 29-32 dust, 49 fog, 53-57 thunder. Only the ranges the island draws are mapped.
    /// </summary>
    private static WeatherCondition FromCmaImageCode(string? code)
    {
        if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return WeatherCondition.Unknown;
        }

        return value switch
        {
            0 => WeatherCondition.Clear,
            1 or 2 => WeatherCondition.PartlyCloudy,
            3 => WeatherCondition.Cloudy,
            >= 4 and <= 8 => WeatherCondition.Rain,
            >= 9 and <= 13 => WeatherCondition.Snow,
            49 => WeatherCondition.Fog,
            >= 53 and <= 57 => WeatherCondition.Thunder,
            _ => WeatherCondition.Unknown,
        };
    }

    /// <summary>
    /// Day or night from the station's own sunrise and sunset, not the clock: the panel only uses this
    /// to decide between a sun and a moon, and local sunrise time is what makes that correct.
    /// </summary>
    private static bool IsDay(JsonElement real)
    {
        if (!real.TryGetProperty("sunriseSunset", out var sun) || sun.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var publish = Text(real, "publish_time");
        var rise = Text(sun, "sunrise");
        var set = Text(sun, "sunset");

        if (!DateTime.TryParse(publish, CultureInfo.InvariantCulture, DateTimeStyles.None, out var now)
            || !DateTime.TryParse(rise, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dawn)
            || !DateTime.TryParse(set, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dusk))
        {
            return true;
        }

        return now >= dawn && now <= dusk;
    }

    /// <summary>
    /// Quotes for the configured symbols. Tencent's public endpoint is used because it answers for
    /// both mainland listings (sh600519, sz000001) and US ones (usAAPL), and Stooq's CSV endpoint
    /// that this used to call now returns "page does not exist".
    /// </summary>
    public async Task<IReadOnlyList<MarketReading>> MarketsAsync(string symbols, CancellationToken ct)
    {
        var names = symbols
            .Split([',', ' ', ';', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        if (names.Length == 0)
        {
            throw new InvalidOperationException("Add at least one symbol, for example sh600519 or usAAPL.");
        }

        if (names.Any(n => n.Length < 3 || n.Any(c => !char.IsAsciiLetterOrDigit(c))))
        {
            throw new InvalidOperationException("Symbols may only contain letters and digits, for example sh600519, sz000001, usAAPL.");
        }

        var uri = "https://qt.gtimg.cn/q=" + Uri.EscapeDataString(string.Join(",", names));

        // The reply is GBK, not UTF-8: reading it as UTF-8 turns every Chinese name into mojibake.
        using var response = await http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var body = Gbk.GetString(bytes);

        return ParseTencentMarkets(body);
    }

    /// <summary>
    /// Parses Tencent's <c>v_sh600519="1~贵州茅台~600519~1460.21~..."</c> lines. Fields are positional
    /// and documented only by their order: 1 name, 2 code, 3 last, 4 previous close, 31 change,
    /// 32 change percent, 30 timestamp. A short line is skipped rather than guessed at.
    /// </summary>
    public static IReadOnlyList<MarketReading> ParseTencentMarkets(string body)
    {
        var readings = new List<MarketReading>();

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var payload = line[(equals + 1)..].Trim();
            if (payload.Length >= 2 && payload[0] == '"' && payload[^1] == '"')
            {
                payload = payload[1..^1];
            }

            var fields = payload.Split('~');
            if (fields.Length < 32)
            {
                // A symbol that does not exist comes back as a short or empty line.
                continue;
            }

            // Verified against a live response: the endpoint's own field numbers are 1-based, so
            // field 3 (price) sits at index 2, field 31 (change) at index 30 and field 32 (change
            // percent) at index 31. Off by one here silently produces a plausible wrong number.
            var name = fields[1].Trim();
            var code = fields[2].Trim();
            var last = fields[3].Trim();
            var stamp = fields[30].Trim();
            var change = fields[31].Trim();
            var changePercent = fields[32].Trim();

            // "N/D" or an empty price means the market has no quote for it right now.
            if (name.Length == 0 || last.Length == 0 || last == "0")
            {
                continue;
            }

            var (date, time) = SplitStamp(stamp);

            // Indexes 33 and 34 are the day's high and low. They are absent or zero for an instrument
            // that has not traded, in which case the row draws no range bar.
            var high = ParseNumber(fields.Length > 33 ? fields[33] : null);
            var low = ParseNumber(fields.Length > 34 ? fields[34] : null);

            readings.Add(new MarketReading(
                code.Length > 0 ? code : name, name, last, change, changePercent, date, time, low, high));
        }

        return readings;
    }

    private static double ParseNumber(string? text) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;

    /// <summary>Tencent sends <c>20261119154325</c> for mainland listings and <c>2026-11-18 16:00:01</c> for US ones.</summary>
    private static (string Date, string Time) SplitStamp(string stamp)
    {
        if (stamp.Contains('-'))
        {
            var parts = stamp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (parts.Length > 0 ? parts[0] : stamp, parts.Length > 1 ? parts[1] : string.Empty);
        }

        if (stamp.Length >= 14 && stamp.All(char.IsAsciiDigit))
        {
            var date = $"{stamp[..4]}-{stamp[4..6]}-{stamp[6..8]}";
            var time = $"{stamp[8..10]}:{stamp[10..12]}:{stamp[12..14]}";
            return (date, time);
        }

        return (stamp, string.Empty);
    }

    public async Task<IReadOnlyList<BotMessage>> TelegramAsync(string token, long offset, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://api.telegram.org/bot{token}/getUpdates?timeout=0&offset={offset}&allowed_updates=%5B%22message%22,%22channel_post%22%5D", ct);
        if (!json.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Telegram rejected the request.");
        return ParseTelegram(json.RootElement);
    }

    public static IReadOnlyList<BotMessage> ParseTelegram(JsonElement root)
    {
        var items = new List<BotMessage>();
        foreach (var update in root.GetProperty("result").EnumerateArray())
        {
            var id = update.GetProperty("update_id").GetInt64();
            if (!update.TryGetProperty("message", out var msg) && !update.TryGetProperty("channel_post", out msg))
            {
                items.Add(new BotMessage(id, 0, 0, "", "", DateTimeOffset.UnixEpoch, null));
                continue;
            }
            var chat = msg.GetProperty("chat");
            var chatId = chat.GetProperty("id").GetInt64();
            var messageId = msg.GetProperty("message_id").GetInt32();
            string Get(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
            var user = Get(chat, "username");
            var title = Get(chat, "title");
            if (title.Length == 0) title = Get(chat, "first_name");
            var text = Get(msg, "text");
            if (text.Length == 0) text = Get(msg, "caption");
            items.Add(new BotMessage(id, chatId, messageId, title, text.Length == 0 ? "[Media message]" : text,
                DateTimeOffset.FromUnixTimeSeconds(msg.GetProperty("date").GetInt64()),
                user.Length > 0 && Get(chat, "type") is "channel" or "supergroup" ? $"https://t.me/{user}/{messageId}" : null));
        }
        return items;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// GBK decoding for the quote endpoint, which answers in GBK rather than UTF-8. .NET Core ships only
/// a handful of code pages, so the provider has to be registered before the encoding is requested.
/// </summary>
internal static class Gbk
{
    private static readonly System.Text.Encoding Encoding = Create();

    private static System.Text.Encoding Create()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(936);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Without the code page, latin-1 at least preserves the bytes instead of throwing.
            return System.Text.Encoding.Latin1;
        }
    }

    public static string GetString(byte[] bytes) => Encoding.GetString(bytes);
}
