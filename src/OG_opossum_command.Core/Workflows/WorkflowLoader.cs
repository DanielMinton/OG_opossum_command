using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Scheduling;

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
        AllowDuplicateProperties = false,
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
        root.RejectUnknownProperties("schemaVersion", "id", "name", "description", "profileId", "trigger", "execution", "requiresApproval", "steps", "aliases");

        var schemaVersion = root.RequiredInt("schemaVersion", 1, int.MaxValue);
        if (schemaVersion is not null && (schemaVersion < WorkflowDefinition.OldestSupportedSchemaVersion || schemaVersion > WorkflowDefinition.CurrentSchemaVersion))
        {
            root.AddError("schemaVersion", $"This version of the app understands schemaVersion {WorkflowDefinition.OldestSupportedSchemaVersion} to {WorkflowDefinition.CurrentSchemaVersion} (file says {schemaVersion}).");
        }

        // Features that arrived with schema v2 are rejected in a v1 file so an old file never silently gains behaviour (ADR-032).
        var isV2 = schemaVersion >= 2;

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

        var trigger = ValidateTrigger(root.RequiredObject("trigger"), isV2);

        var aliases = root.OptionalStringArray("aliases") ?? [];
        if (aliases.Count > 0 && !isV2)
        {
            root.AddError("aliases", "'aliases' needs \"schemaVersion\": 2.");
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias) || alias.Length > 40)
            {
                root.AddError("aliases", $"Alias '{alias}' must be 1-40 characters.");
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

        var steps = ValidateSteps(root, profile, isV2);
        if (id is null || name is null || steps is null || trigger is null || schemaVersion is null)
        {
            return null;
        }

        return new WorkflowDefinition(
            schemaVersion.Value,
            id,
            name,
            description,
            profile.Id,
            trigger,
            maxParallel,
            onDuplicate,
            requiresApproval,
            steps,
            aliases.Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            source,
            hash);
    }

    private static TriggerDefinition? ValidateTrigger(JsonObjectReader? trigger, bool isV2)
    {
        if (trigger is null)
        {
            return null;
        }

        var type = trigger.RequiredString("type");
        switch (type)
        {
            case null:
                return null;
            case "manual":
                trigger.RejectUnknownProperties("type");
                return TriggerDefinition.Manual;
            case "schedule":
                if (!isV2)
                {
                    trigger.AddError("type", "A \"schedule\" trigger needs \"schemaVersion\": 2.");
                    return null;
                }

                var schedule = ValidateSchedule(trigger);
                return schedule is null ? null : new TriggerDefinition(TriggerType.Schedule, schedule);
            default:
                trigger.AddError("type", $"'trigger.type' must be \"manual\" or \"schedule\" (got '{type}').");
                return null;
        }
    }

    private static ScheduleDefinition? ValidateSchedule(JsonObjectReader trigger)
    {
        trigger.RejectUnknownProperties("type", "at", "days", "timeZone", "missedRuns", "catchUpLimit", "expiresAfterMinutes");
        var ok = true;

        var times = new List<TimeOnly>();
        var atTexts = trigger.OptionalStringArray("at");
        if (atTexts is null)
        {
            if (!trigger.Has("at"))
            {
                trigger.AddError("at", "A schedule needs 'at': an array of wall-clock times such as [\"08:30\", \"13:00\"].");
            }

            ok = false;
        }
        else
        {
            if (atTexts.Count == 0 || atTexts.Count > ScheduleDefinition.MaxTimes)
            {
                trigger.AddError("at", $"'at' must list 1 to {ScheduleDefinition.MaxTimes} times.");
                ok = false;
            }

            foreach (var text in atTexts)
            {
                if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    times.Add(time);
                }
                else
                {
                    trigger.AddError("at", $"'{text}' is not a time in 24-hour HH:mm form (for example \"08:30\").");
                    ok = false;
                }
            }
        }

        var days = new List<DayOfWeek>();
        var dayTexts = trigger.OptionalStringArray("days");
        if (dayTexts is null)
        {
            if (trigger.Has("days"))
            {
                ok = false;
            }
            else
            {
                days.AddRange(Enum.GetValues<DayOfWeek>());
            }
        }
        else
        {
            if (dayTexts.Count == 0)
            {
                trigger.AddError("days", "'days' must name at least one weekday (\"mon\" … \"sun\"), or be omitted for every day.");
                ok = false;
            }

            foreach (var text in dayTexts)
            {
                if (ScheduleDefinition.TryParseDay(text, out var day))
                {
                    days.Add(day);
                }
                else
                {
                    trigger.AddError("days", $"'{text}' is not a weekday. Use \"mon\", \"tue\", \"wed\", \"thu\", \"fri\", \"sat\", \"sun\".");
                    ok = false;
                }
            }
        }

        var timeZoneId = trigger.RequiredString("timeZone");
        if (timeZoneId is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            trigger.AddError("timeZone", $"'timeZone' \"{timeZoneId}\" is not a time zone this PC knows. Use a Windows ID such as \"Pacific Standard Time\" or an IANA ID such as \"America/Los_Angeles\".");
            timeZoneId = null;
        }

        var policy = MissedRunPolicy.Skip;
        var policyText = trigger.OptionalString("missedRuns");
        if (policyText is not null && !ScheduleDefinition.TryParsePolicy(policyText, out policy))
        {
            trigger.AddError("missedRuns", $"'missedRuns' must be \"skip\", \"run-once\", or \"catch-up-limited\" (got '{policyText}').");
            ok = false;
        }

        var catchUpLimit = trigger.OptionalInt("catchUpLimit", 1, ScheduleDefinition.MaxCatchUpLimit);
        if (policy == MissedRunPolicy.CatchUpLimited && catchUpLimit is null)
        {
            if (!trigger.Has("catchUpLimit"))
            {
                trigger.AddError("catchUpLimit", $"\"catch-up-limited\" needs 'catchUpLimit' (1 to {ScheduleDefinition.MaxCatchUpLimit}).");
            }

            ok = false;
        }
        else if (policy != MissedRunPolicy.CatchUpLimited && trigger.Has("catchUpLimit"))
        {
            trigger.AddError("catchUpLimit", "'catchUpLimit' only applies with \"missedRuns\": \"catch-up-limited\".");
            ok = false;
        }

        var expiresValue = trigger.OptionalInt("expiresAfterMinutes", 1, ScheduleDefinition.MaxExpiresAfterMinutes);
        if (trigger.Has("expiresAfterMinutes") && expiresValue is null)
        {
            ok = false;
        }

        var expires = expiresValue ?? ScheduleDefinition.DefaultExpiresAfterMinutes;

        if (!ok || timeZoneId is null)
        {
            return null;
        }

        return new ScheduleDefinition(
            times.Distinct().Order().ToList(),
            days.Distinct().Order().ToList(),
            timeZoneId,
            policy,
            policy == MissedRunPolicy.CatchUpLimited ? catchUpLimit!.Value : 1,
            TimeSpan.FromMinutes(expires));
    }

    private List<StepDefinition>? ValidateSteps(JsonObjectReader root, Profile profile, bool isV2)
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
            reader.RejectUnknownProperties("id", "name", "action", "inputs", "timeoutSeconds", "dependsOn", "onFailure", "locks");

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

            var locks = new List<string>();
            var lockTexts = reader.OptionalStringArray("locks");
            if (lockTexts is not null && !isV2)
            {
                reader.AddError("locks", "'locks' needs \"schemaVersion\": 2.");
                ok = false;
            }
            else if (lockTexts is not null)
            {
                foreach (var text in lockTexts)
                {
                    var problem = Engine.ResourceLocks.Normalize(text, out var key);
                    if (problem is null)
                    {
                        locks.Add(key);
                    }
                    else
                    {
                        reader.AddError("locks", problem);
                        ok = false;
                    }
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
                timeoutSeconds is null ? null : TimeSpan.FromSeconds(timeoutSeconds.Value),
                dependsOn,
                onFailure,
                locks.Distinct(StringComparer.Ordinal).ToList()));
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
