using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests;

/// <summary>
/// The demo snapshot never renders the quote or weather cards, because demo mode pauses external
/// integrations. These check the data shape the cards are built from instead.
/// </summary>
public class PanelCardProbe
{
    [Fact]
    public void A_quote_carries_everything_a_row_needs()
    {
        var fields = new string[40];
        fields[0] = "1";
        fields[1] = "贵州茅台";
        fields[2] = "600519";
        fields[3] = "1460.21";
        fields[30] = "20261119154325";
        fields[31] = "17.55";
        fields[32] = "1.22";

        var quote = IslandAdapters.ParseTencentMarkets("v_sh600519=\"" + string.Join("~", fields) + "\";").Single();

        // Name, code, price and the day's move: the four things the row lays out.
        Assert.Equal("贵州茅台", quote.Name);
        Assert.Equal("600519", quote.Symbol);
        Assert.Equal("1460.21", quote.Price);
        Assert.Equal("1.22", quote.ChangePercent);
    }

    [Fact]
    public void A_flat_quote_is_distinguishable_from_a_rise()
    {
        // The card colours a rise green and a fall red; a flat quote must not borrow either, which is
        // why the sign is checked rather than the magnitude.
        static string Build(string percent)
        {
            var f = new string[40];
            f[0] = "1";
            f[1] = "X";
            f[2] = "X";
            f[3] = "10";
            f[30] = "20261119154325";
            f[31] = "0";
            f[32] = percent;
            return "v_x=\"" + string.Join("~", f) + "\";";
        }

        Assert.StartsWith("-", IslandAdapters.ParseTencentMarkets(Build("-1.31")).Single().ChangePercent);
        Assert.DoesNotContain("-", IslandAdapters.ParseTencentMarkets(Build("1.22")).Single().ChangePercent);
        Assert.Equal("0.00", IslandAdapters.ParseTencentMarkets(Build("0.00")).Single().ChangePercent);
    }
}
