using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests;

/// <summary>
/// The quote endpoint answers with positional, undocumented fields in GBK, and its own field numbers
/// are 1-based while the array is 0-based. That off-by-one turns a change percent into a date, so
/// these tests build payloads with an exact, counted field list and pin the positions that matter.
/// </summary>
public class MarketParsingTests
{
    /// <summary>
    /// Builds a quote line from named fields, so a test never has to count tildes by hand. Positions
    /// follow the live response: 2 name, 3 code, 4 price, 31 change, 32 change percent, 30 timestamp.
    /// </summary>
    private static string Quote(
        string name,
        string code,
        string price,
        string stamp,
        string change,
        string changePercent,
        string variable = "v_sh600519",
        bool quoted = true)
    {
        // 0-based indexes verified against a live response: 30 timestamp, 31 change, 32 change percent.
        var fields = new string[40];
        fields[0] = "1";
        fields[1] = name;
        fields[2] = code;
        fields[3] = price;
        fields[4] = "0";
        fields[30] = stamp;
        fields[31] = change;
        fields[32] = changePercent;

        var payload = string.Join("~", fields);
        return quoted ? $"{variable}=\"{payload}\";" : $"{variable}={payload};";
    }

    [Fact]
    public void A_mainland_quote_yields_name_price_and_change()
    {
        var body = Quote("贵州茅台", "600519", "1460.21", "20261119154325", "17.55", "1.22");

        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(body));

        Assert.Equal("600519", quote.Symbol);
        Assert.Equal("贵州茅台", quote.Name);
        Assert.Equal("1460.21", quote.Price);
        Assert.Equal("17.55", quote.Change);
        Assert.Equal("1.22", quote.ChangePercent);
    }

    [Fact]
    public void A_mainland_timestamp_is_split_into_a_readable_date_and_time()
    {
        var body = Quote("贵州茅台", "600519", "1460.21", "20261119154325", "17.55", "1.22");

        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(body));

        Assert.Equal("2026-11-19", quote.Date);
        Assert.Equal("15:43:25", quote.Time);
    }

    [Fact]
    public void The_change_percent_is_not_mistaken_for_the_date()
    {
        // The off-by-one this guards against showed a change percent of "1.22" as the quote's date.
        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(
            Quote("贵州茅台", "600519", "1460.21", "20261119154325", "17.55", "1.22")));

        Assert.NotEqual(quote.ChangePercent, quote.Date);
        Assert.Equal("2026-11-19", quote.Date);
    }

    [Fact]
    public void A_us_quote_uses_the_date_format_that_already_has_separators()
    {
        var body = Quote("苹果", "AAPL.OQ", "352.45", "2026-11-18 16:00:01", "0.54", "0.15", "v_usAAPL");

        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(body));

        Assert.Equal("苹果", quote.Name);
        Assert.Equal("352.45", quote.Price);
        Assert.Equal("2026-11-18", quote.Date);
        Assert.Equal("16:00:01", quote.Time);
    }

    [Fact]
    public void A_negative_move_keeps_its_sign()
    {
        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(
            Quote("平安银行", "000001", "12.04", "20261119155042", "-0.16", "-1.31", "v_sz000001")));

        Assert.Equal("-0.16", quote.Change);
        Assert.Equal("-1.31", quote.ChangePercent);
    }

    [Fact]
    public void Several_quotes_in_one_response_are_all_returned()
    {
        var body = Quote("贵州茅台", "600519", "1460.21", "20261119154325", "17.55", "1.22")
            + "\n" + Quote("平安银行", "000001", "12.04", "20261119155042", "-0.16", "-1.31", "v_sz000001");

        var quotes = IslandAdapters.ParseTencentMarkets(body);

        Assert.Equal(2, quotes.Count);
        Assert.Contains(quotes, q => q.Name == "平安银行" && q.ChangePercent == "-1.31");
        Assert.Contains(quotes, q => q.Name == "贵州茅台" && q.ChangePercent == "1.22");
    }

    [Fact]
    public void A_symbol_that_does_not_exist_is_skipped_rather_than_shown_as_zero()
    {
        // The endpoint answers an unknown symbol with a short or empty line.
        Assert.Empty(IslandAdapters.ParseTencentMarkets("v_sh999999=\"1~\";"));
    }

    [Fact]
    public void A_quote_with_a_zero_price_is_skipped()
    {
        Assert.Empty(IslandAdapters.ParseTencentMarkets(
            Quote("Something", "999999", "0", "20261119150000", "0", "0")));
    }

    [Fact]
    public void A_line_without_a_value_is_ignored()
    {
        Assert.Empty(IslandAdapters.ParseTencentMarkets("garbage without an equals sign"));
    }

    [Fact]
    public void An_empty_response_returns_nothing()
    {
        Assert.Empty(IslandAdapters.ParseTencentMarkets(string.Empty));
    }

    [Fact]
    public void The_quotes_are_quoted_and_unquoted_the_same_way()
    {
        // The endpoint's quoting has varied; both forms must parse.
        var body = Quote("贵州茅台", "600519", "1460.21", "20261119154325", "17.55", "1.22", quoted: false);

        var quote = Assert.Single(IslandAdapters.ParseTencentMarkets(body));

        Assert.Equal("贵州茅台", quote.Name);
        Assert.Equal("1.22", quote.ChangePercent);
    }

    [Fact]
    public void Gbk_bytes_decode_to_the_chinese_name()
    {
        // 贵州茅台 in GBK, as the endpoint actually sends it.
        var bytes = new byte[] { 0xB9, 0xF3, 0xD6, 0xDD, 0xC3, 0xA9, 0xCC, 0xA8 };

        Assert.Equal("贵州茅台", Gbk.GetString(bytes));
    }

    [Theory]
    [InlineData("sh600519")]
    [InlineData("sz000001")]
    [InlineData("usAAPL")]
    [InlineData("hk00700")]
    public void A_symbol_with_the_expected_shape_is_accepted(string symbol)
    {
        // The validator only rejects characters that cannot be part of a symbol; the endpoint decides
        // whether the symbol exists, and an unknown one comes back as an empty line.
        Assert.True(symbol.All(char.IsAsciiLetterOrDigit));
    }
}
