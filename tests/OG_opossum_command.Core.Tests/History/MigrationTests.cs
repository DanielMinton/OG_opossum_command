using Microsoft.Data.Sqlite;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.History;

/// <summary>Spec §18 "Update": configuration and history survive a version change. The fixture is a hand-written v1 database.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly TempDataRoot _root = new();

    private void CreateV1Database()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "data-v1.sql"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _root.Paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_v1_database_migrates_to_the_latest_schema_and_keeps_its_rows()
    {
        CreateV1Database();

        using var store = SqliteHistoryStore.Open(_root.Paths.DatabasePath);

        Assert.Equal(2, Migrations.Latest);
        Assert.Equal([1, 2], store.GetAppliedMigrations());
        Assert.Equal("personal", store.GetSetting("ui.activeProfile"));

        var completed = store.GetExecution(Guid.Parse("11111111-1111-1111-1111-111111111111"))!;
        Assert.Equal(ExecutionStatus.Completed, completed.Status);
        Assert.Equal("Start Workday", completed.WorkflowName);
        Assert.Equal(string.Empty, completed.WorkflowHash);
        Assert.Equal(TriggerSources.Manual, completed.TriggerSource);
        Assert.Null(completed.ScheduledFor);
        Assert.Null(completed.ResumedFrom);
        Assert.Null(completed.Recovery);
        var step = Assert.Single(completed.Steps);
        Assert.Equal("Opened outlook.office.com", step.Message);
        var evt = Assert.Single(store.GetEvents(completed.Id));
        Assert.Equal("execution.completed", evt.EventType);
        Assert.Equal(2000, evt.DurationMs);

        var trigger = Assert.Single(store.GetTriggers());
        Assert.Equal("work/start-workday/manual", trigger.Id);
        Assert.True(trigger.Enabled);
        Assert.Equal(string.Empty, trigger.ConfigHash);
        Assert.Null(trigger.NextDueAt);
        Assert.Null(trigger.LastFiredFor);

        // Writing with the new columns works on the migrated file.
        store.UpsertTrigger(trigger with { NextDueAt = DateTimeOffset.UtcNow, LastOutcome = "probe" });
        Assert.Equal("probe", store.GetTrigger(trigger.Id)!.LastOutcome);
    }

    [Fact]
    public void Opening_a_v1_database_through_the_host_reconciles_its_active_run_and_cleans_stale_trigger_rows()
    {
        CreateV1Database();

        using var host = CoreHost.Open(_root.Paths, new RecordingLaunchers().Set, _root.EmptyExamplesRoot);

        Assert.Equal(1, host.InterruptedOnStartup);
        var run = host.History.GetExecution(Guid.Parse("22222222-2222-2222-2222-222222222222"))!;
        // The workflow file does not exist in this data root, so recovery cannot check it: Needs attention, not a crash.
        Assert.Equal(ExecutionStatus.NeedsAttention, run.Status);
        Assert.Equal([StepStatus.Completed, StepStatus.Interrupted], run.Steps.Select(s => s.Status));
        Assert.Empty(host.Scheduler.Triggers); // the v1 "manual" trigger row is not a schedule this version knows
        Assert.Equal(2, host.History.SchemaVersion);
        Assert.Equal(ProfileId.Personal, ProfileIdExtensions.TryParseJsonValue(host.History.GetSetting(SettingKeys.ActiveProfile), out var profile) ? profile : ProfileId.Work);
    }

    [Fact]
    public void Migrating_twice_is_a_no_op()
    {
        CreateV1Database();
        using (SqliteHistoryStore.Open(_root.Paths.DatabasePath))
        {
        }

        using var again = SqliteHistoryStore.Open(_root.Paths.DatabasePath);

        Assert.Equal([1, 2], again.GetAppliedMigrations());
    }

    public void Dispose() => _root.Dispose();
}
