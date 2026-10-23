using System.Text.Json;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Exports;

public sealed class StatusExporterTests : IDisposable
{
    private readonly ExportsFixture _fx = new();
    private readonly SqliteHistoryStore _history;
    private readonly List<ExecutionSnapshot> _active = [];
    private readonly List<ProfileWorkflows> _workflows = [];

    public StatusExporterTests()
    {
        _history = _fx.OpenHistory();
    }

    public void Dispose()
    {
        _history.Dispose();
        _fx.Dispose();
    }

    private string StatusFile => Path.Combine(_fx.WorkFolder, AppPaths.StatusFileName);

    private StatusExporter Exporter(Profile? profile = null) =>
        new(profile ?? _fx.WorkProfile(), _fx.Paths, _history, () => _active, () => _workflows, _fx.Clock, "9.9.9");

    private JsonElement ReadStatus()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(StatusFile));
        return document.RootElement.Clone();
    }

    [Fact]
    public void Writes_status_json_atomically_with_the_v1_shape()
    {
        var state = Exporter().WriteNow(_fx.Clock.GetUtcNow());

        Assert.Equal(ExportOutcome.Written, state.Outcome);
        Assert.Equal(_fx.Clock.GetUtcNow(), state.LastWrittenAt);
        Assert.Equal([AppPaths.StatusFileName], ExportsFixture.FilesUnder(_fx.WorkFolder));

        var root = ReadStatus();
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Work", root.GetProperty("profile").GetString());
        Assert.Equal("2026-10-23T12:00:00-07:00", root.GetProperty("generatedAt").GetString());
        Assert.Equal("9.9.9", root.GetProperty("appVersion").GetString());
        Assert.Equal("2026-10-22T12:00:00-07:00", root.GetProperty("failedSince").GetString());
        Assert.Equal(0, root.GetProperty("completedToday").GetInt32());
        foreach (var array in new[] { "needsAttention", "failed", "runningNow", "scheduledToday" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(array).ValueKind);
        }
    }

    [Fact]
    public void Rewrites_replace_the_file_and_leave_no_temporary_file()
    {
        var exporter = Exporter();
        exporter.WriteNow(_fx.Clock.GetUtcNow());
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        exporter.WriteNow(_fx.Clock.GetUtcNow());

        Assert.Equal([AppPaths.StatusFileName], ExportsFixture.FilesUnder(_fx.WorkFolder));
        Assert.Equal("2026-10-23T12:01:00-07:00", ReadStatus().GetProperty("generatedAt").GetString());
    }

    [Fact]
    public void Lists_only_this_profiles_runs_with_scrubbed_text_and_no_output()
    {
        var now = _fx.Clock.GetUtcNow();
        var attention = ExportsFixture.Run(ExecutionStatus.NeedsAttention, now.AddHours(-1), error: @"Unsure whether C:\Users\Me\secret.txt was written; token=abc123");
        _history.SaveExecution(attention);
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddMinutes(-30), workflowId: "mail", stepMessage: "Exit code 1 from https://intranet.example/x?code=zzz password=hunter2"));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddMinutes(-20), profile: ProfileId.Personal, workflowId: "personal-only"));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now.AddHours(-2)));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now.AddHours(-3)));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now.AddDays(-1)));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now.AddHours(-1), profile: ProfileId.Personal));

        Exporter().WriteNow(now);

        var text = File.ReadAllText(StatusFile);
        Assert.DoesNotContain("personal-only", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUT-SHOULD-NEVER-LEAK", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("intranet.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\Users", text, StringComparison.Ordinal);

        var root = ReadStatus();
        var needs = Assert.Single(root.GetProperty("needsAttention").EnumerateArray());
        Assert.Equal(attention.Id.ToString("D"), needs.GetProperty("runId").GetString());
        Assert.Equal("first", needs.GetProperty("step").GetString());
        Assert.Contains("[path]", needs.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-10-23T11:00:00-07:00", needs.GetProperty("since").GetString());

        var failed = Assert.Single(root.GetProperty("failed").EnumerateArray());
        Assert.Equal("mail", failed.GetProperty("workflow").GetString());
        Assert.Equal("first", failed.GetProperty("step").GetString());
        Assert.Contains("[link]", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, root.GetProperty("completedToday").GetInt32());
    }

    [Fact]
    public void Failed_is_capped_at_20_newest_first_within_the_last_24_hours()
    {
        var now = _fx.Clock.GetUtcNow();
        for (var i = 0; i < 25; i++)
        {
            _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddMinutes(-i - 1), workflowId: $"wf{i}"));
        }

        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddHours(-25), workflowId: "too-old"));

        Exporter().WriteNow(now);

        var failed = ReadStatus().GetProperty("failed").EnumerateArray().ToList();
        Assert.Equal(StatusExporter.MaxFailed, failed.Count);
        Assert.Equal("wf0", failed[0].GetProperty("workflow").GetString());
        Assert.Equal("wf19", failed[^1].GetProperty("workflow").GetString());
        Assert.DoesNotContain(failed, f => f.GetProperty("workflow").GetString() == "too-old");
    }

    [Fact]
    public void Running_now_comes_from_the_active_runs_of_this_profile()
    {
        var now = _fx.Clock.GetUtcNow();
        _active.Add(ExportsFixture.Run(ExecutionStatus.Running, now.AddMinutes(-2), workflowId: "busy"));
        _active.Add(ExportsFixture.Run(ExecutionStatus.Running, now.AddMinutes(-2), profile: ProfileId.Personal, workflowId: "other"));

        Exporter().WriteNow(now);

        var running = Assert.Single(ReadStatus().GetProperty("runningNow").EnumerateArray());
        Assert.Equal("busy", running.GetProperty("workflow").GetString());
        Assert.Equal("2026-10-23T11:57:55-07:00", running.GetProperty("startedAt").GetString());
    }

    [Fact]
    public void Scheduled_today_lists_the_remaining_occurrences_of_the_local_day()
    {
        var profile = _fx.WorkProfile();
        var workflow = TestSupport.LoadValid(_fx.Registry, profile, $$"""
            {
              "schemaVersion": 2, "id": "briefing", "name": "Morning briefing", "profileId": "work",
              "trigger": { "type": "schedule", "at": ["08:00", "13:30", "17:00"], "timeZone": "{{ExportsFixture.Zone.Id}}" },
              "steps": [ { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.invalid/" } } ]
            }
            """);
        _workflows.Add(new ProfileWorkflows(profile, [workflow], []));

        Exporter(profile).WriteNow(_fx.Clock.GetUtcNow());

        var scheduled = ReadStatus().GetProperty("scheduledToday").EnumerateArray().Select(s => s.GetProperty("at").GetString()).ToList();
        Assert.Equal(["2026-10-23T13:30:00-07:00", "2026-10-23T17:00:00-07:00"], scheduled);
    }

    [Fact]
    public void Missing_OneDrive_root_skips_without_creating_it()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);
        using var history = fx.OpenHistory();
        var exporter = new StatusExporter(fx.WorkProfile(), fx.Paths, history, () => [], () => [], fx.Clock);
        var changes = new List<StatusExportState>();
        exporter.Changed += (_, s) => changes.Add(s);

        var first = exporter.WriteNow(fx.Clock.GetUtcNow());
        exporter.WriteNow(fx.Clock.GetUtcNow());

        Assert.Equal(ExportOutcome.Skipped, first.Outcome);
        Assert.Equal(ExportFolderState.FolderMissing, first.Folder);
        Assert.False(Directory.Exists(fx.WorkOneDrive));
        Assert.Single(changes); // reported once, not on every attempt
    }

    [Fact]
    public void Disabled_profile_writes_nothing()
    {
        var personal = _fx.LoadCatalog().Get(ProfileId.Personal);

        var state = Exporter(personal).WriteNow(_fx.Clock.GetUtcNow());

        Assert.Equal(ExportOutcome.Disabled, state.Outcome);
        Assert.Empty(ExportsFixture.FilesUnder(_fx.PersonalOneDrive));
        Assert.Empty(ExportsFixture.FilesUnder(_fx.WorkOneDrive));
    }

    [Fact]
    public void Status_switched_off_writes_nothing()
    {
        var profile = _fx.Profile(ProfileId.Work, new ExportSettings(_fx.WorkFolder, Status: false, Alerts: true, Carryovers: true));

        Assert.Equal(ExportOutcome.Disabled, Exporter(profile).WriteNow(_fx.Clock.GetUtcNow()).Outcome);
        Assert.False(File.Exists(StatusFile));
    }

    [Fact]
    public void Tick_writes_first_then_every_15_minutes()
    {
        var exporter = Exporter();

        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
    }

    [Fact]
    public void Requests_are_debounced_to_one_write_per_30_seconds()
    {
        var exporter = Exporter();
        exporter.Tick(_fx.Clock.GetUtcNow());

        _fx.Clock.Advance(TimeSpan.FromSeconds(5));
        exporter.RequestSoon(_fx.Clock.GetUtcNow());
        exporter.RequestSoon(_fx.Clock.GetUtcNow());
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));

        _fx.Clock.Advance(TimeSpan.FromSeconds(25));
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));

        _fx.Clock.Advance(TimeSpan.FromMinutes(2));
        exporter.RequestSoon(_fx.Clock.GetUtcNow());
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
    }

    [Fact]
    public void A_request_made_while_a_write_is_being_built_gets_its_own_write_after_the_debounce()
    {
        StatusExporter? exporter = null;
        var requestDuringBuild = false;
        IReadOnlyList<ExecutionSnapshot> Active()
        {
            if (requestDuringBuild)
            {
                // A run ends on an engine thread while the loop thread is inside BuildJson.
                requestDuringBuild = false;
                exporter!.RequestSoon(_fx.Clock.GetUtcNow());
            }

            return [];
        }

        exporter = new StatusExporter(_fx.WorkProfile(), _fx.Paths, _history, Active, () => _workflows, _fx.Clock, "9.9.9");
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        exporter.RequestSoon(_fx.Clock.GetUtcNow());
        requestDuringBuild = true;

        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        Assert.False(requestDuringBuild);

        _fx.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));
    }

    [Fact]
    public void A_failed_write_is_retried_after_30_seconds_and_reported_once_per_message()
    {
        var exporter = Exporter();
        var changes = new List<StatusExportState>();
        exporter.Changed += (_, s) => changes.Add(s);
        Directory.CreateDirectory(StatusFile); // a folder where the file should go: every move fails

        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        Assert.Equal(ExportOutcome.Failed, exporter.State.Outcome);
        _fx.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(exporter.Tick(_fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));
        Assert.Single(changes); // the same failure twice is one change

        Directory.Delete(StatusFile);
        _fx.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(exporter.Tick(_fx.Clock.GetUtcNow()));

        Assert.Equal(ExportOutcome.Written, exporter.State.Outcome);
        Assert.Equal(2, changes.Count);
        Assert.True(File.Exists(StatusFile));
    }

    [Fact]
    public void Every_successful_write_raises_Changed_so_Settings_shows_the_latest_time()
    {
        var exporter = Exporter();
        var changes = new List<StatusExportState>();
        exporter.Changed += (_, s) => changes.Add(s);

        exporter.WriteNow(_fx.Clock.GetUtcNow());
        _fx.Clock.Advance(TimeSpan.FromMinutes(15));
        exporter.WriteNow(_fx.Clock.GetUtcNow());

        Assert.Equal(2, changes.Count);
        Assert.Equal(_fx.Clock.GetUtcNow(), changes[^1].LastWrittenAt);
    }

    [Fact]
    public void The_other_profiles_needs_attention_backlog_does_not_hide_this_profiles_runs()
    {
        var now = _fx.Clock.GetUtcNow();
        var work = ExportsFixture.Run(ExecutionStatus.NeedsAttention, now.AddHours(-5), workflowId: "work-run");
        _history.SaveExecution(work);
        for (var i = 0; i < StatusExporter.MaxListed; i++)
        {
            _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.NeedsAttention, now.AddMinutes(-i), profile: ProfileId.Personal, workflowId: $"personal{i}"));
        }

        Exporter().WriteNow(now);

        var needs = Assert.Single(ReadStatus().GetProperty("needsAttention").EnumerateArray());
        Assert.Equal(work.Id.ToString("D"), needs.GetProperty("runId").GetString());
    }

    [Fact]
    public void A_write_failure_is_reported_not_thrown()
    {
        Directory.CreateDirectory(StatusFile); // a folder where the file should go: the move fails

        var state = Exporter().WriteNow(_fx.Clock.GetUtcNow());

        Assert.Equal(ExportOutcome.Failed, state.Outcome);
        Assert.Empty(Directory.GetFiles(_fx.WorkFolder));
    }
}
