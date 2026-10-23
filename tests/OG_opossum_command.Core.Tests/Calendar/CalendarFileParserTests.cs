using OG_opossum_command.Core.Calendar;

namespace OG_opossum_command.Core.Tests.Calendar;

public sealed class CalendarFileParserTests
{
    /// <summary>tests/fixtures/calendar.sample.json, copied next to the test assembly (never read from the current directory).</summary>
    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "calendar.sample.json");

    public static string SampleJson => File.ReadAllText(SamplePath);

    private static CalendarSnapshot ParseValid(string json)
    {
        var result = CalendarFileParser.Parse(json, "<test>");
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.Snapshot!;
    }

    private static List<string> ParseInvalid(string json)
    {
        var result = CalendarFileParser.Parse(json, "<test>");
        Assert.False(result.IsValid, "Expected the calendar file to be rejected.");
        Assert.NotEmpty(result.Errors);
        return result.Errors.Select(e => $"{e.JsonPath}: {e.Message}").ToList();
    }

    private static string WithEvent(string eventJson, string generatedAt = "2026-10-22T15:45:00Z") => $$"""
        { "schemaVersion": 1, "generatedAt": "{{generatedAt}}", "events": [ {{eventJson}} ] }
        """;

    [Fact]
    public void The_sample_fixture_parses_with_every_field()
    {
        var snapshot = ParseValid(SampleJson);

        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 45, 0, TimeSpan.Zero), snapshot.GeneratedAt);
        Assert.Equal("power-automate", snapshot.Source);
        Assert.Equal("Pacific Standard Time", snapshot.TimeZone);
        Assert.Equal(4, snapshot.Events.Count);

        var standUp = snapshot.Events.Single(e => e.Subject == "Team stand-up");
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 16, 0, 0, TimeSpan.Zero), standUp.Start.ToUniversalTime());
        Assert.Equal("Microsoft Teams Meeting", standUp.Location);
        Assert.Equal("teams.microsoft.com", standUp.Link!.Host);
        Assert.Equal("busy", standUp.ShowAs);

        var walk = snapshot.Events.Single(e => e.Subject == "Site walkthrough");
        Assert.Null(walk.Link); // "" means no link
        Assert.Equal("tentative", walk.ShowAs);

        var allDay = snapshot.Events.Single(e => e.IsAllDay);
        Assert.Null(allDay.Location); // null tolerated
        Assert.Null(allDay.Link);

        Assert.Null(snapshot.Events.Single(e => e.Subject == "Budget review").Location); // "" tolerated
    }

    [Fact]
    public void Events_are_sorted_by_start()
    {
        var snapshot = ParseValid(SampleJson);
        Assert.Equal(snapshot.Events.OrderBy(e => e.Start).Select(e => e.Subject), snapshot.Events.Select(e => e.Subject));
    }

    [Fact]
    public void Today_lists_all_day_first_then_timed_events_and_drops_tomorrow()
    {
        var snapshot = ParseValid(SampleJson);
        var morning = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.FromHours(-7));

        var today = snapshot.EventsOn(morning).Select(e => e.Subject).ToList();

        Assert.Equal(["Staff appreciation week", "Team stand-up", "Site walkthrough"], today);
    }

    [Fact]
    public void Upcoming_drops_events_that_are_over_but_keeps_the_one_in_progress()
    {
        var snapshot = ParseValid(SampleJson);
        var duringWalk = new DateTimeOffset(2026, 10, 22, 12, 30, 0, TimeSpan.FromHours(-7)); // 19:30Z

        var upcoming = snapshot.UpcomingOn(duringWalk);

        Assert.Equal(["Staff appreciation week", "Site walkthrough"], upcoming.Select(e => e.Subject));
        Assert.True(upcoming.Single(e => e.Subject == "Site walkthrough").IsInProgressAt(duringWalk));
    }

    [Fact]
    public void An_empty_event_list_is_valid()
    {
        var snapshot = ParseValid("""{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z", "events": [] }""");
        Assert.Empty(snapshot.Events);
    }

    [Fact]
    public void A_missing_subject_becomes_a_placeholder()
    {
        var snapshot = ParseValid(WithEvent("""{ "subject": null, "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z" }"""));
        Assert.Equal("(no subject)", Assert.Single(snapshot.Events).Subject);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z", "events": [], "extra": 1 }""", "extra")]
    [InlineData("""{ "schemaVersion": 2, "generatedAt": "2026-10-22T15:45:00Z", "events": [] }""", "schemaVersion")]
    [InlineData("""{ "generatedAt": "2026-10-22T15:45:00Z", "events": [] }""", "schemaVersion")]
    [InlineData("""{ "schemaVersion": 1, "events": [] }""", "generatedAt")]
    [InlineData("""{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00", "events": [] }""", "generatedAt")]
    [InlineData("""{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z" }""", "events")]
    [InlineData("""{ "schemaVersion": "1", "generatedAt": "2026-10-22T15:45:00Z", "events": [] }""", "schemaVersion")]
    [InlineData("""[ ]""", "$")]
    public void Top_level_problems_are_rejected(string json, string expectedInMessage)
    {
        var errors = ParseInvalid(json);
        Assert.Contains(errors, e => e.Contains(expectedInMessage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00", "end": "2026-10-22T17:00:00Z" }""", "offset")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T15:00:00Z" }""", "before")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22", "end": "2026-10-23" }""", "offset")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z", "organizer": "a" }""", "organizer")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z", "link": "http://insecure.example" }""", "https")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z", "link": "file:///C:/x" }""", "https")]
    [InlineData("""{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z", "isAllDay": "no" }""", "isAllDay")]
    [InlineData("""{ "subject": 5, "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z" }""", "subject")]
    [InlineData("""{ "subject": "x", "end": "2026-10-22T17:00:00Z" }""", "start")]
    public void Event_problems_are_rejected(string eventJson, string expectedInMessage)
    {
        var errors = ParseInvalid(WithEvent(eventJson));
        Assert.Contains(errors, e => e.Contains(expectedInMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void A_date_only_value_is_accepted_for_all_day_events()
    {
        var snapshot = ParseValid(WithEvent("""{ "subject": "Holiday", "start": "2026-10-22", "end": "2026-10-23", "isAllDay": true }"""));
        var holiday = Assert.Single(snapshot.Events);
        Assert.True(holiday.IsAllDay);
        Assert.True(holiday.OccursOn(new DateOnly(2026, 10, 22), new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.FromHours(-7))));
        Assert.False(holiday.OccursOn(new DateOnly(2026, 10, 23), new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(-7))));
    }

    [Fact]
    public void Truncated_text_is_reported_as_malformed_json_not_as_a_shape_problem()
    {
        var half = SampleJson[..(SampleJson.Length / 2)];

        var result = CalendarFileParser.Parse(half, "<test>");

        Assert.False(result.IsValid);
        Assert.True(result.IsMalformedJson);
        Assert.False(CalendarFileParser.Parse("""{ "schemaVersion": 9 }""", "<test>").IsMalformedJson);
    }

    [Fact]
    public void Too_many_events_are_rejected()
    {
        var one = """{ "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z" }""";
        var json = $$"""{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z", "events": [ {{string.Join(",", Enumerable.Repeat(one, CalendarFileParser.MaxEvents + 1))}} ] }""";

        Assert.Contains(ParseInvalid(json), e => e.Contains("at most", StringComparison.Ordinal));
    }
}
