using OG_opossum_command.Core.Exports;

namespace OG_opossum_command.Core.Tests.Exports;

public sealed class ExportTextTests
{
    [Fact]
    public void Secrets_are_redacted_through_OutputRedactor()
    {
        var scrubbed = ExportText.Scrub("login failed: password=hunter2 and Bearer abcdefghijklmnop.qrstuvwxyz");

        Assert.NotNull(scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", scrubbed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Could not find C:\Users\Someone\My Docs\report final.xlsx today", "Someone")]
    [InlineData(@"Could not find C:\Users\Someone\My Docs\report final.xlsx today", "final.xlsx")]
    [InlineData(@"Opened \\fileserver\share\private\notes.txt", "fileserver")]
    [InlineData(@"Calendar file %OneDriveCommercial%\OG_opossum_command\calendar.json is stale", "OneDriveCommercial")]
    [InlineData(@"forward/slash D:/data/secret/thing.txt", "secret")]
    public void File_paths_are_removed(string text, string leaked)
    {
        var scrubbed = ExportText.Scrub(text)!;

        Assert.DoesNotContain(leaked, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ExportText.PathPlaceholder, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\you\OneDrive - Contoso\HR\Salary review 2026.xlsx'.", "Could not find file '[path]'.")]
    [InlineData(@"Access to the path ""\\server\share\Team A\plan b.docx"" is denied.", @"Access to the path ""[path]"" is denied.")]
    [InlineData(@"Opened %OneDriveCommercial%\OneDrive - Org\a b\c d.xlsx: locked", "Opened [path]: locked")]
    [InlineData(@"Could not find C:\Users\Someone\OneDrive - Org\a b\c d.xlsx", "Could not find [path]")]
    [InlineData(@"Saved (C:\Users\Someone\OneDrive - Org\a b\c d.xlsx) fine", "Saved ([path]) fine")]
    public void Paths_with_spaces_are_removed_whole(string text, string expected)
    {
        var scrubbed = ExportText.Scrub(text);

        Assert.Equal(expected, scrubbed);
        foreach (var leaked in new[] { "Contoso", "HR", "Salary", "review", "Org", "a b", "c d", "xlsx", "Team", "plan", "docx", "server" })
        {
            Assert.DoesNotContain(leaked, scrubbed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Known_roots_are_removed_literally_and_an_apostrophe_in_a_folder_name_leaks_nothing()
    {
        using var fx = new ExportsFixture();
        var root = fx.WorkOneDrive; // "<temp>\OneDrive - Work"

        var scrubbed = ExportText.Scrub($"Could not open '{root.ToUpperInvariant()}\\O'Brien files\\notes 1.txt' now", paths: fx.Paths)!;

        Assert.DoesNotContain("Work", scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Brien", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("notes", scrubbed, StringComparison.Ordinal);
        Assert.StartsWith("Could not open '[path]", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Join at https://teams.microsoft.com/l/meetup-join/19%3ameeting?context=abc now", "teams.microsoft.com")]
    [InlineData("see www.example.com/private/path", "example.com")]
    [InlineData("file:///C:/x/y.txt broke", "y.txt")]
    public void Urls_are_removed(string text, string leaked)
    {
        var scrubbed = ExportText.Scrub(text)!;

        Assert.DoesNotContain(leaked, scrubbed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ExportText.LinkPlaceholder, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_capped_at_200_characters_on_one_line()
    {
        var scrubbed = ExportText.Scrub(new string('x', 150) + "\r\n\t" + new string('y', 500))!;

        Assert.True(scrubbed.Length <= ExportText.MaxFieldChars, $"length {scrubbed.Length}");
        Assert.EndsWith("…", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', scrubbed);
        Assert.DoesNotContain('\t', scrubbed);
        Assert.DoesNotContain("truncated", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Blank_is_null(string? text) => Assert.Null(ExportText.Scrub(text));

    [Fact]
    public void Plain_text_is_unchanged()
    {
        Assert.Equal("Timed out after 20 s", ExportText.Scrub("Timed out after 20 s"));
    }

    [Fact]
    public void Timestamps_carry_the_local_offset()
    {
        var text = ExportText.Timestamp(ExportsFixture.Noon, ExportsFixture.Zone);

        Assert.Equal("2026-10-23T12:00:00-07:00", text);
    }

    [Fact]
    public void App_version_has_no_build_metadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", ExportText.AppVersion);
    }
}
