using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// The island's pulse: a small accent point that breathes on an uneven rhythm.
/// <para>
/// A metronome is the difference between a living thing and a machine that is merely on. The cycle
/// below is 3.4 s with three contractions of different depth at uneven intervals — it repeats, so the
/// compositor can loop it forever, but within one cycle nothing lines up. At rest the island should
/// feel alive without ever asking for attention.
/// </para>
/// <para>
/// Everything runs on the compositor: the dot keeps breathing while the UI thread is busy parsing a
/// transcript or drawing a panel, and stops entirely when Windows' animation setting is off.
/// </para>
/// </summary>
public sealed class PulseDot : Grid
{
    private readonly Ellipse _core;
    private readonly Ellipse _halo;
    private readonly Visual _coreVisual;
    private readonly Visual _haloVisual;

    public PulseDot()
    {
        Width = 20;
        Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        this.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AccessibilityViewProperty,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var accentColor = ((SolidColorBrush)accent).Color;

        // A 6 px core with a wider, much softer halo. The halo is what makes the contraction read as
        // light rather than as a circle changing size.
        _halo = new Ellipse
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
        };

        _core = new Ellipse
        {
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = accent,
        };

        Children.Add(_halo);
        Children.Add(_core);

        _coreVisual = ElementCompositionPreview.GetElementVisual(_core);
        _haloVisual = ElementCompositionPreview.GetElementVisual(_halo);
        _coreVisual.CenterPoint = new Vector3(3, 3, 0);
        _haloVisual.CenterPoint = new Vector3(7, 7, 0);

        if (Animations.Motion.IsEnabled)
        {
            Start();
        }
    }

    private void Start()
    {
        var ease = _coreVisual.Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        // One cycle, three beats, no two alike: 6% / 13% / 19% for the first two contractions, then a
        // long quiet stretch and a single softer one at 78%. The gaps are what make it a pulse.
        var scale = _coreVisual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0.00f, Vector3.One, ease);
        scale.InsertKeyFrame(0.06f, new Vector3(1.55f, 1.55f, 1f), ease);
        scale.InsertKeyFrame(0.13f, new Vector3(1.05f, 1.05f, 1f), ease);
        scale.InsertKeyFrame(0.19f, new Vector3(1.42f, 1.42f, 1f), ease);
        scale.InsertKeyFrame(0.34f, Vector3.One, ease);
        scale.InsertKeyFrame(0.78f, new Vector3(1.28f, 1.28f, 1f), ease);
        scale.InsertKeyFrame(1.00f, Vector3.One, ease);
        scale.Duration = TimeSpan.FromSeconds(3.4);
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        _coreVisual.StartAnimation("Scale", scale);

        var opacity = _coreVisual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0.00f, 0.45f);
        opacity.InsertKeyFrame(0.06f, 1.00f);
        opacity.InsertKeyFrame(0.19f, 0.92f);
        opacity.InsertKeyFrame(0.34f, 0.45f);
        opacity.InsertKeyFrame(0.78f, 0.82f);
        opacity.InsertKeyFrame(1.00f, 0.45f);
        opacity.Duration = TimeSpan.FromSeconds(3.4);
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        _coreVisual.StartAnimation("Opacity", opacity);

        // The halo expands as the core contracts: light spilling out while the point itself tightens.
        var haloScale = _haloVisual.Compositor.CreateVector3KeyFrameAnimation();
        haloScale.InsertKeyFrame(0.00f, new Vector3(0.72f, 0.72f, 1f), ease);
        haloScale.InsertKeyFrame(0.06f, new Vector3(1.24f, 1.24f, 1f), ease);
        haloScale.InsertKeyFrame(0.26f, new Vector3(0.78f, 0.78f, 1f), ease);
        haloScale.InsertKeyFrame(0.78f, new Vector3(1.05f, 1.05f, 1f), ease);
        haloScale.InsertKeyFrame(1.00f, new Vector3(0.72f, 0.72f, 1f), ease);
        haloScale.Duration = TimeSpan.FromSeconds(3.4);
        haloScale.IterationBehavior = AnimationIterationBehavior.Forever;
        _haloVisual.StartAnimation("Scale", haloScale);

        var haloOpacity = _haloVisual.Compositor.CreateScalarKeyFrameAnimation();
        haloOpacity.InsertKeyFrame(0.00f, 0.10f);
        haloOpacity.InsertKeyFrame(0.10f, 0.30f);
        haloOpacity.InsertKeyFrame(0.34f, 0.08f);
        haloOpacity.InsertKeyFrame(1.00f, 0.10f);
        haloOpacity.Duration = TimeSpan.FromSeconds(3.4);
        haloOpacity.IterationBehavior = AnimationIterationBehavior.Forever;
        _haloVisual.StartAnimation("Opacity", haloOpacity);
    }

    /// <summary>Stops every loop, for when the island is hidden rather than merely idle.</summary>
    public void Stop()
    {
        _coreVisual.StopAnimation("Scale");
        _coreVisual.StopAnimation("Opacity");
        _haloVisual.StopAnimation("Scale");
        _haloVisual.StopAnimation("Opacity");
    }
}
