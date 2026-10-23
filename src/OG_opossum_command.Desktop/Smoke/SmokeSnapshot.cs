using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OG_opossum_command.Desktop.Smoke;

/// <summary>
/// Optional PNG snapshots during <c>--smoke-ui</c>, for checking the look without the real app (ADR-055).
/// Only when <c>OGOC_SMOKE_SNAPSHOT_DIR</c> names an existing absolute folder; otherwise does nothing.
/// The Mica backdrop is drawn by Windows, not WPF, so it does not appear in the image.
/// </summary>
internal static class SmokeSnapshot
{
    public const string DirVariable = "OGOC_SMOKE_SNAPSHOT_DIR";

    public static void Save(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
        {
            return;
        }

        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Paint the theme's window colour first; the real window shows Mica there.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = window.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawRectangle(new VisualBrush(window.Content as Visual ?? window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(file);
        Console.WriteLine($"smoke-ui: snapshot {name}.png");
    }
}
