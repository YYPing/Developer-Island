using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Settings;

/// <summary>
/// The city and symbol fields. They live in Settings rather than in the island's panel because both
/// take free text: a capsule that retracts when the pointer leaves is a poor place to type.
/// <para>
/// The value is saved on every keystroke, not on a button: the field is a preference, and losing a
/// half-typed city because the window closed would be worse than an occasional intermediate save.
/// </para>
/// </summary>
public sealed partial class SettingsWindow
{
    private void LoadWeatherAndMarkets(Core.Settings.AppSettings s)
    {
        WeatherCityBox.Text = s.WeatherLocation;
        MarketSymbolsBox.Text = s.MarketSymbols;
    }

    private void OnWeatherCityChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = WeatherCityBox.Text.Trim();

        // An empty city would make the panel ask the API for nothing; keep the last good value
        // instead of clearing the setting on the way to typing a new one.
        if (value.Length == 0)
        {
            return;
        }

        _store.Update(s => s.WeatherLocation = value);
    }

    private void OnMarketSymbolsChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var value = MarketSymbolsBox.Text.Trim();
        if (value.Length == 0)
        {
            return;
        }

        _store.Update(s => s.MarketSymbols = value);
    }
}
