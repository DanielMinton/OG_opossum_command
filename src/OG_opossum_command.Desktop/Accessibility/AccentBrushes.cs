using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OG_opossum_command.Desktop.Accessibility;

/// <summary>
/// The two accent gradients (ADR-057): <c>OgocBadgeFill</c> (tile badge) and <c>OgocGlowFill</c> (tile hover glow).
/// WPF cannot point a gradient stop at a DynamicResource colour, so they are built here from the Fluent theme's
/// accent colours, frozen, and rebuilt when Windows colours or high contrast change. Views use them through
/// DynamicResource, so a rebuild repaints live.
/// </summary>
internal static class AccentBrushes
{
    public static void Track(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Apply(app);
        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            {
                // After the theme has refreshed its own accent colours.
                app.Dispatcher.BeginInvoke(() => Apply(app), System.Windows.Threading.DispatcherPriority.Background);
            }
        };
        SystemEvents.UserPreferenceChanged += handler;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }

    internal static void Apply(Application app)
    {
        // Start from the theme's own accent fill (lighter in dark mode, darker in light mode), so the badge matches
        // the accent buttons and its on-accent glyph keeps the theme's contrast; then add a gentle sheen.
        var fill = app.TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accentFill
            ? accentFill.Color
            : SystemParameters.WindowGlassColor;
        var badge = new LinearGradientBrush(Mix(fill, Colors.White, 0.22), Mix(fill, Colors.Black, 0.12), new Point(0, 0), new Point(1, 1));
        badge.Freeze();
        app.Resources["OgocBadgeFill"] = badge;

        var light1 = Color(app, "SystemAccentColorLight1", fill);
        var glow = new RadialGradientBrush(light1, Colors.Transparent)
        {
            Center = new Point(0.12, 0.1),
            GradientOrigin = new Point(0.12, 0.1),
            RadiusX = 0.9,
            RadiusY = 1.3,
            Opacity = 0.22,
        };
        glow.Freeze();
        app.Resources["OgocGlowFill"] = glow;
    }

    private static Color Mix(Color from, Color to, double amount) => System.Windows.Media.Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    private static Color Color(Application app, string key, Color fallback) =>
        app.TryFindResource(key) is Color color ? color : fallback;
}
