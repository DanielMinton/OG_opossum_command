using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Notifications;

public sealed class NotificationServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly SqliteHistoryStore _history;
    private readonly WorkflowEngine _engine;
    private readonly RecordingNotificationSink _sink = new();
    private readonly NotificationService _service;
    private readonly Profile _work;

    public NotificationServiceTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        _engine = new WorkflowEngine(_registry, _history);
        _service = new NotificationService(_history, _sink, _engine, null);
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private const string Url = """
        { "schemaVersion": 1, "id": "ok", "name": "Okay", "profileId": "work", "trigger": { "type": "manual" },
          "steps": [ { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } } ] }
        """;

    private const string Failing = """
        { "schemaVersion": 1, "id": "bad", "name": "Broken", "profileId": "work", "trigger": { "type": "manual" },
          "steps": [ { "id": "a", "action": "windows.folder.open", "inputs": { "path": "C:\\this\\folder\\does\\not\\exist" } } ] }
        """;

    private async Task RunAsync(string json)
    {
        var started = _engine.Start(TestSupport.LoadValid(_registry, _work, json), _work);
        using var cts = new CancellationTokenSource(Timeout);
        await _engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
        Assert.True(await TestSupport.WaitUntilAsync(() => _sink.Shown.Count + _service.SuppressedCount >= 1, Timeout));
    }

    [Fact]
    public async Task Completed_runs_notify_at_Info_and_failed_runs_at_Error()
    {
        await RunAsync(Url);
        await RunAsync(Failing);

        Assert.Equal([NotificationLevel.Info, NotificationLevel.Error], _sink.Shown.Select(n => n.Level));
        Assert.Equal("Okay", _sink.Shown[0].Title);
        Assert.Contains("Broken", _sink.Shown[1].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_mode_suppresses_everything_below_Error_and_is_persisted()
    {
        _service.QuietMode = true;
        Assert.Equal("true", _history.GetSetting(SettingKeys.QuietMode));

        await RunAsync(Url);
        Assert.Empty(_sink.Shown);
        Assert.Equal(1, _service.SuppressedCount);

        await RunAsync(Failing);
        var shown = Assert.Single(_sink.Shown);
        Assert.Equal(NotificationLevel.Error, shown.Level);

        _service.QuietMode = false;
        Assert.True(_service.Notify(new Notification(NotificationLevel.Info, "t", "m")));
        Assert.Equal(2, _sink.Shown.Count);
    }

    public void Dispose()
    {
        _service.Dispose();
        _engine.Dispose();
        _history.Dispose();
        _root.Dispose();
    }
}
