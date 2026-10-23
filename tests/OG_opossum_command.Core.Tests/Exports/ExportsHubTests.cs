using System.Text.Json;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Recovery;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Exports;

/// <summary>The hub wired into <see cref="CoreHost"/>: engine events, recovery decisions, timers, clean exit.</summary>
public sealed class ExportsHubTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ExportsFixture _fx = new();
    private readonly RecordingLaunchers _launchers = new();

    public ExportsHubTests()
    {
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 3);
    }

    public void Dispose() => _fx.Dispose();

    private string Outbox => Path.Combine(_fx.WorkFolder, AppPaths.OutboxFolderName);

    private string StatusFile => Path.Combine(_fx.WorkFolder, AppPaths.StatusFileName);

    private CoreHost Open(IAppPaths? paths = null) =>
        CoreHost.Open(paths ?? _fx.Paths, _launchers.Set, _fx.Root.EmptyExamplesRoot, new CoreHostOptions(StartSchedulerLoop: false, StartExportsLoop: false), _fx.Clock);

    private void WriteFailingWorkflow(ExportsFixture? fx = null)
    {
        var folder = (fx ?? _fx).Paths.GetProfileWorkflowsDirectory(ProfileId.Work);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "failing.json"), """
            {
              "schemaVersion": 1, "id": "failing", "name": "Failing job", "profileId": "work",
              "trigger": { "type": "manual" },
              "steps": [ { "id": "launch", "action": "windows.process.launch", "inputs": { "path": "%SystemRoot%\\System32\\PING.EXE", "arguments": ["-n", "1", "localhost"], "waitForExit": true } } ]
            }
            """);
    }

    private static JsonElement[] FailedIn(string statusFile)
    {
        using var status = JsonDocument.Parse(File.ReadAllText(statusFile));
        return status.RootElement.GetProperty("failed").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task A_failed_run_writes_an_alert_and_a_debounced_status_and_a_final_status_on_exit()
    {
        WriteFailingWorkflow();
        using (var host = Open())
        {
            host.History.SetSetting(SettingKeys.QuietMode, "true"); // quiet mode never suppresses the outbox

            // Use up the first-pass write, so the next write can only come from the run ending.
            host.Exports.Tick(_fx.Clock.GetUtcNow());
            Assert.Empty(FailedIn(StatusFile));
            _fx.Clock.Advance(TimeSpan.FromSeconds(10));

            var started = host.Run(ProfileId.Work, "failing");
            var finished = await host.Engine.WaitForCompletionAsync(started.Execution.Id).WaitAsync(Timeout);
            Assert.Equal(ExecutionStatus.Failed, finished.Status);

            Assert.True(await TestSupport.WaitUntilAsync(() => Directory.Exists(Outbox) && Directory.GetFiles(Outbox).Length == 1, Timeout));
            using (var alert = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Outbox)[0])))
            {
                Assert.Equal(AlertEvents.RunFailed, alert.RootElement.GetProperty("event").GetString());
                Assert.Equal("failing", alert.RootElement.GetProperty("workflow").GetString());
                Assert.Equal("launch", alert.RootElement.GetProperty("step").GetString());
            }

            // The run's end asked for a write; the debounce (30 s since the last write) holds it until then.
            host.Exports.Tick(_fx.Clock.GetUtcNow());
            Assert.Empty(FailedIn(StatusFile));
            _fx.Clock.Advance(TimeSpan.FromSeconds(19));
            host.Exports.Tick(_fx.Clock.GetUtcNow());
            Assert.Empty(FailedIn(StatusFile));
            _fx.Clock.Advance(TimeSpan.FromSeconds(1));
            host.Exports.Tick(_fx.Clock.GetUtcNow()); // 30 s after the first write, far short of 15 minutes
            var failed = Assert.Single(FailedIn(StatusFile));
            Assert.Equal(finished.Id.ToString("D"), failed.GetProperty("runId").GetString());

            Assert.Equal(ExportOutcome.Written, host.Exports.GetStatus(ProfileId.Work).Status.Outcome);
            Assert.Equal(AlertEvents.RunFailed, host.Exports.GetStatus(ProfileId.Work).Alerts.LastEvent);
            File.Delete(StatusFile);
            _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Dispose after a clean start writes one last status.json.
        Assert.True(File.Exists(StatusFile));
        Assert.Empty(ExportsFixture.FilesUnder(_fx.PersonalOneDrive));
    }

    [Fact]
    public void Recovery_decisions_raise_needs_attention_alerts()
    {
        using var host = Open();
        var run = ExportsFixture.Run(ExecutionStatus.NeedsAttention, _fx.Clock.GetUtcNow(), workflowId: "send-mail", error: "Step 'send' may already have run. Resume or Dismiss.");
        host.History.SaveExecution(run);

        host.Exports.ObserveRecovery([new RecoveryDecision(run.Id, ProfileId.Work, "send-mail", "Send mail", RecoveryStates.NeedsAttention, "may already have run", null)]);

        var file = Assert.Single(Directory.GetFiles(Outbox));
        using var alert = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(AlertEvents.RunNeedsAttention, alert.RootElement.GetProperty("event").GetString());
        Assert.Equal(run.Id.ToString("D"), alert.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public void Auto_resumed_decisions_raise_nothing()
    {
        using var host = Open();

        host.Exports.ObserveRecovery([new RecoveryDecision(Guid.NewGuid(), ProfileId.Work, "x", "X", RecoveryStates.AutoResumed, "fine", Guid.NewGuid())]);

        Assert.False(Directory.Exists(Outbox));
    }

    [Fact]
    public void Without_OneDrive_nothing_is_written_anywhere_and_Settings_says_why()
    {
        var root = Path.Combine(_fx.Root.Root, "smoke-like");
        var paths = AppPaths.ForRoot(root);
        WriteFailingWorkflow();
        using (var host = Open(paths))
        {
            host.Exports.Tick(_fx.Clock.GetUtcNow());
            var work = host.Exports.GetStatus(ProfileId.Work);
            Assert.Equal(ExportOutcome.Disabled, work.Status.Outcome);
            Assert.Contains("OneDriveCommercial", work.Settings.DisabledReason, StringComparison.Ordinal);
            Assert.Equal(CarryoverStatus.Disabled, host.Exports.Carryovers.Read(host.Exports.GetProfile(ProfileId.Work)).Status);
            Assert.Equal(ExportOutcome.Disabled, host.Exports.GetStatus(ProfileId.Personal).Status.Outcome);
        }

        Assert.Empty(ExportsFixture.FilesUnder(_fx.WorkOneDrive));
        Assert.Empty(ExportsFixture.FilesUnder(_fx.PersonalOneDrive));
    }

    [Fact]
    public void Carryovers_are_read_through_the_hub_and_reported_in_status()
    {
        Directory.CreateDirectory(_fx.WorkFolder);
        File.WriteAllText(Path.Combine(_fx.WorkFolder, AppPaths.CarryoversFileName), CarryoverFileParserTests.Sample);
        using var host = Open();
        var changed = new List<ProfileId>();
        host.Exports.Changed += (_, p) => changed.Add(p);

        var result = host.Exports.Carryovers.Read(host.Exports.GetProfile(ProfileId.Work));

        Assert.Equal(CarryoverStatus.Loaded, result.Status);
        Assert.Same(result, host.Exports.GetStatus(ProfileId.Work).Carryovers);
        Assert.Contains(ProfileId.Work, changed);
    }

    [Fact]
    public async Task An_alert_skipped_while_OneDrive_is_missing_is_written_by_a_later_Tick()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);
        WriteFailingWorkflow(fx);
        using var host = CoreHost.Open(fx.Paths, _launchers.Set, fx.Root.EmptyExamplesRoot, new CoreHostOptions(StartSchedulerLoop: false, StartExportsLoop: false), fx.Clock);
        var started = host.Run(ProfileId.Work, "failing");
        await host.Engine.WaitForCompletionAsync(started.Execution.Id).WaitAsync(Timeout);
        Assert.True(await TestSupport.WaitUntilAsync(() => host.Exports.GetStatus(ProfileId.Work).Alerts.Outcome == ExportOutcome.Skipped, Timeout));

        // OneDrive finishes signing in; the run is never reported again, so only the hub's retry can write the alert.
        Directory.CreateDirectory(fx.WorkOneDrive);
        host.Exports.Tick(fx.Clock.GetUtcNow());

        var outbox = Path.Combine(fx.WorkFolder, AppPaths.OutboxFolderName);
        var file = Assert.Single(Directory.GetFiles(outbox));
        using var alert = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(AlertEvents.RunFailed, alert.RootElement.GetProperty("event").GetString());
        Assert.Equal(started.Execution.Id.ToString("D"), alert.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task Stop_ends_the_loop_before_the_final_write()
    {
        using (var host = CoreHost.Open(_fx.Paths, _launchers.Set, _fx.Root.EmptyExamplesRoot, new CoreHostOptions(StartSchedulerLoop: false, StartExportsLoop: true), _fx.Clock))
        {
            Assert.True(await TestSupport.WaitUntilAsync(() => File.Exists(StatusFile), Timeout)); // the loop's first pass

            Assert.True(host.Exports.Stop());
            Assert.True(host.Exports.Stop()); // idempotent
            File.Delete(StatusFile);
            _fx.Clock.Advance(TimeSpan.FromMinutes(16));
            await Task.Delay(ExportsHub.LoopInterval + TimeSpan.FromMilliseconds(500));
            Assert.False(File.Exists(StatusFile)); // no pass ran after Stop
        }

        Assert.True(File.Exists(StatusFile)); // the final write still happens on dispose
    }

    [Fact]
    public void Housekeep_sweeps_orphaned_temporary_files_from_the_exports_folder()
    {
        Directory.CreateDirectory(_fx.WorkFolder);
        var now = _fx.Clock.GetUtcNow();
        var stale = Path.Combine(_fx.WorkFolder, $".{AppPaths.StatusFileName}.{Guid.NewGuid():N}{AtomicFile.TempSuffix}");
        var fresh = Path.Combine(_fx.WorkFolder, $".{AppPaths.StatusFileName}.{Guid.NewGuid():N}{AtomicFile.TempSuffix}");
        File.WriteAllText(stale, "{");
        File.WriteAllText(fresh, "{");
        File.SetLastWriteTimeUtc(stale, now.UtcDateTime.AddHours(-3));
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime);
        using var host = Open();

        host.Exports.Housekeep(now);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Routine_writes_refresh_Settings_but_are_not_logged()
    {
        using var host = Open();
        var changed = 0;
        host.Exports.Changed += (_, p) => changed += p == ProfileId.Work ? 1 : 0;

        host.Exports.Tick(_fx.Clock.GetUtcNow());
        _fx.Clock.Advance(StatusExporter.Interval);
        host.Exports.Tick(_fx.Clock.GetUtcNow());

        Assert.Equal(2, changed);
        var log = File.ReadAllLines(Path.Combine(_fx.Paths.LogsDirectory, ExportsHub.LogFileName));
        Assert.Single(log, l => l.Contains("[Work] status.json: Written", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_change_is_logged_once()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);
        using var host = CoreHost.Open(fx.Paths, _launchers.Set, fx.Root.EmptyExamplesRoot, new CoreHostOptions(StartSchedulerLoop: false, StartExportsLoop: false), fx.Clock);

        host.Exports.Tick(fx.Clock.GetUtcNow());
        fx.Clock.Advance(TimeSpan.FromMinutes(16));
        host.Exports.Tick(fx.Clock.GetUtcNow());

        var log = File.ReadAllLines(Path.Combine(fx.Paths.LogsDirectory, ExportsHub.LogFileName));
        Assert.Single(log, l => l.Contains("[Work] status.json: Skipped", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fx.WorkOneDrive));
    }
}
