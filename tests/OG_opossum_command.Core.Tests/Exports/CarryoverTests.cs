using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Tests.Exports;

public sealed class CarryoverFileParserTests
{
    public const string Sample = """
        {
          "schemaVersion": 1,
          "forDate": "2026-10-23",
          "generatedAt": "2026-10-22T16:30:05.1234567-07:00",
          "items": [
            { "title": "Finish budget memo", "source": "todo", "due": "2026-10-24", "note": "Draft is in Teams" },
            { "title": "Reply to Sam", "source": "email", "due": null, "note": "" },
            { "title": "Follow up in channel", "source": "teams" },
            { "title": "Call the vendor", "source": "other", "due": "" }
          ]
        }
        """;

    private static CarryoverParseResult Parse(string json) => CarryoverFileParser.Parse(json, "tomorrow.json");

    private static string WithItems(string items) => $$"""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ {{items}} ] }""";

    [Fact]
    public void Parses_the_documented_sample()
    {
        var result = Parse(Sample);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var snapshot = result.Snapshot!;
        Assert.Equal(new DateOnly(2026, 10, 23), snapshot.ForDate);
        Assert.Equal(4, snapshot.Items.Count);
        Assert.Equal(new CarryoverItem("Finish budget memo", CarryoverSource.Todo, new DateOnly(2026, 10, 24), "Draft is in Teams"), snapshot.Items[0]);
        Assert.Equal(new CarryoverItem("Reply to Sam", CarryoverSource.Email, null, null), snapshot.Items[1]);
        Assert.Equal(CarryoverSource.Teams, snapshot.Items[2].Source);
        Assert.Null(snapshot.Items[3].Due);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": "a", "title": "b", "source": "todo" } ] }""")]
    public void Duplicate_keys_are_rejected(string json)
    {
        Assert.False(Parse(json).IsValid);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [], "extra": 1 }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": "a", "source": "todo", "link": "https://x" } ] }""")]
    public void Unknown_properties_are_rejected(string json)
    {
        var result = Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("Unknown property", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "23/10/2026", "generatedAt": "2026-10-22T23:30:00Z", "items": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00", "items": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z" }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": "a", "source": "Slack" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": "  ", "source": "todo" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": "a", "source": "todo", "due": "tomorrow" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [ { "title": 5, "source": "todo" } ] }""")]
    [InlineData("""[]""")]
    public void Wrong_shapes_are_rejected_with_messages(string json)
    {
        var result = Parse(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.False(result.IsMalformedJson);
    }

    [Fact]
    public void More_than_10_items_are_rejected()
    {
        var items = string.Join(", ", Enumerable.Range(0, 11).Select(i => $$"""{ "title": "item {{i}}", "source": "todo" }"""));

        var result = Parse(WithItems(items));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("at most 10", StringComparison.Ordinal));
        Assert.True(Parse(WithItems(string.Join(", ", Enumerable.Range(0, 10).Select(i => $$"""{ "title": "item {{i}}", "source": "todo" }""")))).IsValid);
    }

    [Fact]
    public void Strings_longer_than_200_characters_are_rejected()
    {
        Assert.True(Parse(WithItems($$"""{ "title": "{{new string('a', 200)}}", "source": "todo" }""")).IsValid);
        Assert.False(Parse(WithItems($$"""{ "title": "{{new string('a', 201)}}", "source": "todo" }""")).IsValid);
        Assert.False(Parse(WithItems($$"""{ "title": "ok", "source": "todo", "note": "{{new string('n', 201)}}" }""")).IsValid);
    }

    [Fact]
    public void Control_characters_are_stripped()
    {
        var result = Parse(WithItems("""{ "title": "Line one\r\nLine\ttwo\u0007!", "source": "todo" }"""));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("Line one Line two !", result.Snapshot!.Items[0].Title);
    }

    [Fact]
    public void An_escaped_NUL_is_refused()
    {
        var result = Parse(WithItems("""{ "title": "bad\u0000title", "source": "todo" }"""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("NUL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_raw_NUL_is_refused()
    {
        var result = Parse(Sample.Replace("Reply to Sam", "Reply\0to Sam", StringComparison.Ordinal));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("NUL", StringComparison.Ordinal));
    }

    [Fact]
    public void Oversize_text_is_refused()
    {
        var padded = Sample.Replace("\"items\"", new string(' ', CarryoverFileParser.MaxFileBytes) + "\"items\"", StringComparison.Ordinal);

        var result = Parse(padded);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("64 KB", StringComparison.Ordinal));
    }

    [Fact]
    public void Truncated_json_is_flagged_as_malformed()
    {
        var result = Parse(Sample[..40]);

        Assert.False(result.IsValid);
        Assert.True(result.IsMalformedJson);
    }
}

public sealed class CarryoverServiceTests : IDisposable
{
    private readonly ExportsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string CarryoverFile => Path.Combine(_fx.WorkFolder, AppPaths.CarryoversFileName);

    private CarryoverService Service() => new(_fx.Paths, _fx.Clock);

    private void Write(string text)
    {
        Directory.CreateDirectory(_fx.WorkFolder);
        File.WriteAllText(CarryoverFile, text);
    }

    [Fact]
    public void Missing_file_is_none()
    {
        Directory.CreateDirectory(_fx.WorkFolder);

        var result = Service().Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.None, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Missing_folder_is_unavailable_and_never_created()
    {
        var result = Service().Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.Unavailable, result.Status);
        Assert.False(Directory.Exists(_fx.WorkFolder));
    }

    [Fact]
    public void Todays_file_is_loaded()
    {
        Write(CarryoverFileParserTests.Sample);

        var service = Service();
        CarryoverReadResult? raised = null;
        service.Changed += (_, r) => raised = r;
        var result = service.Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.Loaded, result.Status);
        Assert.Equal(4, result.Items.Count);
        Assert.Contains("4 item", result.Message, StringComparison.Ordinal);
        Assert.Same(result, raised);
        Assert.Same(result, service.GetLatest(ProfileId.Work));
    }

    [Fact]
    public void A_file_for_another_day_shows_nothing_with_a_gentle_note()
    {
        Write(CarryoverFileParserTests.Sample.Replace("\"forDate\": \"2026-10-23\"", "\"forDate\": \"2026-10-22\"", StringComparison.Ordinal));

        var result = Service().Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.NotToday, result.Status);
        Assert.Empty(result.Items);
        Assert.Contains("No carryovers for today", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_date_decides_today()
    {
        Write(CarryoverFileParserTests.Sample);
        // 06:30 UTC on the 24th is still 23:30 on the 23rd in UTC-7.
        _fx.Clock.Set(new DateTimeOffset(2026, 10, 24, 6, 30, 0, TimeSpan.Zero));

        Assert.Equal(CarryoverStatus.Loaded, Service().Read(_fx.WorkProfile()).Status);

        _fx.Clock.Set(new DateTimeOffset(2026, 10, 24, 7, 30, 0, TimeSpan.Zero));
        Assert.Equal(CarryoverStatus.NotToday, Service().Read(_fx.WorkProfile()).Status);
    }

    [Fact]
    public void A_bad_file_gives_a_readable_message_and_never_throws()
    {
        Write("""{ "schemaVersion": 1, "forDate": "2026-10-23", "generatedAt": "2026-10-22T23:30:00Z", "items": [], "surprise": true }""");

        var result = Service().Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.Invalid, result.Status);
        Assert.Contains("surprise", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', result.Message);
    }

    [Fact]
    public void A_half_written_file_is_unavailable()
    {
        Write(CarryoverFileParserTests.Sample[..50]);

        Assert.Equal(CarryoverStatus.Unavailable, Service().Read(_fx.WorkProfile()).Status);
    }

    [Fact]
    public void An_oversize_file_is_not_read()
    {
        Write(new string(' ', CarryoverFileParser.MaxFileBytes + 1) + "{}");

        var result = Service().Read(_fx.WorkProfile());

        Assert.Equal(CarryoverStatus.Invalid, result.Status);
        Assert.Contains("64 KB", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_is_off_by_default()
    {
        var personal = _fx.LoadCatalog().Get(ProfileId.Personal);

        Assert.Equal(CarryoverStatus.Disabled, Service().Read(personal).Status);
    }

    [Fact]
    public void A_broken_profile_json_disables_carryovers()
    {
        Write(CarryoverFileParserTests.Sample);
        _fx.WritePolicy(ProfileId.Work, """{ "exports": { "carryovers": true, "carryovers": true } }""");

        var work = _fx.LoadCatalog().Get(ProfileId.Work);

        Assert.Equal(CarryoverStatus.Disabled, Service().Read(work).Status);
    }

    [Fact]
    public void Carryovers_switched_off_are_disabled()
    {
        Write(CarryoverFileParserTests.Sample);
        var profile = _fx.Profile(ProfileId.Work, new ExportSettings(_fx.WorkFolder, true, true, Carryovers: false));

        Assert.Equal(CarryoverStatus.Disabled, Service().Read(profile).Status);
    }

    [Fact]
    public void Personal_never_reads_the_work_OneDrive()
    {
        Write(CarryoverFileParserTests.Sample);
        var personal = _fx.Profile(ProfileId.Personal, new ExportSettings(_fx.WorkFolder, true, true, true));

        var result = Service().Read(personal);

        Assert.Equal(CarryoverStatus.Refused, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Work_never_reads_the_personal_OneDrive()
    {
        Directory.CreateDirectory(_fx.PersonalFolder);
        File.WriteAllText(Path.Combine(_fx.PersonalFolder, AppPaths.CarryoversFileName), CarryoverFileParserTests.Sample);
        var work = _fx.Profile(ProfileId.Work, new ExportSettings(_fx.PersonalFolder, true, true, true));

        Assert.Equal(CarryoverStatus.Refused, Service().Read(work).Status);
    }
}
