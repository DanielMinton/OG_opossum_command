using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;

namespace OG_opossum_command.Core.Workflows;

/// <summary>What happens to the rest of the run when a step fails.</summary>
public enum OnFailurePolicy
{
    /// <summary>Default: no further steps start; steps already running finish; the run is Failed.</summary>
    Stop,

    /// <summary>Steps that do not depend on the failed step still run; the run still ends Failed.</summary>
    Continue,
}

/// <summary>What happens when a workflow is started while a run of it is still active.</summary>
public enum OnDuplicatePolicy
{
    /// <summary>Do not start a second run; the UI shows the existing one. The only policy so far.</summary>
    FocusExisting,
}

/// <summary>How a workflow is started.</summary>
public enum TriggerType
{
    /// <summary>Tile, command palette, or hotkey only.</summary>
    Manual,

    /// <summary>Manual plus the schedule in <see cref="TriggerDefinition.Schedule"/> (schema v2).</summary>
    Schedule,
}

/// <summary>The file's <c>trigger</c> block.</summary>
/// <param name="Type">Manual or Schedule.</param>
/// <param name="Schedule">The schedule when <paramref name="Type"/> is <see cref="TriggerType.Schedule"/>; otherwise <c>null</c>.</param>
public sealed record TriggerDefinition(TriggerType Type, ScheduleDefinition? Schedule)
{
    /// <summary>The manual trigger.</summary>
    public static TriggerDefinition Manual { get; } = new(TriggerType.Manual, null);
}

/// <summary>One validated step. <see cref="Inputs"/> is the action's typed input object, produced by <c>IWorkflowAction.ParseInputs</c>.</summary>
/// <param name="Locks">Normalised resource lock keys (<c>window:outlook</c>, <c>file:c:\notes.txt</c>); steps sharing a key never run at the same time (ADR-033).</param>
public sealed record StepDefinition(
    string Id,
    string Name,
    string ActionId,
    object Inputs,
    TimeSpan? Timeout,
    IReadOnlyList<string> DependsOn,
    OnFailurePolicy OnFailure,
    IReadOnlyList<string> Locks);

/// <summary>A validated workflow (schema v1 or v2; see docs/WORKFLOW-SCHEMA.md).</summary>
/// <param name="Aliases">Extra names the command palette matches (schema v2); empty otherwise.</param>
public sealed record WorkflowDefinition(
    int SchemaVersion,
    string Id,
    string Name,
    string? Description,
    ProfileId ProfileId,
    TriggerDefinition Trigger,
    int MaxParallelSteps,
    OnDuplicatePolicy OnDuplicate,
    bool RequiresApproval,
    IReadOnlyList<StepDefinition> Steps,
    IReadOnlyList<string> Aliases,
    string SourcePath,
    string ContentHash)
{
    /// <summary>The newest schema version this build understands. Older ones still load (ADR-032).</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The oldest schema version this build still loads.</summary>
    public const int OldestSupportedSchemaVersion = 1;

    /// <summary>Finds a step by ID.</summary>
    public StepDefinition? FindStep(string stepId) => Steps.FirstOrDefault(s => s.Id == stepId);

    /// <summary>The schedule when the trigger is a schedule, else <c>null</c>.</summary>
    public ScheduleDefinition? Schedule => Trigger.Schedule;
}

/// <summary>Everything loaded for one profile: valid workflows plus readable errors for the invalid files.</summary>
public sealed record ProfileWorkflows(Profile Profile, IReadOnlyList<WorkflowDefinition> Workflows, IReadOnlyList<WorkflowValidationError> Errors);
