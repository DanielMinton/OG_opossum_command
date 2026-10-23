using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Scheduling;

/// <summary>
/// One row of the <c>triggers</c> table: the persisted state of a workflow's schedule (spec §08, §13). The schedule
/// itself is defined in the workflow file (ADR-008); this row remembers what has already fired so a restart never
/// fires the same occurrence twice.
/// </summary>
/// <param name="Id"><c>&lt;profile&gt;/&lt;workflow&gt;/schedule</c>.</param>
/// <param name="Profile">Owning profile.</param>
/// <param name="WorkflowId">Workflow the trigger starts.</param>
/// <param name="Type">Always <c>schedule</c> in this version.</param>
/// <param name="Enabled">Whether the trigger fires at all.</param>
/// <param name="ConfigJson">Human-readable copy of the schedule, for diagnostics.</param>
/// <param name="ConfigHash"><see cref="ScheduleDefinition.ConfigHash"/> the row was built from; a change resets the state.</param>
/// <param name="NextDueAt">The next occurrence, for the UI.</param>
/// <param name="LastFiredFor">The latest occurrence already accounted for (fired, suppressed, or skipped). Written before any run starts.</param>
/// <param name="LastFiredAt">When the trigger last started a run.</param>
/// <param name="LastOutcome">One line about the last evaluation (started, suppressed, skipped …).</param>
/// <param name="UpdatedAt">Row timestamp.</param>
public sealed record TriggerRecord(
    string Id,
    ProfileId Profile,
    string WorkflowId,
    string Type,
    bool Enabled,
    string ConfigJson,
    string ConfigHash,
    DateTimeOffset? NextDueAt,
    DateTimeOffset? LastFiredFor,
    DateTimeOffset? LastFiredAt,
    string? LastOutcome,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The only trigger type persisted so far.</summary>
    public const string ScheduleType = "schedule";

    /// <summary>Row ID for a workflow's schedule trigger.</summary>
    public static string IdFor(ProfileId profile, string workflowId) => $"{profile.ToJsonValue()}/{workflowId}/{ScheduleType}";
}
