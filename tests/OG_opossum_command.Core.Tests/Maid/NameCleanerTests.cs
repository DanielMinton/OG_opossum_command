using OG_opossum_command.Core.Maid;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class NameCleanerTests
{
    private static readonly DateTimeOffset Date = new(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("budget_review_final_FINAL (1)", "Budget Review")]
    [InlineData("Copy of Lease Agreement", "Lease Agreement")]
    [InlineData("annual-report-2025-v3", "Annual Report 2025")]
    [InlineData("Contoso HR onboarding 2026-01-15", "Contoso HR Onboarding")]
    [InlineData("Program Flyer - Copy (2)", "Program Flyer")]
    [InlineData("meeting notes 3f2504e0-4f89-11d3-9a0c-0305e82c3301", "Meeting Notes")]
    [InlineData("e-ticket for the trip", "E-ticket for the Trip")]
    [InlineData("Taxes 2024", "Taxes 2024")]
    [InlineData("iPhone setup guide", "iPhone Setup Guide")]
    public void Clean_strips_noise_and_keeps_meaningful_words(string stem, string expected)
    {
        Assert.Equal(expected, NameCleaner.Clean(stem, null, "document", [], [], Date));
    }

    [Fact]
    public void Meaningless_names_are_rebuilt_from_content()
    {
        Assert.Equal("Contoso Bank Statement", NameCleaner.Clean("Scan_20260314_101010", null, "statement", ["Contoso Bank"], ["balance"], Date));
        Assert.Equal("Fabrikam Invoice", NameCleaner.Clean("document (3)", null, "invoice", [], ["fabrikam"], Date));
    }

    [Fact]
    public void A_good_metadata_title_beats_a_meaningless_name()
    {
        Assert.Equal("Volunteer Handbook", NameCleaner.Clean("download", "Volunteer Handbook", "document", [], [], Date));
    }

    [Fact]
    public void Only_a_meaningless_name_gets_a_date()
    {
        Assert.Equal("Photo 2026-03-14", NameCleaner.Clean("IMG_20260314_090000", null, "photo", [], [], Date));
        Assert.DoesNotContain("2026", NameCleaner.Clean("Garden plan 2026-03-14", null, "document", [], [], Date), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_are_capped_at_forty_characters_on_a_word_boundary()
    {
        var name = NameCleaner.Clean("the quite long name of a very detailed quarterly program evaluation summary", null, "report", [], [], Date);
        Assert.True(name.Length <= NameCleaner.MaxLength, name);
        Assert.False(name.EndsWith(' '));
        Assert.StartsWith("The Quite Long Name", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a<b>c:d", "a b c d")]
    [InlineData("..", "File")]
    [InlineData("", "File")]
    [InlineData("name.", "name")]
    public void Sanitise_makes_any_proposal_a_safe_file_name(string input, string expected)
    {
        Assert.Equal(expected, NameCleaner.Sanitise(input));
    }

    [Theory]
    [InlineData("Money", true)]
    [InlineData("_Review", true)]
    [InlineData(@"Money\Taxes", false)]
    [InlineData("..", false)]
    [InlineData(" Money", false)]
    [InlineData("", false)]
    public void Folder_names_must_be_one_safe_level(string name, bool safe)
    {
        Assert.Equal(safe, NameCleaner.IsSafeFolderName(name));
    }

    [Fact]
    public void Versions_and_finals_are_recognised()
    {
        Assert.Equal(2, NameCleaner.VersionOf("schedule v2"));
        Assert.Equal(3, NameCleaner.VersionOf("budget version 3"));
        Assert.Null(NameCleaner.VersionOf("budget"));
        Assert.True(NameCleaner.SaysFinal("budget FINAL"));
        Assert.Equal(NameCleaner.BaseOf("budget v1"), NameCleaner.BaseOf("Budget_final (1)"));
    }
}
