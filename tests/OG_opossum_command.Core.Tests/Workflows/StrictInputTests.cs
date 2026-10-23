using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.M365;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Workflows;

/// <summary>
/// Hand-edited files fail loudly (ADR-022): a repeated JSON key is an error, not "last one wins" (SEC-8), and a NUL
/// character in a lock or a calendar path is a readable error, never an exception (SEC-6).
/// </summary>
public sealed class StrictInputTests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly ActionRegistry _registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);

    private void WritePolicy(ProfileId profile, string json)
    {
        var folder = _root.Paths.GetProfileDirectory(profile);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ProfileCatalog.PolicyFileName), json);
    }

    [Fact]
    public void A_workflow_step_with_a_duplicated_action_key_does_not_load()
    {
        const string json = """
            {
              "schemaVersion": 1, "id": "wf", "name": "WF", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [ { "id": "s", "action": "windows.url.open", "inputs": { "url": "https://example.com/" }, "action": "windows.process.launch" } ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _root.Profile(ProfileId.Work, _registry), json);

        Assert.Contains(errors, e => e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_profile_policy_with_a_duplicated_allowedActions_key_is_an_error_and_fails_closed()
    {
        WritePolicy(ProfileId.Personal, """{ "allowedActions": ["windows.url.open"], "allowedActions": ["windows.url.open", "m365.calendar.readFile"] }""");

        var catalog = ProfileCatalog.Load(_root.Paths, _registry);

        Assert.Contains(catalog.Errors, e => e.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.False(catalog.Get(ProfileId.Personal).IsActionAllowed(CalendarReadFileAction.ActionId));
    }

    [Fact]
    public void A_NUL_character_in_a_file_lock_is_a_validation_error_not_an_exception()
    {
        const string json = """
            {
              "schemaVersion": 2, "id": "wf", "name": "WF", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [ { "id": "s", "action": "windows.url.open", "inputs": { "url": "https://example.com/" }, "locks": ["file:C:\\a\u0000b.txt"] } ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _root.Profile(ProfileId.Work, _registry), json);

        Assert.Contains(errors, e => e.Message.Contains("NUL", StringComparison.Ordinal));
        Assert.NotNull(ResourceLocks.Normalize("file:C:\\a\0b.txt", out _));
    }

    [Fact]
    public void A_NUL_character_in_calendarFile_is_a_policy_error()
    {
        WritePolicy(ProfileId.Work, """{ "calendarFile": "C:\\cal\u0000.json" }""");

        var catalog = ProfileCatalog.Load(_root.Paths, _registry);

        Assert.Contains(catalog.Errors, e => e.Message.Contains("NUL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_calendar_service_reports_a_NUL_path_as_unavailable_instead_of_throwing()
    {
        var profile = new Profile(ProfileId.Work, _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Work), _root.Paths.GetProfileScriptsDirectory(ProfileId.Work), _registry.Ids, "C:\\cal\0.json");

        var result = await new CalendarService().ReadAsync(profile);

        Assert.Equal(CalendarStatus.Unavailable, result.Status);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void A_calendar_file_with_a_duplicated_key_does_not_parse()
    {
        const string json = """{ "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z", "events": [], "events": [] }""";

        var result = CalendarFileParser.Parse(json, "<test>");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_rejected_meeting_link_is_not_echoed_in_the_error()
    {
        const string json = """
            { "schemaVersion": 1, "generatedAt": "2026-10-22T15:45:00Z", "events": [
              { "subject": "x", "start": "2026-10-22T16:00:00Z", "end": "2026-10-22T17:00:00Z", "link": "http://meet.example/j?pwd=SECRETVALUE" } ] }
            """;

        var result = CalendarFileParser.Parse(json, "<test>");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("https", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("SECRETVALUE", StringComparison.Ordinal));
    }

    public void Dispose() => _root.Dispose();
}
