using System.Reflection;
using System.Text.RegularExpressions;

namespace OG_opossum_command.Core.Tests.Security;

/// <summary>
/// Phase 6 accessibility rules that a source scan can hold (ADR-047, ADR-055). Reads the Desktop project's XAML as
/// text, so nothing is loaded or run: font sizes are scaled DynamicResource keys, never literals; every scaled key in
/// App.xaml is rewritten by FontScale; and accent text brushes are not used, because the Fluent Accent* brushes keep
/// the user's accent under high contrast instead of a system colour.
/// </summary>
public sealed partial class DesktopXamlRulesTests
{
    private static string DesktopProjectDir()
    {
        var exe = typeof(DesktopXamlRulesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DesktopExePath").Value!;
        // <project>\bin\<Configuration>\net10.0-windows\OG_opossum_command.exe
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(dir, "App.xaml")), $"Desktop project not found at {dir}.");
        return dir;
    }

    private static IEnumerable<(string File, string Text)> XamlFiles()
    {
        var root = DesktopProjectDir();
        return Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                .Any(p => p is "bin" or "obj"))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllText(f)));
    }

    [GeneratedRegex(@"FontSize=""\d|Property=""FontSize""\s+Value=""\d")]
    private static partial Regex LiteralFontSize();

    [GeneratedRegex("""<sys:Double\s+x:Key="(Ogoc\w+Size)">""")]
    private static partial Regex ScaledKey();

    [Fact]
    public void Xaml_has_no_literal_font_sizes()
    {
        var offenders = XamlFiles().Where(x => LiteralFontSize().IsMatch(x.Text)).Select(x => x.File).ToList();
        Assert.True(offenders.Count == 0, "Use a DynamicResource Ogoc*Size key: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Xaml_uses_no_accent_text_brush()
    {
        var offenders = XamlFiles().Where(x => x.Text.Contains("AccentTextFillColor", StringComparison.Ordinal))
            .Select(x => x.File).ToList();
        Assert.True(offenders.Count == 0, "Accent text is not a system colour under high contrast: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_scaled_size_key_is_rewritten_by_FontScale()
    {
        var root = DesktopProjectDir();
        var keys = ScaledKey().Matches(File.ReadAllText(Path.Combine(root, "App.xaml"))).Select(m => m.Groups[1].Value).ToList();
        var fontScale = File.ReadAllText(Path.Combine(root, "Accessibility", "FontScale.cs"));

        Assert.Contains("OgocBodySize", keys); // the scan really found the keys
        Assert.All(keys, k => Assert.Contains($"(\"{k}\",", fontScale, StringComparison.Ordinal));
    }
}
