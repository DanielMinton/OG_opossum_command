using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Exports;

/// <summary>How the last export attempt ended.</summary>
public enum ExportOutcome
{
    /// <summary>Nothing attempted yet.</summary>
    NotYet,

    /// <summary>The file was written.</summary>
    Written,

    /// <summary>Exports are off for this profile (or this feature), so nothing was written.</summary>
    Disabled,

    /// <summary>The folder is missing (OneDrive not signed in or not synced yet) or refused; nothing was written.</summary>
    Skipped,

    /// <summary>Writing failed (disk, permissions, database); the next attempt tries again.</summary>
    Failed,
}

/// <summary>What Settings shows about <c>status.json</c> for one profile.</summary>
/// <param name="Outcome">How the last attempt ended.</param>
/// <param name="Folder">The folder state at the last attempt.</param>
/// <param name="Message">One readable line.</param>
/// <param name="LastWrittenAt">When the file was last written successfully, if ever.</param>
public sealed record StatusExportState(ExportOutcome Outcome, ExportFolderState Folder, string Message, DateTimeOffset? LastWrittenAt)
{
    public static StatusExportState Initial { get; } = new(ExportOutcome.NotYet, ExportFolderState.Disabled, "Not written yet.", null);
}

/// <summary>
/// Writes <c>status.json</c> (schema v1, ADR-050) into one profile's exports folder for the user's Power Automate
/// Morning Briefing flow: every <see cref="Interval"/>, soon after a run of the profile fails, completes, is cancelled,
/// or needs attention (at most once per <see cref="Debounce"/>), and once on clean exit. The content is small and
/// scrubbed: no step output, arguments, file paths, or URLs; every free-text field goes through <see cref="ExportText.Scrub"/>.
/// Time comes from the injected <see cref="TimeProvider"/>; <see cref="Tick"/> does the bookkeeping so tests drive it.
/// </summary>
public sealed class StatusExporter
{
    /// <summary>Current schema of <c>status.json</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>At most this many failed runs are listed.</summary>
    public const int MaxFailed = 20;

    /// <summary>At most this many runs needing attention / scheduled occurrences are listed.</summary>
    public const int MaxListed = 50;

    /// <summary>How many recent runs are read to count today's completions and the last day's failures.</summary>
    public const int RecentRunsScanned = 1000;

    /// <summary>Regular export interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Minimum spacing of event-driven exports.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);

    /// <summary>Failures from this far back are listed (<c>failedSince</c> = generatedAt minus this).</summary>
    public static readonly TimeSpan FailedWindow = TimeSpan.FromHours(24);

    /// <summary>Shared by the exporters: indented, and without HTML-style escaping of <c>+</c> or non-ASCII (the file is never embedded in a page).</summary>
    internal static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Profile _profile;
    private readonly IAppPaths _paths;
    private readonly IHistoryStore _history;
    private readonly Func<IReadOnlyList<ExecutionSnapshot>> _activeRuns;
    private readonly Func<IReadOnlyList<ProfileWorkflows>> _workflows;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _pendingDueAt;
    private long _requests;
    private StatusExportState _state = StatusExportState.Initial;

    /// <param name="profile">The profile exported; only its runs appear.</param>
    /// <param name="paths">OneDrive roots for the cross-OneDrive guard.</param>
    /// <param name="history">Runs needing attention, recent failures, completions.</param>
    /// <param name="activeRuns">Runs active right now (normally <see cref="WorkflowEngine.ActiveExecutions"/>).</param>
    /// <param name="workflows">Loaded workflows, for today's schedule.</param>
    /// <param name="time">Clock and local zone.</param>
    /// <param name="appVersion">Written as <c>appVersion</c>; defaults to <see cref="ExportText.AppVersion"/>.</param>
    public StatusExporter(Profile profile, IAppPaths paths, IHistoryStore history, Func<IReadOnlyList<ExecutionSnapshot>> activeRuns, Func<IReadOnlyList<ProfileWorkflows>> workflows, TimeProvider? time = null, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(activeRuns);
        ArgumentNullException.ThrowIfNull(workflows);

        _profile = profile;
        _paths = paths;
        _history = history;
        _activeRuns = activeRuns;
        _workflows = workflows;
        _time = time ?? TimeProvider.System;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? ExportText.AppVersion : appVersion;
    }

    /// <summary>
    /// Raised whenever <see cref="State"/> differs from the previous attempt's: every successful write (its time moves on),
    /// and a failure or skip only when its outcome, folder state or message is new. Never throws into the caller.
    /// </summary>
    public event EventHandler<StatusExportState>? Changed;

    /// <summary>The profile exported.</summary>
    public ProfileId Profile => _profile.Id;

    /// <summary>Whether <c>status.json</c> is switched on for the profile at all.</summary>
    public bool IsEnabled => _profile.Exports.Status && _profile.Exports.Folder is not null;

    /// <summary>The last attempt's result, for Settings.</summary>
    public StatusExportState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Asks for an export soon (a run of this profile reached a terminal state or needs attention); debounced by <see cref="Debounce"/>.</summary>
    public void RequestSoon(DateTimeOffset now)
    {
        lock (_gate)
        {
            _requests++;
            var earliest = _lastAttemptAt is { } last ? last + Debounce : now;
            var due = earliest > now ? earliest : now;
            if (_pendingDueAt is null || due < _pendingDueAt)
            {
                _pendingDueAt = due;
            }
        }
    }

    /// <summary>Writes when the regular interval elapsed or a requested export is due. Returns whether a write was attempted.</summary>
    public bool Tick(DateTimeOffset now)
    {
        bool due;
        lock (_gate)
        {
            due = _lastAttemptAt is null
                || now - _lastAttemptAt.Value >= Interval
                || now < _lastAttemptAt.Value // the clock went back: do not wait out the gap
                || (_pendingDueAt is { } pending && now >= pending);
        }

        if (!due)
        {
            return false;
        }

        WriteNow(now);
        return true;
    }

    /// <summary>Builds and writes <c>status.json</c> now, whatever the schedule (clean exit). Never throws for I/O or database problems.</summary>
    public StatusExportState WriteNow(DateTimeOffset now)
    {
        lock (_writeGate)
        {
            StatusExportState next;
            StatusExportState previous;
            long requestsBefore;
            lock (_gate)
            {
                previous = _state;
                requestsBefore = _requests;
            }

            if (!_profile.Exports.Status)
            {
                next = new StatusExportState(ExportOutcome.Disabled, ExportFolderState.Disabled, _profile.Exports.DisabledReason ?? $"status.json is switched off for the {_profile.DisplayName} profile.", previous.LastWrittenAt);
            }
            else
            {
                var folder = ExportFolder.Resolve(_profile, _paths, createIfParentExists: true);
                if (!folder.IsReady)
                {
                    var outcome = folder.State == ExportFolderState.Disabled ? ExportOutcome.Disabled : ExportOutcome.Skipped;
                    next = new StatusExportState(outcome, folder.State, folder.Message, previous.LastWrittenAt);
                }
                else
                {
                    try
                    {
                        var json = BuildJson(now);
                        AtomicFile.WriteAllText(Path.Combine(folder.FullPath!, AppPaths.StatusFileName), json);
                        var local = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone);
                        next = new StatusExportState(ExportOutcome.Written, ExportFolderState.Ready, $"status.json written at {local.ToString("HH:mm", CultureInfo.CurrentCulture)}.", now);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        next = new StatusExportState(ExportOutcome.Failed, ExportFolderState.Ready, $"Could not write status.json: {Scrub(ex.Message)}", previous.LastWrittenAt);
                    }
                }
            }

            lock (_gate)
            {
                _lastAttemptAt = now;

                // A failed write (OneDrive or antivirus holding the file) is tried again after the debounce, not in 15 minutes.
                // A request that arrived while this write was being built is honoured too: its run may be missing from the file.
                _pendingDueAt = next.Outcome == ExportOutcome.Failed || _requests != requestsBefore ? now + Debounce : null;
                _state = next;
            }

            if (previous != next)
            {
                RaiseChanged(next);
            }

            return next;
        }
    }

    /// <summary>The <c>status.json</c> text for <paramref name="now"/> (public for tests and the docs).</summary>
    public string BuildJson(DateTimeOffset now)
    {
        var zone = _time.LocalTimeZone;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var failedSince = now - FailedWindow;

        // Queried per profile so the other profile's backlog can never fill the cap and hide this one's runs.
        var needsAttention = _history.GetExecutionsByStatus(ExecutionStatus.NeedsAttention, _profile.Id, MaxListed)
            .Where(e => e.Profile == _profile.Id)
            .ToList();
        var recent = _history.GetRecentExecutions(_profile.Id, RecentRunsScanned).Where(e => e.Profile == _profile.Id).ToList();
        var failed = recent
            .Where(e => e.Status == ExecutionStatus.Failed && (e.FinishedAt ?? e.QueuedAt) >= failedSince)
            .OrderByDescending(e => e.FinishedAt ?? e.QueuedAt)
            .Take(MaxFailed)
            .ToList();
        var completedToday = recent.Count(e =>
            e.Status == ExecutionStatus.Completed
            && e.FinishedAt is { } finished
            && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(finished, zone).DateTime) == today);
        var running = _activeRuns().Where(e => e.Profile == _profile.Id).OrderBy(e => e.StartedAt ?? e.QueuedAt).ToList();
        var scheduled = ScheduledToday(now, today, zone);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("profile", _profile.DisplayName);
            writer.WriteString("generatedAt", ExportText.Timestamp(now, zone));
            writer.WriteString("appVersion", _appVersion);

            writer.WriteStartArray("needsAttention");
            foreach (var run in needsAttention)
            {
                writer.WriteStartObject();
                writer.WriteString("runId", run.Id.ToString("D"));
                writer.WriteString("workflow", Scrub(run.WorkflowId));
                writer.WriteString("workflowName", Scrub(run.WorkflowName));
                WriteNullableString(writer, "step", Scrub(UnfinishedStep(run)?.Id));
                WriteNullableString(writer, "reason", Scrub(run.Error) ?? "Needs attention: Resume or Dismiss in the app.");
                writer.WriteString("since", ExportText.Timestamp(run.FinishedAt ?? run.StartedAt ?? run.QueuedAt, zone));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteString("failedSince", ExportText.Timestamp(failedSince, zone));
            writer.WriteStartArray("failed");
            foreach (var run in failed)
            {
                var step = run.Steps.Where(s => s.Status == StepStatus.Failed).OrderBy(s => s.Ordinal).FirstOrDefault();
                writer.WriteStartObject();
                writer.WriteString("runId", run.Id.ToString("D"));
                writer.WriteString("workflow", Scrub(run.WorkflowId));
                writer.WriteString("workflowName", Scrub(run.WorkflowName));
                WriteNullableString(writer, "step", Scrub(step?.Id));
                WriteNullableString(writer, "error", Scrub(step?.Message ?? run.Error) ?? "Failed.");
                writer.WriteString("at", ExportText.Timestamp(run.FinishedAt ?? run.QueuedAt, zone));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteNumber("completedToday", completedToday);

            writer.WriteStartArray("runningNow");
            foreach (var run in running)
            {
                writer.WriteStartObject();
                writer.WriteString("workflow", Scrub(run.WorkflowId));
                writer.WriteString("startedAt", ExportText.Timestamp(run.StartedAt ?? run.QueuedAt, zone));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("scheduledToday");
            foreach (var (workflow, at) in scheduled)
            {
                writer.WriteStartObject();
                writer.WriteString("workflow", Scrub(workflow.Id));
                writer.WriteString("workflowName", Scrub(workflow.Name));
                writer.WriteString("at", ExportText.Timestamp(at, zone));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The step a run needing attention stopped in: the first one that was running or interrupted.</summary>
    internal static StepSnapshot? UnfinishedStep(ExecutionSnapshot run) =>
        run.Steps.Where(s => s.Status is StepStatus.Running or StepStatus.Interrupted).OrderBy(s => s.Ordinal).FirstOrDefault();

    /// <summary>Occurrences from now to the end of the local day of the profile's enabled schedules (none while the scheduler is paused).</summary>
    private List<(WorkflowDefinition Workflow, DateTimeOffset At)> ScheduledToday(DateTimeOffset now, DateOnly today, TimeZoneInfo zone)
    {
        var result = new List<(WorkflowDefinition, DateTimeOffset)>();
        if (string.Equals(_history.GetSetting(SettingKeys.SchedulerPaused), "true", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var endOfDay = OccurrenceCalculator.ToInstant(today.AddDays(1), TimeOnly.MinValue, zone);
        foreach (var loaded in _workflows().Where(w => w.Profile.Id == _profile.Id))
        {
            foreach (var workflow in loaded.Workflows.Where(w => w.Schedule is not null))
            {
                var trigger = _history.GetTrigger(TriggerRecord.IdFor(workflow.ProfileId, workflow.Id));
                if (trigger is { Enabled: false })
                {
                    continue;
                }

                foreach (var at in OccurrenceCalculator.OccurrencesBetween(workflow.Schedule!, now, endOfDay).Where(at => at < endOfDay))
                {
                    result.Add((workflow, at));
                }
            }
        }

        return result.OrderBy(r => r.Item2).ThenBy(r => r.Item1.Id, StringComparer.Ordinal).Take(MaxListed).ToList();
    }

    private string? Scrub(string? text) => ExportText.Scrub(text, paths: _paths);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private void RaiseChanged(StatusExportState state)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<StatusExportState>>())
        {
            try
            {
                handler(this, state);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A Settings view failing is not an export failure.
            }
        }
    }
}
