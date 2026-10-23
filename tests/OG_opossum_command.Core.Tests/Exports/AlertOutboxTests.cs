using System.Text.Json;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Tests.Exports;

public sealed class AlertOutboxTests : IDisposable
{
    private static readonly Guid FixedId = Guid.Parse("0123456789abcdef0123456789abcdef");
    private readonly ExportsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private string Outbox => Path.Combine(_fx.WorkFolder, AppPaths.OutboxFolderName);

    private AlertOutbox NewOutbox(Profile? profile = null, Func<Guid>? ids = null) => new(profile ?? _fx.WorkProfile(), _fx.Paths, _fx.Clock, ids);

    private static JsonElement ReadSingle(string folder)
    {
        var file = Assert.Single(Directory.GetFiles(folder));
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_failed_run_writes_one_v1_alert_file()
    {
        var outbox = NewOutbox(ids: () => FixedId);
        var run = ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow(), workflowId: "send-report", workflowName: "Send report", stepMessage: "Exit code 2 (password=hunter2) in C:\\Users\\Me\\x.cmd");

        Assert.Equal(AlertWriteOutcome.Written, outbox.ObserveExecution(run));

        var file = Assert.Single(Directory.GetFiles(Outbox));
        Assert.Equal("20261023-190000-0123456789abcdef0123456789abcdef.json", Path.GetFileName(file));
        Assert.True(AlertOutbox.IsAlertFileName(Path.GetFileName(file)));
        var json = ReadSingle(Outbox);
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AlertEvents.RunFailed, json.GetProperty("event").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", json.GetProperty("eventId").GetString());
        Assert.Equal("2026-10-23T12:00:00-07:00", json.GetProperty("at").GetString());
        Assert.Equal("Work", json.GetProperty("profile").GetString());
        Assert.Equal("send-report", json.GetProperty("workflow").GetString());
        Assert.Equal("Send report", json.GetProperty("workflowName").GetString());
        Assert.Equal(run.Id.ToString("D"), json.GetProperty("runId").GetString());
        Assert.Equal("first", json.GetProperty("step").GetString());
        var message = json.GetProperty("message").GetString()!;
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", message, StringComparison.Ordinal);
        Assert.True(message.Length <= ExportText.MaxFieldChars);

        // The temporary file lives beside outbox\ and is gone.
        Assert.Equal([Path.Combine(AppPaths.OutboxFolderName, Path.GetFileName(file))], ExportsFixture.FilesUnder(_fx.WorkFolder));
        Assert.Equal(ExportOutcome.Written, outbox.State.Outcome);
        Assert.Equal(AlertEvents.RunFailed, outbox.State.LastEvent);
    }

    [Fact]
    public void Non_failed_and_other_profile_snapshots_are_ignored()
    {
        var outbox = NewOutbox();
        var now = _fx.Clock.GetUtcNow();

        Assert.Null(outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now)));
        Assert.Null(outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Running, now)));
        Assert.Null(outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now, profile: ProfileId.Personal)));
        Assert.False(Directory.Exists(Outbox));
    }

    [Fact]
    public void The_same_run_seen_twice_alerts_once()
    {
        var outbox = NewOutbox();
        var run = ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow());

        outbox.ObserveExecution(run);
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AlertWriteOutcome.Deduplicated, outbox.ObserveExecution(run));

        Assert.Single(Directory.GetFiles(Outbox));
    }

    [Fact]
    public void One_alert_per_workflow_and_event_per_30_minutes()
    {
        var outbox = NewOutbox();

        Assert.Equal(AlertWriteOutcome.Written, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));
        _fx.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(AlertWriteOutcome.Deduplicated, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));
        Assert.Equal(AlertWriteOutcome.Written, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow(), workflowId: "another")));
        Assert.Equal(AlertWriteOutcome.Written, outbox.ObserveNeedsAttention(ExportsFixture.Run(ExecutionStatus.NeedsAttention, _fx.Clock.GetUtcNow())));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AlertWriteOutcome.Written, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));

        Assert.Equal(4, Directory.GetFiles(Outbox).Length);
    }

    [Fact]
    public void Needs_attention_names_the_interrupted_step()
    {
        var outbox = NewOutbox();
        var run = ExportsFixture.Run(ExecutionStatus.NeedsAttention, _fx.Clock.GetUtcNow(), error: "A side effect may already have happened. Resume or Dismiss.");

        outbox.ObserveNeedsAttention(run);

        var json = ReadSingle(Outbox);
        Assert.Equal(AlertEvents.RunNeedsAttention, json.GetProperty("event").GetString());
        Assert.Equal("first", json.GetProperty("step").GetString());
        Assert.Equal("A side effect may already have happened. Resume or Dismiss.", json.GetProperty("message").GetString());
    }

    [Fact]
    public void Overrun_alerts_once_when_a_step_runs_5_minutes_past_its_timeout()
    {
        var outbox = NewOutbox();
        var started = _fx.Clock.GetUtcNow();
        var run = ExportsFixture.Run(ExecutionStatus.Running, started.AddSeconds(5));
        TimeSpan? Timeout(ExecutionSnapshot r, StepSnapshot s) => TimeSpan.FromMinutes(1);

        _fx.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, outbox.CheckOverruns([run], Timeout, _fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, outbox.CheckOverruns([run], Timeout, _fx.Clock.GetUtcNow()));
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, outbox.CheckOverruns([run], Timeout, _fx.Clock.GetUtcNow()));

        var json = ReadSingle(Outbox);
        Assert.Equal(AlertEvents.RunOverrun, json.GetProperty("event").GetString());
        Assert.Equal("first", json.GetProperty("step").GetString());
    }

    [Fact]
    public void Alerts_switched_off_write_nothing()
    {
        var outbox = NewOutbox(_fx.Profile(ProfileId.Work, new ExportSettings(_fx.WorkFolder, Status: true, Alerts: false, Carryovers: true)));

        Assert.Equal(AlertWriteOutcome.Disabled, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));
        Assert.False(Directory.Exists(Outbox));
    }

    [Fact]
    public void Personal_default_and_broken_policy_write_nothing()
    {
        _fx.WritePolicy(ProfileId.Work, "{ not json");
        var catalog = _fx.LoadCatalog();

        foreach (var profile in catalog.Profiles)
        {
            var outbox = new AlertOutbox(profile, _fx.Paths, _fx.Clock);
            Assert.Equal(AlertWriteOutcome.Disabled, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow(), profile: profile.Id)));
        }

        Assert.Empty(ExportsFixture.FilesUnder(_fx.WorkOneDrive));
        Assert.Empty(ExportsFixture.FilesUnder(_fx.PersonalOneDrive));
    }

    [Fact]
    public void A_skipped_alert_is_queued_and_written_by_RetryPending_with_its_original_time()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);
        var outbox = new AlertOutbox(fx.WorkProfile(), fx.Paths, fx.Clock);
        var run = ExportsFixture.Run(ExecutionStatus.Failed, fx.Clock.GetUtcNow());

        // The engine reports a run's end once; a second sighting does not write or queue a second alert.
        Assert.Equal(AlertWriteOutcome.Skipped, outbox.ObserveExecution(run));
        Assert.Equal(AlertWriteOutcome.Deduplicated, outbox.ObserveExecution(run));
        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, outbox.RetryPending(fx.Clock.GetUtcNow()));
        Assert.False(Directory.Exists(fx.WorkOneDrive));

        Directory.CreateDirectory(fx.WorkOneDrive);
        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, outbox.RetryPending(fx.Clock.GetUtcNow()));
        Assert.Equal(0, outbox.RetryPending(fx.Clock.GetUtcNow()));

        var file = Assert.Single(Directory.GetFiles(Path.Combine(fx.WorkFolder, AppPaths.OutboxFolderName)));
        using var alert = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("2026-10-23T12:00:00-07:00", alert.RootElement.GetProperty("at").GetString());
        Assert.Equal(run.Id.ToString("D"), alert.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public void A_failed_write_is_retried_too()
    {
        Directory.CreateDirectory(_fx.WorkFolder);
        File.WriteAllText(Outbox, "a file where outbox\\ should be"); // CreateDirectory fails
        var outbox = NewOutbox();

        Assert.Equal(AlertWriteOutcome.Failed, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));

        File.Delete(Outbox);
        Assert.Equal(1, outbox.RetryPending(_fx.Clock.GetUtcNow().AddSeconds(30)));
        Assert.Single(Directory.GetFiles(Outbox));
    }

    [Fact]
    public void A_queued_alert_older_than_the_dedupe_window_is_dropped()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);
        var outbox = new AlertOutbox(fx.WorkProfile(), fx.Paths, fx.Clock);
        Assert.Equal(AlertWriteOutcome.Skipped, outbox.ObserveNeedsAttention(ExportsFixture.Run(ExecutionStatus.NeedsAttention, fx.Clock.GetUtcNow())));

        Directory.CreateDirectory(fx.WorkOneDrive);
        fx.Clock.Advance(AlertOutbox.DedupeWindow + TimeSpan.FromMinutes(1));

        Assert.Equal(0, outbox.RetryPending(fx.Clock.GetUtcNow()));
        Assert.Empty(ExportsFixture.FilesUnder(fx.WorkOneDrive));
    }

    [Fact]
    public void Cross_OneDrive_folder_is_refused()
    {
        var personalIntoWork = _fx.Profile(ProfileId.Personal, new ExportSettings(_fx.WorkFolder, true, true, true));
        var outbox = new AlertOutbox(personalIntoWork, _fx.Paths, _fx.Clock);

        Assert.Equal(AlertWriteOutcome.Skipped, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow(), profile: ProfileId.Personal)));
        Assert.Equal(ExportFolderState.Refused, outbox.State.Folder);
        Assert.Empty(ExportsFixture.FilesUnder(_fx.WorkOneDrive));
    }

    [Fact]
    public void Prune_keeps_200_newest_and_nothing_older_than_7_days_and_touches_only_own_files()
    {
        Directory.CreateDirectory(Outbox);
        var now = _fx.Clock.GetUtcNow();
        for (var i = 0; i < 210; i++)
        {
            File.WriteAllText(Path.Combine(Outbox, AlertOutbox.FileNameFor(now.AddMinutes(-i), Guid.NewGuid())), "{}");
        }

        var old = AlertOutbox.FileNameFor(now.AddDays(-8), Guid.NewGuid());
        File.WriteAllText(Path.Combine(Outbox, old), "{}");
        var foreign = new[] { "notes.json", "readme.txt", "20261023-190000-NOTAGUID.json", "20200101-000000-0123456789abcdef0123456789abcdef.json.bak" };
        foreach (var name in foreign)
        {
            File.WriteAllText(Path.Combine(Outbox, name), "x");
        }

        var nested = Path.Combine(Outbox, "archive");
        Directory.CreateDirectory(nested);
        var nestedOld = Path.Combine(nested, AlertOutbox.FileNameFor(now.AddDays(-30), Guid.NewGuid()));
        File.WriteAllText(nestedOld, "{}");
        var outside = Path.Combine(_fx.WorkFolder, AlertOutbox.FileNameFor(now.AddDays(-30), Guid.NewGuid()));
        File.WriteAllText(outside, "{}");

        var deleted = NewOutbox().Prune(now);

        Assert.Equal(11, deleted);
        var remaining = Directory.GetFiles(Outbox).Select(Path.GetFileName).ToList();
        Assert.Equal(AlertOutbox.MaxFiles, remaining.Count(n => AlertOutbox.IsAlertFileName(n!)));
        Assert.DoesNotContain(old, remaining);
        Assert.Contains(AlertOutbox.FileNameFor(now, Guid.Empty)[..15], remaining.Select(n => n![..15]));
        foreach (var name in foreign)
        {
            Assert.Contains(name, remaining);
        }

        Assert.True(File.Exists(nestedOld));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Prune_does_not_follow_a_linked_outbox()
    {
        var target = Path.Combine(_fx.Root.Root, "elsewhere");
        Directory.CreateDirectory(target);
        var victim = Path.Combine(target, AlertOutbox.FileNameFor(_fx.Clock.GetUtcNow().AddDays(-30), Guid.NewGuid()));
        File.WriteAllText(victim, "{}");
        Directory.CreateDirectory(_fx.WorkFolder);
        try
        {
            Directory.CreateSymbolicLink(Outbox, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode for a standard user; nothing to check without one.
            return;
        }

        var outbox = NewOutbox();
        Assert.Equal(0, outbox.Prune(_fx.Clock.GetUtcNow()));
        Assert.Equal(AlertWriteOutcome.Skipped, outbox.ObserveExecution(ExportsFixture.Run(ExecutionStatus.Failed, _fx.Clock.GetUtcNow())));
        Assert.True(File.Exists(victim));
        Assert.Single(Directory.GetFiles(target));
    }

    [Fact]
    public void Prune_skips_linked_files()
    {
        Directory.CreateDirectory(Outbox);
        var target = Path.Combine(_fx.Root.Root, "victim.json");
        File.WriteAllText(target, "keep me");
        var link = Path.Combine(Outbox, AlertOutbox.FileNameFor(_fx.Clock.GetUtcNow().AddDays(-30), Guid.NewGuid()));
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(0, NewOutbox().Prune(_fx.Clock.GetUtcNow()));
        Assert.True(File.Exists(link));
        Assert.Equal("keep me", File.ReadAllText(target));
    }
}
