using System.Text.Json;
using OG_opossum_command.Core.Profiles;

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
    /// <summary>Do not start a second run; the UI shows the existing one. The only policy in schema v1.</summary>
    FocusExisting,
}

/// <summary>One validated step. <see cref="Inputs"/> is the action's typed input object, produced by <c>IWorkflowAction.ParseInputs</c>.</summary>
public sealed record StepDefinition(
    string Id,
    string Name,
    string ActionId,
    object Inputs,
    JsonElement RawInputs,
    TimeSpan? Timeout,
    IReadOnlyList<string> DependsOn,
    OnFailurePolicy OnFailure);

/// <summary>A validated workflow (schema v1; see docs/WORKFLOW-SCHEMA.md).</summary>
public sealed record WorkflowDefinition(
    int SchemaVersion,
    string Id,
    string Name,
    string? Description,
    ProfileId ProfileId,
    int MaxParallelSteps,
    OnDuplicatePolicy OnDuplicate,
    bool RequiresApproval,
    IReadOnlyList<StepDefinition> Steps,
    string SourcePath,
    string ContentHash)
{
    /// <summary>The schema version this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Finds a step by ID.</summary>
    public StepDefinition? FindStep(string stepId) => Steps.FirstOrDefault(s => s.Id == stepId);
}

/// <summary>Everything loaded for one profile: valid workflows plus readable errors for the invalid files.</summary>
public sealed record ProfileWorkflows(Profile Profile, IReadOnlyList<WorkflowDefinition> Workflows, IReadOnlyList<WorkflowValidationError> Errors);
