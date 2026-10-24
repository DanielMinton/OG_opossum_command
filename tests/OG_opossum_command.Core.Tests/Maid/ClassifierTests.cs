using OG_opossum_command.Core.Maid;

namespace OG_opossum_command.Core.Tests.Maid;

public sealed class ClassifierTests : IDisposable
{
    private readonly MaidFixture _world = new();

    public void Dispose() => _world.Dispose();

    private Classification ClassifyOne(string folder, string name, string text)
    {
        _world.Write(folder, name, text);
        var source = _world.Locations.Sources.Single(s => string.Equals(s.Path, folder, StringComparison.OrdinalIgnoreCase));
        var file = new Scanner(_world.Config.Settings).Scan([source]).Files.Single(f => f.Name == name);
        return new Classifier(_world.Config).Classify(file);
    }

    [Fact]
    public void A_work_inbox_report_is_work_reports_with_reasons()
    {
        var c = ClassifyOne(_world.Inbox, "q3.txt", "Quarterly report. Executive summary, key metrics and findings for the program.");
        Assert.Equal("report", c.DocType);
        Assert.Equal(MaidSide.Work, c.Side);
        Assert.Equal("Reports", c.Category);
        Assert.Contains(c.Reasons, r => r.Contains("where it was found", StringComparison.Ordinal));
        Assert.True(c.Confidence >= 0.75, $"confidence {c.Confidence}");
    }

    [Fact]
    public void Invoices_are_protected_and_tagged()
    {
        var c = ClassifyOne(_world.Downloads, "Scan_0001.txt", "INVOICE\nInvoice number 77\nBill to: Jane\nAmount due 12.00\nFabrikam Ltd\nFabrikam Ltd");
        Assert.Equal("invoice", c.DocType);
        Assert.True(c.IsProtected);
        Assert.Contains("invoice", c.Tags);
        Assert.Equal("Fabrikam Ltd Invoice", c.ShortName);
    }

    [Fact]
    public void A_downloads_file_with_only_a_category_fit_needs_a_decision()
    {
        var c = ClassifyOne(_world.Downloads, "roster.txt", "Summer camp roster, youth program, session times");
        Assert.Equal(MaidSide.Unassigned, c.Side);
        Assert.NotEqual(MaidSide.Unassigned, c.LikelySide);
        Assert.Contains(c.Reasons, r => r.Contains("Needs your decision", StringComparison.Ordinal));
    }

    [Fact]
    public void A_work_signal_places_a_downloads_file_on_the_work_side()
    {
        var c = ClassifyOne(_world.Downloads, "flyer.txt", "Contoso fundraiser gala event flyer, volunteer sign-up and agenda");
        Assert.Equal(MaidSide.Work, c.Side);
        Assert.Equal("Events", c.Category);
        Assert.True(c.HasWorkEvidence);
    }

    [Fact]
    public void A_personal_looking_file_in_the_work_inbox_is_a_cross_side_suspect()
    {
        _world.Config.Settings.PersonalSignals = ["my family", "my mortgage"];
        var c = ClassifyOne(_world.Inbox, "home.txt", "My mortgage statement for my family home. Mortgage, bank, statement, account number, closing balance.");
        Assert.Equal(MaidSide.Work, c.Side);
        Assert.True(c.CrossSideSuspect);
        Assert.Equal(MaidSide.Personal, c.LikelySide);
    }

    [Fact]
    public void Credential_names_are_security_and_never_opened()
    {
        var path = _world.Write(_world.Downloads, "bank recovery codes.txt", "1111-2222");
        var file = new Scanner(_world.Config.Settings).Scan(_world.Locations.Sources).Files.Single(f => f.FullPath == path);
        Assert.True(file.IsCredentialName);
        Assert.Null(file.Sha256);
        Assert.Equal(string.Empty, file.Extraction!.Text);
        Assert.True(new Classifier(_world.Config).Classify(file).IsSecurity);
    }

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("vault.kdbx")]
    [InlineData("server.pem")]
    [InlineData("cert.pfx")]
    [InlineData(".env.local")]
    [InlineData("Passwords 2026.xlsx")]
    [InlineData("github-recovery-codes.txt")]
    [InlineData("api_keys.json")]
    public void Credential_guard_matches_names_only(string name)
    {
        Assert.True(CredentialGuard.LooksLikeCredential(name));
    }

    [Theory]
    [InlineData("Lease agreement.pdf")]
    [InlineData("Budget.xlsx")]
    [InlineData("Keynote speech.docx")]
    public void Ordinary_names_are_not_credentials(string name)
    {
        Assert.False(CredentialGuard.LooksLikeCredential(name));
    }

    [Fact]
    public void Secret_shaped_text_makes_a_file_security_too()
    {
        var c = ClassifyOne(_world.Downloads, "notes.txt", "server notes\npassword: hunter2hunter2\nremember to rotate");
        Assert.True(c.IsSecurity);
    }

    [Fact]
    public void A_learned_rule_decides_side_and_category()
    {
        _world.Config.Learned.Rules =
        [
            new LearnedRule { Id = "L0001", Match = LearnedMatch.Mentions, Value = "northwind", Side = MaidSide.Personal, Category = "Hobbies", Hits = 2 },
        ];
        var c = ClassifyOne(_world.Downloads, "club.txt", "Northwind hiking club newsletter");
        Assert.Equal(MaidSide.Personal, c.Side);
        Assert.Equal("Hobbies", c.Category);
        Assert.True(c.UsedLearnedRule);
        Assert.Contains(c.Reasons, r => r.Contains("L0001", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_analysis_finds_entities_and_keywords()
    {
        var text = "Contoso Bank sent a letter.\nContoso Bank says the loan is approved.\nWrite to help@fabrikam.com about the loan.";
        var entities = TextAnalysis.Entities(text);
        Assert.Contains("Contoso Bank", entities);
        Assert.Contains("fabrikam.com", entities);
        Assert.Contains("loan", TextAnalysis.Keywords(text, string.Empty, string.Empty));
    }
}
