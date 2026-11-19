using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Tests;

/// <summary>
/// The window is deliberately bigger than the capsule: it carries the shadow and grows into itself.
/// What must never happen is the capsule itself leaving the screen — a bottom-anchored island would
/// otherwise have the expanded panel's last rows past the display edge, where they are clipped
/// instead of scrolled. These tests pin the line between "shadow may hang off" and "content may not".
/// </summary>
public class PlacementClampTests
{
    private const double Inset = 40;

    /// <summary>A 1080p screen with a 48 px taskbar, at 100%.</summary>
    private static MonitorInfo Screen() => new(
        "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1032),
        1.0,
        IsPrimary: true);

    private static PixelRect Place(IslandAnchor anchor, double width = 500, double height = 880) =>
        IslandPlacement.WindowRect(Screen(), anchor, 0, 0, width, height, Inset);

    private static PixelRect Fit(PixelRect rect) => IslandPlacement.FitContentOnScreen(rect, Screen(), Inset);

    [Fact]
    public void A_bottom_anchored_window_is_pulled_up_so_its_content_stays_on_screen()
    {
        var fitted = Fit(Place(IslandAnchor.BottomCenter));

        // The capsule sits on its anchor, which is itself EdgeGap above the work area's bottom, so the
        // capsule's lower edge lands there rather than flush with the screen.
        var capsuleBottom = fitted.Y + fitted.Height - (int)Inset;
        var expected = 1032 - IslandPlacement.EdgeGap;

        Assert.Equal(expected, capsuleBottom, tolerance: 1);
    }

    [Fact]
    public void The_capsule_never_ends_below_the_work_area_from_any_anchor()
    {
        foreach (var anchor in Enum.GetValues<IslandAnchor>())
        {
            var fitted = Fit(Place(anchor));
            Assert.True(
                fitted.Y + fitted.Height - (int)Inset <= 1032 + 1,
                $"{anchor}: capsule bottom {fitted.Y + fitted.Height - (int)Inset} exceeds 1032");
        }
    }

    [Fact]
    public void The_capsule_never_ends_past_the_right_edge()
    {
        foreach (var anchor in Enum.GetValues<IslandAnchor>())
        {
            var fitted = Fit(Place(anchor));
            Assert.True(
                fitted.X + fitted.Width - (int)Inset <= 1920 + 1,
                $"{anchor}: capsule right {fitted.X + fitted.Width - (int)Inset} exceeds 1920");
        }
    }

    [Fact]
    public void A_top_anchored_window_keeps_its_shadow_overhang()
    {
        // The original placement lets a top-anchored window start above the work area on purpose.
        // Fitting must not disturb that, or the shadow gets a visible seam.
        var original = Place(IslandAnchor.TopCenter);
        var fitted = Fit(original);

        Assert.Equal(original.Y, fitted.Y);
        Assert.Equal(original.X, fitted.X);
    }

    [Fact]
    public void An_already_fitting_window_is_returned_unchanged()
    {
        var small = Place(IslandAnchor.BottomCenter, width: 500, height: 300);
        var fitted = Fit(small);

        // A short window already leaves room for its shadow, so nothing moves.
        Assert.Equal(small, fitted);
    }

    [Fact]
    public void A_window_taller_than_the_screen_does_not_produce_a_negative_position()
    {
        var fitted = Fit(Place(IslandAnchor.BottomCenter, width: 500, height: 2000));

        // The content cannot fit either way; what matters is the position stays sane rather than
        // becoming a large negative number that would place the window off the desktop.
        Assert.True(fitted.Y < 1032, $"y={fitted.Y}");
    }

    [Fact]
    public void A_secondary_monitor_is_fitted_to_its_own_work_area()
    {
        // PixelRect is (X, Y, Width, Height): this screen is 1920 wide, starting at x = 1920.
        var monitor = new MonitorInfo(
            "DISPLAY2",
            new PixelRect(1920, 0, 1920, 1080),
            new PixelRect(1920, 0, 1920, 1032),
            1.0,
            IsPrimary: false);

        var placed = IslandPlacement.WindowRect(monitor, IslandAnchor.BottomRight, 0, 0, 500, 880, Inset);
        var fitted = IslandPlacement.FitContentOnScreen(placed, monitor, Inset);

        Assert.True(fitted.Y + fitted.Height - (int)Inset <= 1032 + 1, "must use this monitor's work area");
        Assert.True(fitted.X + fitted.Width - (int)Inset <= 3840 + 1, $"right {fitted.X + fitted.Width - (int)Inset}");
    }

    [Fact]
    public void A_scaled_display_keeps_the_content_inside_its_work_area()
    {
        // 150%: the same DIP window occupies 1.5x as many physical pixels, and so does the inset.
        var monitor = new MonitorInfo(
            "DISPLAY1",
            new PixelRect(0, 0, 2880, 1620),
            new PixelRect(0, 0, 2880, 1548),
            1.5,
            IsPrimary: true);

        var placed = IslandPlacement.WindowRect(monitor, IslandAnchor.BottomCenter, 0, 0, 500, 880, Inset, 1.5);
        var inset = (int)Math.Ceiling(Inset * 1.5);
        var fitted = IslandPlacement.FitContentOnScreen(placed, monitor, Inset * 1.5);

        Assert.True(fitted.Y + fitted.Height - inset <= 1548 + 1, $"bottom {fitted.Y + fitted.Height - inset}");
    }

    [Fact]
    public void The_original_placement_behaviour_is_unchanged()
    {
        // Top center at 100%: the window starts 30 px above the work area for the shadow. This is the
        // upstream expectation, and the fit step must not be folded into WindowRect.
        Assert.Equal(new PixelRect(710, -30, 500, 400), Place(IslandAnchor.TopCenter, 500, 400));
    }
}
