namespace OG_opossum_command.Core.Workflows;

/// <summary>One readable problem found while loading a workflow file.</summary>
/// <param name="Source">The file path, or a label such as <c>&lt;inline&gt;</c> for definitions loaded from a string.</param>
/// <param name="JsonPath">Where in the document the problem is, in dotted form (<c>steps[2].inputs.url</c>), or <c>$</c> for the whole file.</param>
/// <param name="Message">What is wrong and what is expected instead.</param>
public sealed record WorkflowValidationError(string Source, string JsonPath, string Message)
{
    /// <summary>Single-line form shown to the user.</summary>
    public override string ToString() => $"{Source}: {JsonPath}: {Message}";
}

/// <summary>Collects <see cref="WorkflowValidationError"/>s for one source while the loader walks the document.</summary>
public sealed class ValidationErrors
{
    private readonly List<WorkflowValidationError> _errors = [];

    public ValidationErrors(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
    }

    /// <summary>The file path or label every error is attributed to.</summary>
    public string Source { get; }

    /// <summary>Errors found so far, in document order.</summary>
    public IReadOnlyList<WorkflowValidationError> Items => _errors;

    /// <summary>Whether any error has been recorded.</summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>Records one error.</summary>
    public void Add(string jsonPath, string message) => _errors.Add(new WorkflowValidationError(Source, jsonPath, message));
}
