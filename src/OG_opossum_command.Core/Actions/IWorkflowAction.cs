using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions;

/// <summary>How safely an action can be repeated. Used by crash recovery (spec §13) to decide what may auto-resume.</summary>
public enum IdempotencyClass
{
    /// <summary>Repeating has no additional effect worth worrying about (opening a URL or folder that is already open).</summary>
    Idempotent,

    /// <summary>Repeating causes a second side effect (a second process). Never repeat blindly.</summary>
    NonIdempotent,
}

/// <summary>Outcome of one action run.</summary>
/// <param name="Succeeded">Whether the step should count as completed.</param>
/// <param name="ErrorCode">Stable, machine-readable failure reason (<c>executable-not-found</c>), or <c>null</c> on success.</param>
/// <param name="Message">One human-readable line.</param>
/// <param name="Output">Captured diagnostic output; the engine bounds and redacts it before it is stored.</param>
public sealed record ActionResult(bool Succeeded, string? ErrorCode, string Message, string? Output = null)
{
    public static ActionResult Success(string message, string? output = null) => new(true, null, message, output);

    public static ActionResult Failure(string errorCode, string message, string? output = null) => new(false, errorCode, message, output);
}

/// <summary>What an action knows about the step it is running.</summary>
public sealed record ActionContext(Guid ExecutionId, string WorkflowId, string StepId, Profile Profile);

/// <summary>
/// One registered capability (spec §05 D). Every side effect goes through an injectable launcher so tests and
/// smoke modes can run actions with fakes (rule 7).
/// </summary>
public interface IWorkflowAction
{
    /// <summary>Stable identifier used in workflow files and profile allowlists, e.g. <c>windows.url.open</c>.</summary>
    string Id { get; }

    /// <summary>Short human-readable name.</summary>
    string DisplayName { get; }

    /// <summary>Whether a repeat is safe.</summary>
    IdempotencyClass Idempotency { get; }

    /// <summary>Timeout applied when the step declares none.</summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>
    /// Parses and validates the step's <c>inputs</c> object. Returns the typed inputs, or <c>null</c> after
    /// recording at least one error on the reader.
    /// </summary>
    object? ParseInputs(JsonObjectReader inputs);

    /// <summary>Runs the action with inputs previously produced by <see cref="ParseInputs"/>.</summary>
    Task<ActionResult> ExecuteAsync(object inputs, ActionContext context, CancellationToken cancellationToken);
}

/// <summary>Typed base for actions: validation and execution see <typeparamref name="TInputs"/>, never a dictionary.</summary>
public abstract class WorkflowAction<TInputs> : IWorkflowAction
    where TInputs : class
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract IdempotencyClass Idempotency { get; }

    /// <inheritdoc />
    public virtual TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Typed form of <see cref="IWorkflowAction.ParseInputs"/>.</summary>
    public abstract TInputs? Parse(JsonObjectReader inputs);

    /// <summary>Typed form of <see cref="IWorkflowAction.ExecuteAsync"/>.</summary>
    public abstract Task<ActionResult> ExecuteAsync(TInputs inputs, ActionContext context, CancellationToken cancellationToken);

    object? IWorkflowAction.ParseInputs(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Parse(inputs);
    }

    Task<ActionResult> IWorkflowAction.ExecuteAsync(object inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs is not TInputs typed)
        {
            throw new ArgumentException($"Action '{Id}' expects inputs of type {typeof(TInputs).Name}, got {inputs.GetType().Name}.", nameof(inputs));
        }

        return ExecuteAsync(typed, context, cancellationToken);
    }
}
