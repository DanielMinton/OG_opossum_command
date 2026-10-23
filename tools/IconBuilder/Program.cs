// Throwaway icon builder: turns one large transparent PNG into the app's .ico and the PNGs Power Apps / Teams want.
// Usage: IconBuilder <source.png> <outDir> [faceX faceY faceSize]
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(args[0]);
        src.EndInit();
        src.Freeze();
        var outDir = args[1];
        Directory.CreateDirectory(outDir);
        var face = args.Length >= 5
            ? new Int32Rect(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[4]))
            : new Int32Rect(560, 40, 480, 480);

        // Full picture centred in a square, in source pixels.
        int side = Math.Max(src.PixelWidth, src.PixelHeight);
        var fullSquare = new Rect((src.PixelWidth - side) / 2.0, (src.PixelHeight - side) / 2.0, side, side);

        int[] small = [16, 20, 24, 32];
        int[] large = [48, 64, 128, 256];

        // Comparison sheet: full vs face, each size at 1x and 4x.
        int[] all = [16, 20, 24, 32, 48, 64];
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 46)), null, new Rect(0, 0, 1400, 760));
            double x = 20;
            foreach (var s in all)
            {
                var a = Full(src, fullSquare, s);
                var b = Face(src, face, s);
                Label(dc, $"{s} px", x, 6);
                dc.DrawImage(a, new Rect(x, 34, s, s));
                DrawNearest(dc, a, new Rect(x, 110, s * 4, s * 4));
                dc.DrawImage(b, new Rect(x, 400, s, s));
                DrawNearest(dc, b, new Rect(x, 476, s * 4, s * 4));
                x += Math.Max(s * 4, 70) + 26;
            }

            Label(dc, "Top: full picture.   Bottom: face close-up.   Each at real size, then 4x.", 20, 735);
        }

        Save(Render(sheet, 1400, 760), Path.Combine(outDir, "icon-compare.png"));

        // ICO: small sizes from the face, large sizes from the full picture. PNG-compressed entries.
        var entries = new List<(int Size, byte[] Png)>();
        foreach (var s in small) entries.Add((s, Png(Face(src, face, s))));
        foreach (var s in large) entries.Add((s, Png(Full(src, fullSquare, s))));
        WriteIco(Path.Combine(outDir, "app.ico"), entries);

        // PNGs for Power Apps (512) and a Teams colour icon (192), plus a 64 px site logo.
        Save(Full(src, fullSquare, 512), Path.Combine(outDir, "hub-icon-512.png"));
        Save(Full(src, fullSquare, 192), Path.Combine(outDir, "teams-color-192.png"));
        Save(Full(src, fullSquare, 64), Path.Combine(outDir, "site-logo-64.png"));
        Console.WriteLine("done");
        return 0;
    }

    private static BitmapSource Full(BitmapSource src, Rect fromPx, int size)
    {
        var v = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.Fant);
        using (var dc = v.RenderOpen())
        {
            var crop = new CroppedBitmap(src, Clamp(src, fromPx));
            var scale = size / fromPx.Width;
            var off = new Point((Math.Max(0, -fromPx.X)) * scale, (Math.Max(0, -fromPx.Y)) * scale);
            dc.DrawImage(Downscale(crop, crop.PixelWidth * scale, crop.PixelHeight * scale), new Rect(off.X, off.Y, crop.PixelWidth * scale, crop.PixelHeight * scale));
        }

        return Render(v, size, size);
    }

    private static BitmapSource Face(BitmapSource src, Int32Rect face, int size)
    {
        var v = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.Fant);
        using (var dc = v.RenderOpen())
        {
            var disc = new EllipseGeometry(new Rect(0, 0, size, size));
            dc.PushClip(disc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(14, 30, 74)), null, new Rect(0, 0, size, size));
            dc.DrawImage(Downscale(new CroppedBitmap(src, face), size, size), new Rect(0, 0, size, size));
            dc.Pop();
            var w = Math.Max(1.0, size / 16.0);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(80, 150, 255)), w), new Point(size / 2.0, size / 2.0), size / 2.0 - w / 2, size / 2.0 - w / 2);
        }

        return Render(v, size, size);
    }

    // Halve repeatedly before the last step: much sharper than one big jump.
    private static BitmapSource Downscale(BitmapSource img, double w, double h)
    {
        BitmapSource cur = img;
        while (cur.PixelWidth / 2.0 > w)
        {
            cur = new TransformedBitmap(cur, new ScaleTransform(0.5, 0.5));
            cur = Render(Wrap(cur), cur.PixelWidth, cur.PixelHeight);
        }

        return cur;
    }

    private static DrawingVisual Wrap(BitmapSource img)
    {
        var v = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.Fant);
        using var dc = v.RenderOpen();
        dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
        return v;
    }

    private static Int32Rect Clamp(BitmapSource src, Rect r)
    {
        int x = (int)Math.Max(0, r.X), y = (int)Math.Max(0, r.Y);
        int w = (int)Math.Min(src.PixelWidth - x, r.Width), h = (int)Math.Min(src.PixelHeight - y, r.Height);
        return new Int32Rect(x, y, w, h);
    }

    private static void DrawNearest(DrawingContext dc, BitmapSource img, Rect r)
    {
        var g = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(g, BitmapScalingMode.NearestNeighbor);
        using (var gc = g.Open()) gc.DrawImage(img, r);
        dc.DrawDrawing(g);
    }

    private static void Label(DrawingContext dc, string text, double x, double y) =>
        dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 14, Brushes.White, 1.0), new Point(x, y));

    private static RenderTargetBitmap Render(Visual v, int w, int h)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Png(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void Save(BitmapSource img, string path) => File.WriteAllBytes(path, Png(img));

    private static void WriteIco(string path, List<(int Size, byte[] Png)> entries)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var (size, png) in entries)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in entries) w.Write(png);
    }
}
