using System.Windows;
using Microsoft.Win32;

namespace OG_opossum_command.Desktop.Accessibility;

/// <summary>
/// Scales the app's font-size resources (<c>OgocBodySize</c> and friends in App.xaml) with the system message font.
/// Every view refers to these resources through DynamicResource, and this class rewrites them at startup and on
/// preference changes. The scale is the message font relative to its default 12 DIP, clamped to 1–2.25.
/// Whether Settings › Accessibility › Text size reaches that font in WPF is unverified (docs/ACCEPTANCE.md M5.4);
/// if it does not, read <c>TextScaleFactor</c> here instead.
/// </summary>
internal static class FontScale
{
    private static readonly (string Key, double BaseSize)[] Sizes =
    [
        ("OgocBodySize", 14),
        ("OgocSmallSize", 12),
        ("OgocHeadingSize", 13),
        ("OgocLargeSize", 16),
        ("OgocTitleSize", 20),
        ("OgocDisplaySize", 28),
        ("OgocBadgeSize", 36), // not a font: the tile badge diameter, so its glyph still fits
    ];

    /// <summary>Applies the current scale to the app resources and keeps it current until the app exits.</summary>
    public static void Track(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Apply(app.Resources);
        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Window or UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
            {
                app.Dispatcher.BeginInvoke(() => Apply(app.Resources));
            }
        };
        SystemEvents.UserPreferenceChanged += handler;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }

    internal static void Apply(ResourceDictionary resources)
    {
        var scale = Math.Clamp(SystemFonts.MessageFontSize / 12.0, 1.0, 2.25);
        foreach (var (key, baseSize) in Sizes)
        {
            resources[key] = Math.Round(baseSize * scale, 1);
        }
    }
}
