using DeveloperIsland.Core.Services;

namespace DeveloperIsland.Tests;

/// <summary>
/// The animation set is chosen from the Chinese sky words the meteorological service sends, so a
/// mis-mapped word shows a sun in a thunderstorm. The words below are the ones the service actually
/// uses; "9999" is its placeholder for "not reported" and falls through to the picture code.
/// </summary>
public class WeatherConditionTests
{
    [Theory]
    [InlineData("晴", WeatherCondition.Clear)]
    [InlineData("多云", WeatherCondition.PartlyCloudy)]
    [InlineData("阴", WeatherCondition.Cloudy)]
    [InlineData("雾", WeatherCondition.Fog)]
    [InlineData("霾", WeatherCondition.Fog)]
    [InlineData("小雨", WeatherCondition.Rain)]
    [InlineData("中雨", WeatherCondition.Rain)]
    [InlineData("阵雨", WeatherCondition.Rain)]
    [InlineData("暴雨", WeatherCondition.Rain)]
    [InlineData("雨夹雪", WeatherCondition.Snow)]
    [InlineData("小雪", WeatherCondition.Snow)]
    [InlineData("大雪", WeatherCondition.Snow)]
    [InlineData("雷阵雨", WeatherCondition.Thunder)]
    [InlineData("雷暴", WeatherCondition.Thunder)]
    public void A_chinese_sky_word_maps_to_the_condition_the_panel_animates(string info, WeatherCondition expected)
    {
        Assert.Equal(expected, IslandAdapters.ConditionOf(info, null));
    }

    [Fact]
    public void Rain_mixed_with_snow_is_snow()
    {
        // "雨夹雪" contains 雨, but the sky is snowing: the snow check has to come first or the
        // panel animates rain through a snowstorm.
        Assert.Equal(WeatherCondition.Snow, IslandAdapters.ConditionOf("雨夹雪", null));
    }

    [Fact]
    public void A_thunderstorm_that_also_rains_is_thunder()
    {
        Assert.Equal(WeatherCondition.Thunder, IslandAdapters.ConditionOf("雷阵雨", null));
    }

    [Theory]
    [InlineData("0", WeatherCondition.Clear)]
    [InlineData("1", WeatherCondition.PartlyCloudy)]
    [InlineData("3", WeatherCondition.Cloudy)]
    [InlineData("7", WeatherCondition.Rain)]
    [InlineData("10", WeatherCondition.Snow)]
    [InlineData("49", WeatherCondition.Fog)]
    [InlineData("54", WeatherCondition.Thunder)]
    public void The_picture_code_is_used_when_the_text_is_a_placeholder(string code, WeatherCondition expected)
    {
        // The service sends "9999" for the text when it has none; the numeric code still carries the
        // answer, which is why the fallback exists at all.
        Assert.Equal(expected, IslandAdapters.ConditionOf("9999", code));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("9999", "9999")]
    [InlineData("未知", "abc")]
    public void Nothing_recognisable_is_unknown_rather_than_a_guess(string? info, string? code)
    {
        Assert.Equal(WeatherCondition.Unknown, IslandAdapters.ConditionOf(info, code));
    }

    [Fact]
    public void An_empty_reading_does_not_become_clear_skies()
    {
        // The default must not be Clear: showing a sun because the text was unreadable would be a
        // claim the data does not support.
        Assert.NotEqual(WeatherCondition.Clear, IslandAdapters.ConditionOf("", ""));
    }
}
