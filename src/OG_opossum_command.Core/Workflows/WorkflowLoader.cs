using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Workflows;

/// <summary>Loads workflow JSON files and validates them strictly, producing readable errors instead of exceptions.</summary>
public sealed partial class WorkflowLoader
{
    /// <summary>Upper bound on <c>execution.maxParallelSteps</c>.</summary>
    public const int MaxParallelStepsLimit = 8;

    /// <summary>Upper bound on <c>timeoutSeconds</c> (one hour).</summary>
    public const int MaxTimeoutSeconds = 3600;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ActionRegistry _registry;

    public WorkflowLoader(ActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Loads every <c>*.json</c> file in the profile's workflows folder (sorted by file name). A missing folder yields no workflows and no errors.</summary>
    public ProfileWorkflows LoadProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var workflows = new List<WorkflowDefinition>();
        var errors = new List<WorkflowValidationError>();
        if (Directory.Exists(profile.WorkflowsDirectory))
        {
            var files = Directory.GetFiles(profile.WorkflowsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase);
            var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var result = LoadFile(file, profile);
                if (result.Workflow is null)
                {
                    errors.AddRange(result.Errors);
                    continue;
                }

                if (seenIds.TryGetValue(result.Workflow.Id, out var otherFile))
                {
                    errors.Add(new WorkflowValidationError(file, "id", $"Workflow id '{result.Workflow.Id}' is already used by {Path.GetFileName(otherFile)}. IDs must be unique within a profile."));
                    continue;
                }

                seenIds[result.Workflow.Id] = file;
                workflows.Add(result.Workflow);
            }
        }

        return new ProfileWorkflows(profile, workflows, errors);
    }

    /// <summary>Loads one file for the given profile. The file's <c>profileId</c> must match.</summary>
    public WorkflowLoadResult LoadFile(string path, Profile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return WorkflowLoadResult.Failed(new WorkflowValidationError(path, "$", $"Could not read the file: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return WorkflowLoadResult.Failed(new WorkflowValidationError(path, "$", $"Could not read the file: {ex.Message}"));
        }

        return LoadJson(text, profile, path);
    }

    /// <summary>Parses and validates workflow JSON text. <paramref name="source"/> labels errors (a file path, or e.g. <c>&lt;smoke&gt;</c>).</summary>
    public WorkflowLoadResult LoadJson(string json, Profile profile, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var errors = new ValidationErrors(source);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is null ? string.Empty : $" (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1})";
            errors.Add("$", $"Not valid JSON{where}: {ex.Message}");
            return WorkflowLoadResult.Failed(errors.Items);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("$", "A workflow file must contain one JSON object.");
                return WorkflowLoadResult.Failed(errors.Items);
            }

            var workflow = Validate(new JsonObjectReader(document.RootElement, "$", errors), profile, source, ComputeHash(json));
            return workflow is null || errors.HasErrors
                ? WorkflowLoadResult.Failed(errors.Items)
                : WorkflowLoadResult.Loaded(workflow);
        }
    }

    private WorkflowDefinition? Validate(JsonObjectReader root, Profile profile, string source, string hash)
    {
        root.RejectUnknownProperties("schemaVersion", "id", "name", "description", "profileId", "trigger", "execution", "requiresApproval", "steps");

        var schemaVersion = root.RequiredInt("schemaVersion", 1, int.MaxValue);
        if (schemaVersion is not null && schemaVersion != WorkflowDefinition.CurrentSchemaVersion)
        {
            root.AddError("schemaVersion", $"This version of the app understands schemaVersion {WorkflowDefinition.CurrentSchemaVersion} only (file says {schemaVersion}).");
        }

        var id = root.RequiredString("id");
        if (id is not null && !SlugPattern().IsMatch(id))
        {
            root.AddError("id", $"'id' must be 1-64 lower-case letters, digits, or hyphens, starting with a letter or digit (got '{id}').");
        }

        var name = root.RequiredString("name");
        if (name is { Length: > 80 })
        {
            root.AddError("name", "'name' must be 80 characters or fewer.");
        }

        var description = root.OptionalString("description");

        var profileText = root.RequiredString("profileId");
        if (profileText is not null)
        {
            if (!ProfileIdExtensions.TryParseJsonValue(profileText, out var declared))
            {
                root.AddError("profileId", $"'profileId' must be \"work\" or \"personal\" (got '{profileText}').");
            }
            else if (declared != profile.Id)
            {
                root.AddError("profileId", $"This file is in the {profile.DisplayName} profile folder but says profileId \"{profileText}\". A workflow belongs to exactly one profile; move the file or fix the value.");
            }
        }

        var trigger = root.RequiredObject("trigger");
        if (trigger is not null)
        {
            trigger.RejectUnknownProperties("type");
            var type = trigger.RequiredString("type");
            if (type is not null && type != "manual")
            {
                trigger.AddError("type", $"Only \"manual\" triggers exist in this version (got '{type}'). Schedules come in a later version.");
            }
        }

        var maxParallel = 1;
        var onDuplicate = OnDuplicatePolicy.FocusExisting;
        var execution = root.OptionalObject("execution");
        if (execution is not null)
        {
            execution.RejectUnknownProperties("maxParallelSteps", "onDuplicate");
            maxParallel = execution.OptionalInt("maxParallelSteps", 1, MaxParallelStepsLimit) ?? 1;
            var duplicate = execution.OptionalString("onDuplicate");
            if (duplicate is not null && duplicate != "focus-existing")
            {
                execution.AddError("onDuplicate", $"'onDuplicate' must be \"focus-existing\" (got '{duplicate}'); it is the only policy in this version.");
            }
        }

        var requiresApproval = root.OptionalBool("requiresApproval") ?? false;
        if (requiresApproval)
        {
            root.AddError("requiresApproval", "'requiresApproval' is reserved for a future AI-assisted mode and must be false (or omitted) in this version.");
        }

        var steps = ValidateSteps(root, profile);
        if (id is null || name is null || steps is null)
        {
            return null;
        }

        return new WorkflowDefinition(
            WorkflowDefinition.CurrentSchemaVersion,
            id,
            name,
            description,
            profile.Id,
            maxParallel,
            onDuplicate,
            requiresApproval,
            steps,
            source,
            hash);
    }

    private List<StepDefinition>? ValidateSteps(JsonObjectReader root, Profile profile)
    {
        var stepReaders = root.RequiredObjectArray("steps");
        if (stepReaders is null)
        {
            return null;
        }

        if (stepReaders.Count == 0)
        {
            root.AddError("steps", "'steps' must contain at least one step.");
            return null;
        }

        var steps = new List<StepDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var reader in stepReaders)
        {
            reader.RejectUnknownProperties("id", "name", "action", "inputs", "timeoutSeconds", "dependsOn", "onFailure");

            var stepId = reader.RequiredString("id");
            if (stepId is not null)
            {
                if (!SlugPattern().IsMatch(stepId))
                {
                    reader.AddError("id", $"Step 'id' must be 1-64 lower-case letters, digits, or hyphens (got '{stepId}').");
                }
                else if (!ids.Add(stepId))
                {
                    reader.AddError("id", $"Step id '{stepId}' is used more than once.");
                    ok = false; // the dependency check keys steps by id, so it cannot run with duplicates
                }
            }

            var stepName = reader.OptionalString("name");
            var actionId = reader.RequiredString("action");
            IWorkflowAction? action = null;
            if (actionId is not null)
            {
                if (!_registry.TryGet(actionId, out action!))
                {
                    reader.AddError("action", $"Unknown action '{actionId}'. Known actions: {string.Join(", ", _registry.Ids)}.");
                    action = null;
                }
                else if (!profile.IsActionAllowed(actionId))
                {
                    reader.AddError("action", $"Action '{actionId}' is not allowed in the {profile.DisplayName} profile.");
                }
            }

            object? inputs = null;
            var inputsReader = reader.RequiredObject("inputs");
            if (inputsReader is not null && action is not null)
            {
                inputs = action.ParseInputs(inputsReader);
            }

            var timeoutSeconds = reader.OptionalInt("timeoutSeconds", 1, MaxTimeoutSeconds);
            var dependsOn = reader.OptionalStringArray("dependsOn") ?? [];

            var onFailure = OnFailurePolicy.Stop;
            var onFailureText = reader.OptionalString("onFailure");
            if (onFailureText is not null)
            {
                switch (onFailureText)
                {
                    case "stop":
                        onFailure = OnFailurePolicy.Stop;
                        break;
                    case "continue":
                        onFailure = OnFailurePolicy.Continue;
                        break;
                    default:
                        reader.AddError("onFailure", $"'onFailure' must be \"stop\" or \"continue\" (got '{onFailureText}').");
                        break;
                }
            }

            if (stepId is null || actionId is null || inputs is null || inputsReader is null)
            {
                ok = false;
                continue;
            }

            steps.Add(new StepDefinition(
                stepId,
                stepName ?? action!.DisplayName,
                actionId,
                inputs,
                inputsReader.Element.Clone(),
                timeoutSeconds is null ? null : TimeSpan.FromSeconds(timeoutSeconds.Value),
                dependsOn,
                onFailure));
        }

        if (!ok)
        {
            return null;
        }

        return ValidateDependencies(root, steps, stepReaders) ? steps : null;
    }

    private static bool ValidateDependencies(JsonObjectReader root, List<StepDefinition> steps, IReadOnlyList<JsonObjectReader> readers)
    {
        var byId = steps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var ok = true;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            foreach (var dependency in step.DependsOn)
            {
                if (dependency == step.Id)
                {
                    readers[i].AddError("dependsOn", $"Step '{step.Id}' cannot depend on itself.");
                    ok = false;
                }
                else if (!byId.ContainsKey(dependency))
                {
                    readers[i].AddError("dependsOn", $"Step '{step.Id}' depends on unknown step '{dependency}'.");
                    ok = false;
                }
            }
        }

        if (!ok)
        {
            return false;
        }

        // Cycle check: Kahn's algorithm; anything left over is in a cycle.
        var remaining = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        bool progressed;
        do
        {
            progressed = false;
            foreach (var id in remaining.ToList())
            {
                if (byId[id].DependsOn.All(d => !remaining.Contains(d)))
                {
                    remaining.Remove(id);
                    progressed = true;
                }
            }
        }
        while (progressed && remaining.Count > 0);

        if (remaining.Count > 0)
        {
            root.AddError("steps", $"These steps depend on each other in a cycle: {string.Join(", ", remaining.Order(StringComparer.Ordinal))}.");
            return false;
        }

        return true;
    }

    private static string ComputeHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugPattern();
}

/// <summary>Either a workflow or the errors that stopped it from loading.</summary>
public sealed class WorkflowLoadResult
{
    private WorkflowLoadResult(WorkflowDefinition? workflow, IReadOnlyList<WorkflowValidationError> errors)
    {
        Workflow = workflow;
        Errors = errors;
    }

    public WorkflowDefinition? Workflow { get; }

    public IReadOnlyList<WorkflowValidationError> Errors { get; }

    public bool IsValid => Workflow is not null;

    internal static WorkflowLoadResult Loaded(WorkflowDefinition workflow) => new(workflow, []);

    internal static WorkflowLoadResult Failed(IReadOnlyList<WorkflowValidationError> errors) => new(null, errors);

    internal static WorkflowLoadResult Failed(WorkflowValidationError error) => new(null, [error]);
}
