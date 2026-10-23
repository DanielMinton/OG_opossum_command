using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Exports;

/// <summary>Values of the alert file's <c>event</c> field.</summary>
public static class AlertEvents
{
    /// <summary>A run ended Failed.</summary>
    public const string RunFailed = "run.failed";

    /// <summary>A run is waiting for Resume or Dismiss (crash recovery could not prove a side effect did not happen).</summary>
    public const string RunNeedsAttention = "run.needsAttention";

    /// <summary>A run's current step is still running <see cref="AlertOutbox.OverrunGrace"/> after its step timeout.</summary>
    public const string RunOverrun = "run.overrun";
}

/// <summary>What happened to one alert.</summary>
public enum AlertWriteOutcome
{
    /// <summary>A file was written into <c>outbox\</c>.</summary>
    Written,

    /// <summary>An alert for the same workflow and event was written less than <see cref="AlertOutbox.DedupeWindow"/> ago.</summary>
    Deduplicated,

    /// <summary>Alerts are off for the profile.</summary>
    Disabled,

    /// <summary>The folder is missing or refused.</summary>
    Skipped,

    /// <summary>Writing failed.</summary>
    Failed,
}

/// <summary>One alert as written.</summary>
public sealed record AlertRecord(string Event, Guid EventId, DateTimeOffset At, ProfileId Profile, string WorkflowId, string WorkflowName, Guid RunId, string? Step, string Message, string FileName);

/// <summary>What Settings shows about the alert outbox for one profile.</summary>
/// <param name="Outcome">How the last attempt ended.</param>
/// <param name="Folder">Folder state at the last attempt.</param>
/// <param name="Message">One readable line.</param>
/// <param name="LastAlertAt">When the last alert file was written, if ever.</param>
/// <param name="LastEvent">Its <see cref="AlertEvents"/> value.</param>
public sealed record AlertOutboxState(ExportOutcome Outcome, ExportFolderState Folder, string Message, DateTimeOffset? LastAlertAt, string? LastEvent)
{
    public static AlertOutboxState Initial { get; } = new(ExportOutcome.NotYet, ExportFolderState.Disabled, "No alert written yet.", null, null);
}

/// <summary>
/// The alert outbox (ADR-052), which replaces the blueprint's HTTP <c>webhook.post</c> because the app makes no network
/// calls: when a run of the profile fails, needs attention, or overruns its step timeout, one JSON file (schema v1) is
/// written atomically into <c>&lt;exports folder&gt;\outbox\</c>, where the user's Power Automate flow picks it up
/// ("When a file is created"), posts to Teams, and deletes it. In-memory dedupe allows one alert per workflow and event
/// per <see cref="DedupeWindow"/>; an alert whose write is skipped or fails waits in memory for
/// <see cref="RetryPending"/> (ADR-054). Quiet mode does not apply here (the flow applies quiet hours). The outbox is pruned
/// to <see cref="MaxFiles"/> files and <see cref="MaxAge"/>, touching only files that match the app's own name pattern,
/// never recursing and never following reparse points.
/// </summary>
public sealed partial class AlertOutbox
{
    /// <summary>Current schema of the alert files.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Most alert files kept in <c>outbox\</c>.</summary>
    public const int MaxFiles = 200;

    /// <summary>One alert per workflow and event per this window.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(30);

    /// <summary>Alert files older than this are pruned.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>How long past its timeout a step may still be running before <see cref="AlertEvents.RunOverrun"/>.</summary>
    public static readonly TimeSpan OverrunGrace = TimeSpan.FromMinutes(5);

    private const int MaxRemembered = 2000;

    /// <summary>Most alerts waiting for a retry after a skipped or failed write; the oldest is dropped beyond this.</summary>
    private const int MaxPending = 50;

    private readonly Profile _profile;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newId;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<PendingAlert> _pending = [];
    private AlertOutboxState _state = AlertOutboxState.Initial;

    /// <param name="profile">The profile whose runs raise alerts; its <see cref="Profile.Exports"/> decide whether and where.</param>
    /// <param name="paths">OneDrive roots for the cross-OneDrive guard.</param>
    /// <param name="time">Clock and local zone.</param>
    /// <param name="newEventId">Event ID source (tests); defaults to <see cref="Guid.NewGuid"/>.</param>
    public AlertOutbox(Profile profile, IAppPaths paths, TimeProvider? time = null, Func<Guid>? newEventId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(paths);
        _profile = profile;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _newId = newEventId ?? Guid.NewGuid;
    }

    /// <summary>Raised when <see cref="State"/> changes. Never throws into the caller.</summary>
    public event EventHandler<AlertOutboxState>? Changed;

    /// <summary>The profile.</summary>
    public ProfileId Profile => _profile.Id;

    /// <summary>Whether alerts are switched on for the profile.</summary>
    public bool IsEnabled => _profile.Exports.Alerts && _profile.Exports.Folder is not null;

    /// <summary>The last attempt's result, for Settings.</summary>
    public AlertOutboxState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raises <see cref="AlertEvents.RunFailed"/> the first time a snapshot of this profile's run shows it Failed. Other snapshots are ignored.</summary>
    public AlertWriteOutcome? ObserveExecution(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Profile != _profile.Id || execution.Status != ExecutionStatus.Failed)
        {
            return null;
        }

        var step = execution.Steps.Where(s => s.Status == StepStatus.Failed).OrderBy(s => s.Ordinal).FirstOrDefault();
        return RaiseOnce(AlertEvents.RunFailed, execution, step?.Id, step?.Message ?? execution.Error ?? "The run failed.", _time.GetUtcNow());
    }

    /// <summary>Raises <see cref="AlertEvents.RunNeedsAttention"/> once for a run of this profile that is waiting for Resume or Dismiss.</summary>
    public AlertWriteOutcome? ObserveNeedsAttention(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Profile != _profile.Id || execution.Status != ExecutionStatus.NeedsAttention)
        {
            return null;
        }

        var step = StatusExporter.UnfinishedStep(execution);
        return RaiseOnce(AlertEvents.RunNeedsAttention, execution, step?.Id, execution.Error ?? "The run needs attention: Resume or Dismiss in the app.", _time.GetUtcNow());
    }

    /// <summary>
    /// Raises <see cref="AlertEvents.RunOverrun"/> once per run whose running step has been running longer than its
    /// timeout plus <see cref="OverrunGrace"/> (the engine cancels at the timeout, so this means the step ignored it).
    /// </summary>
    /// <param name="active">Active runs (normally <see cref="WorkflowEngine.ActiveExecutions"/>).</param>
    /// <param name="timeoutOf">The effective timeout of a step (its own, else its action's default), or <c>null</c> when unknown.</param>
    /// <param name="now">The current instant.</param>
    public int CheckOverruns(IReadOnlyList<ExecutionSnapshot> active, Func<ExecutionSnapshot, StepSnapshot, TimeSpan?> timeoutOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(timeoutOf);
        var written = 0;
        foreach (var run in active.Where(r => r.Profile == _profile.Id && r.IsActive))
        {
            foreach (var step in run.Steps.Where(s => s.Status == StepStatus.Running && s.StartedAt is not null))
            {
                var timeout = timeoutOf(run, step);
                if (timeout is null)
                {
                    continue;
                }

                var elapsed = now - step.StartedAt!.Value;
                if (elapsed < timeout.Value + OverrunGrace)
                {
                    continue;
                }

                var message = string.Create(CultureInfo.InvariantCulture, $"Step '{step.Id}' has been running for {(int)elapsed.TotalMinutes} min, past its {Math.Max(1, (int)Math.Ceiling(timeout.Value.TotalMinutes))} min timeout. It may be stuck.");
                if (RaiseOnce(AlertEvents.RunOverrun, run, step.Id, message, now) == AlertWriteOutcome.Written)
                {
                    written++;
                }

                break;
            }
        }

        return written;
    }

    /// <summary>
    /// Writes one alert unless alerts are off, the folder is unusable, or one for the same workflow and event was written
    /// within <see cref="DedupeWindow"/>. Never throws for I/O problems.
    /// </summary>
    public AlertWriteOutcome Raise(string eventName, ExecutionSnapshot execution, string? stepId, string message, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(message);

        if (!_profile.Exports.Alerts)
        {
            SetState(new AlertOutboxState(ExportOutcome.Disabled, ExportFolderState.Disabled, _profile.Exports.DisabledReason ?? $"Alerts are switched off for the {_profile.DisplayName} profile.", State.LastAlertAt, State.LastEvent));
            return AlertWriteOutcome.Disabled;
        }

        var dedupeKey = $"{execution.WorkflowId}\n{eventName}";
        lock (_gate)
        {
            if (_lastWritten.TryGetValue(dedupeKey, out var last) && now - last < DedupeWindow && now >= last)
            {
                return AlertWriteOutcome.Deduplicated;
            }
        }

        var folder = ExportFolder.Resolve(_profile, _paths, createIfParentExists: true);
        if (!folder.IsReady)
        {
            var outcome = folder.State == ExportFolderState.Disabled ? ExportOutcome.Disabled : ExportOutcome.Skipped;
            SetState(new AlertOutboxState(outcome, folder.State, folder.Message, State.LastAlertAt, State.LastEvent));
            return folder.State == ExportFolderState.Disabled ? AlertWriteOutcome.Disabled : AlertWriteOutcome.Skipped;
        }

        try
        {
            var outbox = Path.Combine(folder.FullPath!, AppPaths.OutboxFolderName);
            if (IsLink(outbox))
            {
                SetState(new AlertOutboxState(ExportOutcome.Skipped, ExportFolderState.Refused, $"{outbox} is a symbolic link or junction; the app does not write through links.", State.LastAlertAt, State.LastEvent));
                return AlertWriteOutcome.Skipped;
            }

            Directory.CreateDirectory(outbox);
            var eventId = _newId();
            var record = new AlertRecord(
                eventName,
                eventId,
                now,
                _profile.Id,
                Scrub(execution.WorkflowId) ?? "unknown",
                Scrub(execution.WorkflowName) ?? "unknown",
                execution.Id,
                Scrub(stepId),
                Scrub(message) ?? eventName,
                FileNameFor(now, eventId));

            // The temporary file lives beside outbox\, not in it, so the flow's "When a file is created" never sees it.
            AtomicFile.WriteAllText(Path.Combine(outbox, record.FileName), BuildJson(record), overwrite: false, tempDirectory: folder.FullPath);
            lock (_gate)
            {
                if (_lastWritten.Count > MaxRemembered)
                {
                    _lastWritten.Clear();
                }

                _lastWritten[dedupeKey] = now;
            }

            SetState(new AlertOutboxState(ExportOutcome.Written, ExportFolderState.Ready, $"Last alert: {eventName} for {record.WorkflowName}.", now, eventName));
            Prune(now);
            return AlertWriteOutcome.Written;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetState(new AlertOutboxState(ExportOutcome.Failed, ExportFolderState.Ready, $"Could not write an alert: {Scrub(ex.Message)}", State.LastAlertAt, State.LastEvent));
            return AlertWriteOutcome.Failed;
        }
    }

    /// <summary>
    /// Keeps at most <see cref="MaxFiles"/> alert files and none older than <see cref="MaxAge"/> (by the timestamp in the
    /// name). Only files directly in <c>outbox\</c> whose names match the app's pattern are touched; reparse points
    /// (the folder itself or any file) are never followed or deleted. Returns how many files were deleted.
    /// </summary>
    public int Prune(DateTimeOffset now)
    {
        if (_profile.Exports.Folder is null)
        {
            return 0;
        }

        var folder = ExportFolder.Resolve(_profile, _paths, createIfParentExists: false);
        if (!folder.IsReady)
        {
            return 0;
        }

        var outbox = Path.Combine(folder.FullPath!, AppPaths.OutboxFolderName);
        try
        {
            var directory = new DirectoryInfo(outbox);
            if (!directory.Exists || OneDriveBoundary.IsLink(directory))
            {
                return 0;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Directory,
                MatchType = MatchType.Simple,
            };

            var candidates = new List<(FileInfo File, DateTimeOffset Stamp)>();
            foreach (var file in directory.EnumerateFiles("*.json", options))
            {
                var match = NamePattern().Match(file.Name);
                if (!match.Success || OneDriveBoundary.IsLink(file))
                {
                    continue;
                }

                if (DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
                {
                    candidates.Add((file, new DateTimeOffset(stamp, TimeSpan.Zero)));
                }
            }

            var ordered = candidates.OrderByDescending(c => c.Stamp).ThenByDescending(c => c.File.Name, StringComparer.Ordinal).ToList();
            var deleted = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i < MaxFiles && now - ordered[i].Stamp <= MaxAge)
                {
                    continue;
                }

                try
                {
                    ordered[i].File.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The flow may be reading or deleting it right now; the next prune tries again.
                }
            }

            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    /// <summary>The file name for an alert: <c>&lt;yyyyMMdd-HHmmss UTC&gt;-&lt;eventId "N"&gt;.json</c>.</summary>
    public static string FileNameFor(DateTimeOffset at, Guid eventId) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.UtcDateTime:yyyyMMdd-HHmmss}-{eventId:N}.json");

    /// <summary>Whether <paramref name="fileName"/> is one of the app's alert files (the only names <see cref="Prune"/> touches).</summary>
    public static bool IsAlertFileName(string fileName) => fileName is not null && NamePattern().IsMatch(fileName);

    /// <summary>The alert file text (schema v1).</summary>
    public string BuildJson(AlertRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, StatusExporter.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("event", record.Event);
            writer.WriteString("eventId", record.EventId.ToString("N"));
            writer.WriteString("at", ExportText.Timestamp(record.At, _time.LocalTimeZone));
            writer.WriteString("profile", record.Profile.DisplayName());
            writer.WriteString("workflow", record.WorkflowId);
            writer.WriteString("workflowName", record.WorkflowName);
            writer.WriteString("runId", record.RunId.ToString("D"));
            if (record.Step is null)
            {
                writer.WriteNull("step");
            }
            else
            {
                writer.WriteString("step", record.Step);
            }

            writer.WriteString("message", record.Message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private AlertWriteOutcome RaiseOnce(string eventName, ExecutionSnapshot execution, string? stepId, string message, DateTimeOffset now)
    {
        var seenKey = $"{execution.Id:N}\n{eventName}";
        lock (_gate)
        {
            if (_seen.Contains(seenKey))
            {
                return AlertWriteOutcome.Deduplicated;
            }

            if (_seen.Count > MaxRemembered)
            {
                _seen.Clear();
            }
        }

        var outcome = Raise(eventName, execution, stepId, message, now);
        if (outcome is AlertWriteOutcome.Written or AlertWriteOutcome.Deduplicated or AlertWriteOutcome.Disabled)
        {
            lock (_gate)
            {
                _seen.Add(seenKey);
            }
        }
        else if (eventName != AlertEvents.RunOverrun)
        {
            // The engine reports a run's end exactly once, so a skipped or failed write (folder briefly missing or locked)
            // waits here for RetryPending. Overruns need no queue: CheckOverruns sees the run again every 30 s.
            lock (_gate)
            {
                _seen.Add(seenKey);
                if (_pending.Count >= MaxPending)
                {
                    _pending.RemoveAt(0);
                }

                _pending.Add(new PendingAlert(eventName, execution, stepId, message, now));
            }
        }

        return outcome;
    }

    /// <summary>
    /// Tries again the run.failed / run.needsAttention alerts whose write was skipped or failed (ExportsHub calls this
    /// every 30 s). An alert leaves the queue once written, deduplicated, or switched off, when its folder is refused,
    /// or when it is older than <see cref="DedupeWindow"/>. A retried alert keeps its original <c>at</c>. Returns how many
    /// were written.
    /// </summary>
    public int RetryPending(DateTimeOffset now)
    {
        List<PendingAlert> due;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return 0;
            }

            due = [.. _pending];
        }

        var written = 0;
        foreach (var alert in due)
        {
            var settled = now - alert.At > DedupeWindow;
            if (!settled)
            {
                var outcome = Raise(alert.Event, alert.Execution, alert.Step, alert.Message, alert.At);
                written += outcome == AlertWriteOutcome.Written ? 1 : 0;
                settled = outcome is AlertWriteOutcome.Written or AlertWriteOutcome.Deduplicated or AlertWriteOutcome.Disabled
                    || State.Folder == ExportFolderState.Refused;
            }

            if (settled)
            {
                lock (_gate)
                {
                    _pending.Remove(alert);
                }
            }
        }

        return written;
    }

    private string? Scrub(string? text) => ExportText.Scrub(text, paths: _paths);

    private static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && OneDriveBoundary.IsLink(info);
    }

    private void SetState(AlertOutboxState next)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != next;
            _state = next;
        }

        if (!changed)
        {
            return;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AlertOutboxState>>())
        {
            try
            {
                handler(this, next);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A Settings view failing is not an alert failure.
            }
        }
    }

    /// <summary>An alert whose write was skipped or failed, kept in memory for <see cref="RetryPending"/>.</summary>
    private sealed record PendingAlert(string Event, ExecutionSnapshot Execution, string? Step, string Message, DateTimeOffset At);

    [GeneratedRegex(@"^(?<stamp>\d{8}-\d{6})-[0-9a-f]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
