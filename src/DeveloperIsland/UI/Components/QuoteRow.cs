using System.Numerics;
using DeveloperIsland.Core.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// One quote as an instrument reading: name over code on the left, the price as the figure on the
/// right, the day's move as a signed delta under it, and a hairline bar showing where the last price
/// sits inside today's range.
/// <para>
/// The bar is what separates this from a line of text: it turns "1.22%" into "near the top of its
/// range", which is the thing a reader actually wants. It is drawn only when the source reports a
/// high and a low — a bar built from a guess would be a lie told with geometry.
/// </para>
/// </summary>
public sealed class QuoteRow : Grid
{
    private readonly Rectangle _fill;
    private readonly Border _knob;
    private readonly bool _animate;

    public QuoteRow(MarketReading quote, double rangeLow, double rangeHigh)
    {
        ColumnSpacing = 10;
        Margin = new Thickness(0, 0, 0, 0);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rising = !quote.ChangePercent.TrimStart('+').StartsWith('-');
        var flat = quote.ChangePercent.Length == 0 || quote.ChangePercent.Trim('0', '.', '+', '-', '%').Length == 0;

        var moveBrush = flat
            ? (Brush)Application.Current.Resources["TextSecondaryBrush"]
            : (Brush)Application.Current.Resources[rising ? "SuccessBrush" : "CriticalBrush"];

        var name = new TextBlock
        {
            Text = quote.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var code = new TextBlock
        {
            Text = quote.Symbol,
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            Margin = new Thickness(0, 1, 0, 0),
            CharacterSpacing = 40,
        };

        // The range bar: a track, the travelled part, and a knob at the last price.
        var track = new Rectangle
        {
            Height = 2,
            RadiusX = 1,
            RadiusY = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255)),
        };

        _fill = new Rectangle
        {
            Height = 2,
            RadiusX = 1,
            RadiusY = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = moveBrush,
        };

        _knob = new Border
        {
            Width = 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = moveBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.9,
        };

        var bar = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 0) };
        bar.Children.Add(track);
        bar.Children.Add(_fill);
        bar.Children.Add(_knob);

        var left = new StackPanel();
        left.Children.Add(name);
        left.Children.Add(code);
        left.Children.Add(bar);

        var price = new TextBlock
        {
            Text = quote.Price,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            IsTextSelectionEnabled = true,
        };

        var percent = quote.ChangePercent.Length > 0
            ? (rising ? "+" : string.Empty) + quote.ChangePercent + "%"
            : string.Empty;

        var delta = new TextBlock
        {
            Text = percent,
            FontSize = 11.5,
            Foreground = moveBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 0, 0),
        };

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(price);
        right.Children.Add(delta);

        Children.Add(left);
        Grid.SetColumn(right, 1);
        Children.Add(right);

        _animate = Animations.Motion.IsEnabled;

        // The bar is laid out on the next tick, when the row has a width to measure against.
        SizeChanged += (_, _) => Layout(quote, rangeLow, rangeHigh);
    }

    private void Layout(MarketReading quote, double low, double high)
    {
        if (ActualWidth <= 0)
        {
            return;
        }

        // The bar spans the left column only, minus the knob so the dot never overhangs.
        var width = Math.Max(0, ActualWidth - MeasureRight() - 10 - 4);
        if (width <= 0)
        {
            return;
        }

        if (!TryPrice(quote.Price, out var price) || !(high > low))
        {
            // No usable range: hide the bar rather than draw a position the numbers do not support.
            _fill.Width = 0;
            _knob.Opacity = 0;
            return;
        }

        var position = Math.Clamp((price - low) / (high - low), 0, 1);
        var target = width * position;

        _knob.Margin = new Thickness(target, 0, 0, 0);
        _knob.Opacity = 0.9;

        if (!_animate)
        {
            _fill.Width = Math.Max(1, target);
            return;
        }

        // The fill grows from zero the first time the row appears, then tracks the value. A compositor
        // animation keeps that off the UI thread while the panel is scrolling.
        var visual = ElementCompositionPreview.GetElementVisual(_fill);
        var grow = visual.Compositor.CreateScalarKeyFrameAnimation();
        grow.InsertKeyFrame(1f, (float)Math.Max(1, target), Ease());
        grow.Duration = TimeSpan.FromMilliseconds(520);
        visual.StartAnimation("Scale.X", grow);
        _fill.Width = Math.Max(1, target);
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
    }

    /// <summary>Width of the price column, so the bar stops before it instead of running underneath.</summary>
    private double MeasureRight()
    {
        if (Children.Count < 2 || Children[1] is not FrameworkElement right)
        {
            return 0;
        }

        right.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return right.DesiredSize.Width;
    }

    private static bool TryPrice(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    private CompositionEasingFunction Ease() =>
        ElementCompositionPreview.GetElementVisual(this).Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
}
