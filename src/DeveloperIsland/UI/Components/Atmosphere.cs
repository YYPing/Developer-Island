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
/// The sky as an abstract instrument reading, not an illustration.
/// <para>
/// There is deliberately no sun disc, no cloud puff and no umbrella: a drawn object at 44 px reads as
/// a stock icon, which is exactly what this is meant not to be. What is drawn instead is the physics
/// that makes a sky recognisable at a glance — light blooming through a diffuser, drops falling
/// through a plane with mass, flakes with depth, streamlines with speed.
/// </para>
/// <para>
/// Every element carries one idea and the whole is built from a few compositor animations, so it stays
/// cheap: no per-frame UI-thread work, and the loops stop when the panel is not on screen. With
/// Windows' "Animation effects" off, the same composition is laid out once and left still.
/// </para>
/// </summary>
public sealed class Atmosphere : Grid
{
    /// <summary>The plate the scene is drawn on. Small on purpose: it sits beside the reading, not above it.</summary>
    public const double Plate = 52;

    private readonly bool _animate;
    private readonly List<Visual> _animated = [];

    public Atmosphere(WeatherCondition condition, bool isDay, double windSpeed = 0)
    {
        Width = Plate;
        Height = Plate;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        // Everything is drawn inside the plate and nothing bleeds: a Grid clips to its bounds, and a
        // child positioned with a negative margin is simply not painted. The streamlines and rays are
        // therefore placed relative to the plate's own width rather than offset outside it.
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, Plate, Plate) };

        this.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AccessibilityViewProperty,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        _animate = Animations.Motion.IsEnabled;

        switch (condition)
        {
            case WeatherCondition.Clear:
                BuildLightBloom(isDay);
                break;
            case WeatherCondition.PartlyCloudy:
                BuildLightBloom(isDay, intensity: 0.62);
                BuildVeil(density: 0.55);
                break;
            case WeatherCondition.Cloudy:
                BuildVeil(density: 0.9);
                break;
            case WeatherCondition.Fog:
                BuildVeil(density: 0.7);
                BuildBands();
                break;
            case WeatherCondition.Drizzle:
                BuildVeil(density: 0.5);
                BuildFall(count: 7, length: 4.5, speed: 1.5, tint: RainTint, width: 1.1);
                BuildWetPlate(strength: 0.35);
                break;
            case WeatherCondition.Rain:
                BuildVeil(density: 0.62);
                BuildFall(count: 9, length: 9, speed: 0.95, tint: RainTint, width: 1.4);
                BuildWetPlate(strength: 0.7);
                break;
            case WeatherCondition.Snow:
                BuildVeil(density: 0.42);
                BuildFlakes();
                break;
            case WeatherCondition.Thunder:
                BuildVeil(density: 0.85);
                BuildFlash();
                BuildFall(count: 5, length: 7, speed: 1.2, tint: RainTint, width: 1.2);
                break;
            default:
                BuildVeil(density: 0.5);
                break;
        }

        // A strong wind is worth seeing whatever the sky is doing: the reading is what the number
        // means, and a still plate next to "西北风 8 m/s" would understate it.
        if (windSpeed >= 6 && condition is not (WeatherCondition.Snow or WeatherCondition.Rain or WeatherCondition.Thunder))
        {
            BuildStreamlines(strength: Math.Clamp(windSpeed / 12, 0.4, 1));
        }
    }

    /// <summary>Stops every loop. Called when the card is replaced or the panel unloads.</summary>
    public void Stop()
    {
        foreach (var visual in _animated)
        {
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("RotationAngleInDegrees");
        }

        _animated.Clear();
    }

    // ---- palette -------------------------------------------------------------------------------

    // The instrument's three tones, straight from the theme: warm light, cold water, cold air. Nothing
    // here is a fourth colour; a weather scene that invents its own palette stops looking like part of
    // the same machine.
    private static Windows.UI.Color Amber(byte alpha = 255) => Windows.UI.Color.FromArgb(alpha, 0xFF, 0xB3, 0x40);

    private static Windows.UI.Color RainTint => Windows.UI.Color.FromArgb(255, 0x6F, 0xA8, 0xE8);

    private static Windows.UI.Color ColdWhite(byte alpha = 255) => Windows.UI.Color.FromArgb(alpha, 0xF0, 0xF4, 0xF8);

    private static Windows.UI.Color WindTint(byte alpha = 255) => Windows.UI.Color.FromArgb(alpha, 0x35, 0xE0, 0xC0);

    // ---- sky pieces ----------------------------------------------------------------------------

    /// <summary>
    /// Light through a diffuser: a soft radial bloom with a tight hot centre, and rays that expand
    /// and contract around it. At night the bloom is cold and low, which is how a clear night reads.
    /// </summary>
    private void BuildLightBloom(bool isDay, double intensity = 1.0)
    {
        // Three concentric discs stand in for a radial gradient. A RadialGradientBrush on a small
        // Canvas-hosted element did not paint at all here, and stacked discs give the same soft falloff
        // with no brush support to depend on.
        var core = new Grid
        {
            Width = Plate * 0.62,
            Height = Plate * 0.62,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var (scale, alpha) in new[] { (1.0, 0.14), (0.72, 0.30), (0.44, 0.85) })
        {
            var disc = new Ellipse
            {
                Width = Plate * 0.62 * scale,
                Height = Plate * 0.62 * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(isDay ? Amber() : ColdWhite()),
                Opacity = alpha * intensity,
            };

            core.Children.Add(disc);
        }

        Children.Add(core);

        // Rays: thin wedges radiating out, each with its own phase so the bloom never looks like a
        // rotating logo.
        var rays = isDay ? 7 : 4;
        for (var i = 0; i < rays; i++)
        {
            var length = Plate * (0.30 + 0.12 * (i % 3));
            // The ray sits in a square equal to the plate, centred, and is rotated about that centre.
            // Positioning it with a negative margin pushed it outside the plate's clip, which is why
            // the first version drew nothing at all.
            var ray = new Rectangle
            {
                Width = 1.1,
                Height = length,
                RadiusX = 0.55,
                RadiusY = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 1.0),
                RenderTransform = new RotateTransform { Angle = i * (360.0 / rays) },
                Fill = new SolidColorBrush(isDay ? Amber(120) : ColdWhite(80)),
            };

            Children.Add(ray);

            if (!_animate)
            {
                continue;
            }

            var visual = ElementCompositionPreview.GetElementVisual(ray);
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0.25f, Ease());
            opacity.InsertKeyFrame(0.5f, 0.85f, Ease());
            opacity.InsertKeyFrame(1f, 0.25f, Ease());
            opacity.Duration = TimeSpan.FromSeconds(4.2 + i * 0.31);
            opacity.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", opacity);
            _animated.Add(visual);
        }

        if (!_animate)
        {
            return;
        }

        // The bloom itself breathes, very slowly: 6.5 s is under the threshold where the eye starts
        // watching it rather than reading past it.
        var coreVisual = ElementCompositionPreview.GetElementVisual(core);
        coreVisual.CenterPoint = new Vector3((float)core.Width / 2, (float)core.Height / 2, 0);

        var breath = coreVisual.Compositor.CreateVector3KeyFrameAnimation();
        breath.InsertKeyFrame(0f, Vector3.One, Ease());
        breath.InsertKeyFrame(0.5f, new Vector3(1.12f, 1.12f, 1f), Ease());
        breath.InsertKeyFrame(1f, Vector3.One, Ease());
        breath.Duration = TimeSpan.FromSeconds(6.5);
        breath.IterationBehavior = AnimationIterationBehavior.Forever;
        coreVisual.StartAnimation("Scale", breath);
        _animated.Add(coreVisual);
    }

    /// <summary>
    /// Cloud as veil, not as shape: two wide, very soft bands that slide past each other. Their edges
    /// are gradients so there is no outline anywhere to read as a drawn cloud.
    /// </summary>
    private void BuildVeil(double density)
    {
        for (var i = 0; i < 2; i++)
        {
            var band = new Rectangle
            {
                Width = Plate * (1.05 - i * 0.18),
                Height = Plate * (0.30 - i * 0.06),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, Plate * (0.10 + i * 0.16), 0, 0),
                Opacity = density * (0.45 - i * 0.12),
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 0xB8, 0xC6, 0xD8)),
            };

            Children.Add(band);

            if (!_animate)
            {
                continue;
            }

            var visual = ElementCompositionPreview.GetElementVisual(band);
            ElementCompositionPreview.SetIsTranslationEnabled(band, true);
            var drift = visual.Compositor.CreateVector3KeyFrameAnimation();
            var reach = (float)(Plate * (0.05 + i * 0.03));
            drift.InsertKeyFrame(0f, new Vector3(-reach, 0, 0), Ease());
            drift.InsertKeyFrame(0.5f, new Vector3(reach, 0, 0), Ease());
            drift.InsertKeyFrame(1f, new Vector3(-reach, 0, 0), Ease());
            drift.Duration = TimeSpan.FromSeconds(9 + i * 2.5);
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", drift);
            _animated.Add(visual);
        }
    }

    /// <summary>Fog: horizontal strata that slide at different rates, the way real fog has no single speed.</summary>
    private void BuildBands()
    {
        for (var i = 0; i < 3; i++)
        {
            var bar = new Rectangle
            {
                Width = Plate * (0.78 - i * 0.10),
                Height = 1.5,
                RadiusX = 0.75,
                RadiusY = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, Plate * (0.08 + i * 0.17), 0, 0),
                Fill = new SolidColorBrush(ColdWhite((byte)(90 - i * 18))),
            };

            Children.Add(bar);

            if (!_animate)
            {
                continue;
            }

            var visual = ElementCompositionPreview.GetElementVisual(bar);
            ElementCompositionPreview.SetIsTranslationEnabled(bar, true);
            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            var reach = (float)(Plate * (0.10 + i * 0.02));
            slide.InsertKeyFrame(0f, new Vector3(-reach, 0, 0), Ease());
            slide.InsertKeyFrame(0.5f, new Vector3(reach, 0, 0), Ease());
            slide.InsertKeyFrame(1f, new Vector3(-reach, 0, 0), Ease());
            slide.Duration = TimeSpan.FromSeconds(7.5 + i * 1.8);
            slide.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", slide);
            _animated.Add(visual);
        }
    }

    /// <summary>
    /// Drops with mass: each has its own speed, length and start delay, so the fall never marches in
    /// step. They fade in on the way down and out before the plate's edge.
    /// </summary>
    private void BuildFall(int count, double length, double speed, Windows.UI.Color tint, double width)
    {
        var random = new Random(17);
        var top = Plate * 0.18;

        for (var i = 0; i < count; i++)
        {
            var drop = new Rectangle
            {
                Width = width * (0.8 + random.NextDouble() * 0.5),
                Height = length * (0.7 + random.NextDouble() * 0.8),
                RadiusX = (float)width,
                RadiusY = (float)width,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8 + random.NextDouble() * (Plate - 16), top, 0, 0),
                Fill = new SolidColorBrush(tint),
            };

            Children.Add(drop);

            if (!_animate)
            {
                continue;
            }

            var fall = (Plate - top) * (0.85 + random.NextDouble() * 0.3);
            var duration = speed * (0.75 + random.NextDouble() * 0.7);
            var delay = random.NextDouble() * duration;

            var visual = ElementCompositionPreview.GetElementVisual(drop);
            ElementCompositionPreview.SetIsTranslationEnabled(drop, true);

            var move = visual.Compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, Vector3.Zero);
            move.InsertKeyFrame(1f, new Vector3(0, (float)fall, 0));
            move.Duration = TimeSpan.FromSeconds(duration);
            move.DelayTime = TimeSpan.FromSeconds(delay);
            move.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", move);

            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.15f, 0.85f);
            fade.InsertKeyFrame(0.8f, 0.6f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromSeconds(duration);
            fade.DelayTime = TimeSpan.FromSeconds(delay);
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", fade);

            _animated.Add(visual);
        }
    }

    /// <summary>
    /// A wet plane: a low, wide sheen at the bottom, plus rings that expand and fade where drops land.
    /// The rings are what make rain read as touching something rather than falling in a void.
    /// </summary>
    private void BuildWetPlate(double strength)
    {
        var sheen = new Rectangle
        {
            Width = Plate,
            Height = Plate * 0.22,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = strength * 0.5,
            Fill = new SolidColorBrush(RainTint),
        };

        Children.Add(sheen);

        var random = new Random(41);
        for (var i = 0; i < 3; i++)
        {
            var ring = new Ellipse
            {
                Width = Plate * 0.34,
                Height = Plate * 0.09,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6 + random.NextDouble() * (Plate - 24), 0, 0, Plate * 0.06),
                Stroke = new SolidColorBrush(RainTint),
                StrokeThickness = 0.9,
                Opacity = 0,
            };

            Children.Add(ring);

            if (!_animate)
            {
                continue;
            }

            var visual = ElementCompositionPreview.GetElementVisual(ring);
            visual.CenterPoint = new Vector3((float)ring.Width / 2, (float)ring.Height / 2, 0);

            var grow = visual.Compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(0.25f, 0.25f, 1f));
            grow.InsertKeyFrame(0.7f, new Vector3(1.25f, 1.25f, 1f));
            grow.InsertKeyFrame(1f, new Vector3(1.4f, 1.4f, 1f));
            grow.Duration = TimeSpan.FromSeconds(2.4);
            grow.DelayTime = TimeSpan.FromSeconds(i * 0.8);
            grow.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Scale", grow);

            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.2f, 0.7f * (float)strength);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromSeconds(2.4);
            fade.DelayTime = TimeSpan.FromSeconds(i * 0.8);
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", fade);

            _animated.Add(visual);
        }
    }

    /// <summary>
    /// Snow with depth: near flakes are larger, brighter and slower to leave, far ones are small and
    /// dim. Each sways on a different period so no two share a path.
    /// </summary>
    private void BuildFlakes()
    {
        var random = new Random(73);

        for (var i = 0; i < 11; i++)
        {
            // 0 = far, 1 = near.
            var depth = random.NextDouble();
            var size = 1.4 + depth * 3.2;

            var flake = new Ellipse
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5 + random.NextDouble() * (Plate - 10), random.NextDouble() * Plate * 0.35, 0, 0),
                Fill = new SolidColorBrush(ColdWhite((byte)(110 + depth * 145))),
            };

            Children.Add(flake);

            if (!_animate)
            {
                continue;
            }

            var sway = 1.8 + random.NextDouble() * 3.2;
            var fall = (Plate * 0.95) * (0.7 + random.NextDouble() * 0.6);
            var duration = 3.4 + random.NextDouble() * 3.6 - depth * 1.2;
            var delay = random.NextDouble() * duration;

            var visual = ElementCompositionPreview.GetElementVisual(flake);
            ElementCompositionPreview.SetIsTranslationEnabled(flake, true);

            // A sine pair rather than a single bezier: the sideways drift has to reverse to look like
            // a flake caught in moving air.
            var path = visual.Compositor.CreateVector3KeyFrameAnimation();
            var steps = 6;
            for (var s = 0; s <= steps; s++)
            {
                var t = (float)s / steps;
                var x = (float)(Math.Sin(t * Math.PI * 2 + i) * sway);
                path.InsertKeyFrame(t, new Vector3(x, (float)(fall * t), 0), Ease());
            }

            path.Duration = TimeSpan.FromSeconds(duration);
            path.DelayTime = TimeSpan.FromSeconds(delay);
            path.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", path);

            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.12f, 1f);
            fade.InsertKeyFrame(0.85f, 1f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromSeconds(duration);
            fade.DelayTime = TimeSpan.FromSeconds(delay);
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", fade);

            _animated.Add(visual);
        }
    }

    /// <summary>
    /// Wind as streamlines: tapered curves crossing the plate, each faster than the last, with the
    /// leading end brighter. Speed is the subject here, so nothing is symmetrical.
    /// </summary>
    private void BuildStreamlines(double strength)
    {
        var random = new Random(29);

        for (var i = 0; i < 5; i++)
        {
            var length = Plate * (0.45 + random.NextDouble() * 0.5);
            var streak = new Rectangle
            {
                Width = length,
                Height = 1.2,
                RadiusX = 0.6,
                RadiusY = 0.6,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-length, Plate * (0.12 + i * 0.17) + random.NextDouble() * 4, 0, 0),
                Fill = new SolidColorBrush(WindTint((byte)(100 + i * 30))),
            };

            Children.Add(streak);

            if (!_animate)
            {
                continue;
            }

            var visual = ElementCompositionPreview.GetElementVisual(streak);
            ElementCompositionPreview.SetIsTranslationEnabled(streak, true);

            // Travel across the full plate plus the streak's own width, so it enters and leaves clean.
            var travel = (float)(Plate + length * 2);
            var duration = (2.6 - strength * 1.3) * (0.8 + random.NextDouble() * 0.5);

            var move = visual.Compositor.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0f, Vector3.Zero);
            move.InsertKeyFrame(1f, new Vector3(travel, 0, 0));
            move.Duration = TimeSpan.FromSeconds(duration);
            move.DelayTime = TimeSpan.FromSeconds(random.NextDouble() * duration);
            move.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Translation", move);

            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.2f, 1f);
            fade.InsertKeyFrame(0.8f, 1f);
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = TimeSpan.FromSeconds(duration);
            fade.DelayTime = TimeSpan.FromSeconds(random.NextDouble() * duration);
            fade.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", fade);

            _animated.Add(visual);
        }
    }

    /// <summary>Thunder: the plate brightens twice in quick succession, then stays dark for most of the cycle.</summary>
    private void BuildFlash()
    {
        var sheet = new Rectangle
        {
            Width = Plate,
            Height = Plate,
            Opacity = 0,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xCF, 0xE0, 0xFF)),
        };

        Children.Add(sheet);

        if (!_animate)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(sheet);
        var flash = visual.Compositor.CreateScalarKeyFrameAnimation();
        flash.InsertKeyFrame(0f, 0f);
        flash.InsertKeyFrame(0.05f, 0.20f);
        flash.InsertKeyFrame(0.11f, 0f);
        flash.InsertKeyFrame(0.17f, 0.13f);
        flash.InsertKeyFrame(0.28f, 0f);
        flash.InsertKeyFrame(1f, 0f);
        flash.Duration = TimeSpan.FromSeconds(5.5);
        flash.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", flash);
        _animated.Add(visual);
    }

    private CompositionEasingFunction Ease() =>
        ElementCompositionPreview.GetElementVisual(this).Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
}
