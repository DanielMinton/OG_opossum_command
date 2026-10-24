using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Exports;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Cloud;

/// <summary>
/// Checks the Power Automate flow definitions under <c>cloud/flows</c> against the app's own file contracts: every Parse JSON
/// action named in a flow's <c>contract.parseJsonActions</c> must accept what <see cref="StatusExporter"/> and
/// <see cref="AlertOutbox"/> really write, every <c>body('Parse_…')?['x']</c> must name a property the app writes, and the
/// <c>calendar.json</c> / <c>tomorrow.json</c> templates the flows compose must use exactly the keys the strict parsers accept.
/// </summary>
public sealed class CloudContractTests : IDisposable
{
    private const string SolutionFileName = "OG_opossum_command.slnx";

    private static readonly string[] CalendarTopKeys = ["schemaVersion", "generatedAt", "source", "timeZone", "events"];
    private static readonly string[] CalendarEventKeys = ["subject", "start", "end", "isAllDay", "location", "link", "showAs"];
    private static readonly string[] CarryoverTopKeys = ["schemaVersion", "forDate", "generatedAt", "items"];
    private static readonly string[] CarryoverItemKeys = ["title", "source", "due", "note"];
    private static readonly string[] NestedActionContainers = ["actions"];

    private readonly ExportsFixture _fx = new();
    private readonly SqliteHistoryStore _history;

    public CloudContractTests()
    {
        _history = _fx.OpenHistory();
    }

    public void Dispose()
    {
        _history.Dispose();
        _fx.Dispose();
    }

    public static TheoryData<string> FlowFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FlowsFolder(), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void The_cloud_flows_folder_has_flow_files()
    {
        Assert.NotEmpty(Directory.GetFiles(FlowsFolder(), "*.json"));
    }

    /// <summary>
    /// Power Automate refuses to save a flow whose trigger or action description is over 256 characters
    /// (ActionDescriptionTooLong), and it does so at import time, after you've mapped every connection. Placeholders get
    /// longer when they're filled in (a OneDrive folder id is 34 characters), so they're expanded to worst-case lengths first.
    /// </summary>
    [Theory]
    [MemberData(nameof(FlowFiles))]
    public void Every_trigger_and_action_description_fits_Power_Automates_limit(string fileName)
    {
        const int Budget = 256;
        static string Filled(string text) => text
            .Replace("__ONEDRIVE_OUTBOX_ID__", new string('x', 40), StringComparison.Ordinal)
            .Replace("__ONEDRIVE_FOLDER_ID__", new string('x', 40), StringComparison.Ordinal)
            .Replace("__SITE_URL__", new string('x', 90), StringComparison.Ordinal)
            .Replace("__USER_UPN__", new string('x', 40), StringComparison.Ordinal);
        using var flow = LoadFlow(fileName);
        var tooLong = new List<string>();
        void Walk(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("description") && property.Value.ValueKind == JsonValueKind.String
                        && Filled(property.Value.GetString()!).Length > Budget)
                    {
                        tooLong.Add($"{path} ({Filled(property.Value.GetString()!).Length} characters once filled in)");
                    }

                    Walk(property.Value, path + "." + property.Name);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{i++}]");
                }
            }
        }

        Walk(flow.RootElement.GetProperty("definition"), "definition");
        Assert.True(tooLong.Count == 0, "Descriptions over " + Budget + " characters: " + string.Join(", ", tooLong));
    }

    [Theory]
    [MemberData(nameof(FlowFiles))]
    public void Parse_JSON_actions_accept_what_the_app_writes(string fileName)
    {
        using var flow = LoadFlow(fileName);
        var root = flow.RootElement;
        var failures = new List<string>();
        var definitionText = root.GetProperty("definition").GetRawText();

        foreach (var entry in root.GetProperty("contract").GetProperty("parseJsonActions").EnumerateObject())
        {
            var file = entry.Name;
            var actionName = entry.Value.GetString()!;
            var action = FindAction(root.GetProperty("definition"), actionName);
            if (action is null)
            {
                failures.Add($"{file}: Parse JSON action '{actionName}' is not in the definition.");
                continue;
            }

            if (action.Value.GetProperty("type").GetString() != "ParseJson")
            {
                failures.Add($"{file}: action '{actionName}' is not of type ParseJson.");
                continue;
            }

            var schema = action.Value.GetProperty("inputs").GetProperty("schema");
            var samples = SamplesFor(file);
            if (samples.Count == 0)
            {
                failures.Add($"{file}: no app sample is known for this file name (expected status.json or outbox/*.json).");
                continue;
            }

            foreach (var sample in samples)
            {
                using var document = JsonDocument.Parse(sample);
                CheckAgainstSchema(document.RootElement, schema, $"{file} [{actionName}] $", failures);

                // Every body('<action>')?['x'] in the flow must name a property the app writes.
                var pattern = new Regex(@"body\('" + Regex.Escape(actionName) + @"'\)\?\['([^']+)'\]", RegexOptions.CultureInvariant);
                foreach (var name in pattern.Matches(definitionText).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
                {
                    if (!document.RootElement.TryGetProperty(name, out _))
                    {
                        failures.Add($"{file}: the flow reads body('{actionName}')?['{name}'], which the app never writes.");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct(StringComparer.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(FlowFiles))]
    public void Files_the_flow_writes_parse_with_the_apps_strict_parsers(string fileName)
    {
        using var flow = LoadFlow(fileName);
        var root = flow.RootElement;
        var definition = root.GetProperty("definition");
        var failures = new List<string>();

        foreach (var written in root.GetProperty("contract").GetProperty("writes").EnumerateArray().Select(w => w.GetString()!))
        {
            string[] topKeys;
            string[] itemKeys;
            string arrayKey;
            if (written == "calendar.json")
            {
                (topKeys, itemKeys, arrayKey) = (CalendarTopKeys, CalendarEventKeys, "events");
            }
            else if (written == "tomorrow.json")
            {
                (topKeys, itemKeys, arrayKey) = (CarryoverTopKeys, CarryoverItemKeys, "items");
            }
            else
            {
                continue;
            }

            var compose = ComposeWrittenTo(definition, written, failures);
            if (compose is null)
            {
                continue;
            }

            if (compose.Value.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"{written}: the Compose that feeds the file write is not a JSON object template, so its keys cannot be checked.");
                continue;
            }

            var composeKeys = compose.Value.EnumerateObject().Select(p => p.Name).ToList();
            AssertSameKeys(written, "top level", topKeys, composeKeys, failures);
            if (!compose.Value.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                failures.Add($"{written}: schemaVersion is not the number 1 (got {(compose.Value.TryGetProperty("schemaVersion", out var v) ? v.GetRawText() : "nothing")}).");
            }

            JsonElement? select = null;
            if (compose.Value.TryGetProperty(arrayKey, out var arrayExpression) && arrayExpression.ValueKind == JsonValueKind.String)
            {
                var source = Regex.Match(arrayExpression.GetString()!, @"body\('([^']+)'\)", RegexOptions.CultureInvariant);
                var selectAction = source.Success ? FindAction(definition, source.Groups[1].Value) : null;
                if (selectAction is not null
                    && selectAction.Value.GetProperty("type").GetString() == "Select"
                    && selectAction.Value.GetProperty("inputs").GetProperty("select").ValueKind == JsonValueKind.Object)
                {
                    select = selectAction.Value.GetProperty("inputs").GetProperty("select");
                    AssertSameKeys(written, $"each '{arrayKey}' entry", itemKeys, select.Value.EnumerateObject().Select(p => p.Name).ToList(), failures);
                }
                else
                {
                    failures.Add($"{written}: '{arrayKey}' is not fed by a Select with an object map ({arrayExpression.GetString()}).");
                }
            }
            else
            {
                failures.Add($"{written}: '{arrayKey}' is not an expression that yields an array.");
            }

            // Build one realistic file from the flow's own keys and literals, then let the app's parser judge it.
            var sample = BuildSample(compose.Value, arrayKey, select);
            if (written == "calendar.json")
            {
                var result = CalendarFileParser.Parse(sample, written);
                if (!result.IsValid)
                {
                    failures.Add($"{written}: a file in the flow's shape is rejected by CalendarFileParser: {string.Join(" | ", result.Errors)}{Environment.NewLine}{sample}");
                }
            }
            else
            {
                var result = CarryoverFileParser.Parse(sample, written);
                if (!result.IsValid)
                {
                    failures.Add($"{written}: a file in the flow's shape is rejected by CarryoverFileParser: {string.Join(" | ", result.Errors)}{Environment.NewLine}{sample}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FlowsFolder() => Path.Combine(RepoRoot(), "cloud", "flows");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find {SolutionFileName} above {AppContext.BaseDirectory}.");
    }

    private static JsonDocument LoadFlow(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FlowsFolder(), fileName)));

    /// <summary>Finds an action by name anywhere in the definition: scopes, conditions (both branches), loops and switches.</summary>
    private static JsonElement? FindAction(JsonElement container, string name)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in NestedActionContainers)
        {
            if (container.TryGetProperty(key, out var actions) && actions.ValueKind == JsonValueKind.Object)
            {
                foreach (var action in actions.EnumerateObject())
                {
                    if (action.NameEquals(name))
                    {
                        return action.Value;
                    }

                    var nested = FindAction(action.Value, name);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        if (container.TryGetProperty("else", out var otherwise))
        {
            var found = FindAction(otherwise, name);
            if (found is not null)
            {
                return found;
            }
        }

        if (container.TryGetProperty("default", out var fallback))
        {
            var found = FindAction(fallback, name);
            if (found is not null)
            {
                return found;
            }
        }

        if (container.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Object)
        {
            foreach (var branch in cases.EnumerateObject())
            {
                var found = FindAction(branch.Value, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> AllActions(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (container.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Object)
        {
            foreach (var action in actions.EnumerateObject())
            {
                yield return action.Value;
                foreach (var nested in AllActions(action.Value))
                {
                    yield return nested;
                }
            }
        }

        foreach (var branchKey in new[] { "else", "default" })
        {
            if (container.TryGetProperty(branchKey, out var branch))
            {
                foreach (var nested in AllActions(branch))
                {
                    yield return nested;
                }
            }
        }

        if (container.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Object)
        {
            foreach (var branch in cases.EnumerateObject())
            {
                foreach (var nested in AllActions(branch.Value))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>The inputs of the Compose whose output a OneDrive Create/Update file action writes as <paramref name="fileName"/>.</summary>
    private static JsonElement? ComposeWrittenTo(JsonElement definition, string fileName, List<string> failures)
    {
        foreach (var action in AllActions(definition))
        {
            if (!action.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object
                || !inputs.TryGetProperty("host", out var host)
                || host.GetProperty("operationId").GetString() != "CreateFile"
                || !inputs.TryGetProperty("parameters", out var parameters)
                || !parameters.TryGetProperty("name", out var name) || name.GetString() != fileName)
            {
                continue;
            }

            var body = parameters.GetProperty("body").GetString() ?? string.Empty;
            var match = Regex.Match(body, @"outputs\('([^']+)'\)", RegexOptions.CultureInvariant);
            var compose = match.Success ? FindAction(definition, match.Groups[1].Value) : null;
            if (compose is null || compose.Value.GetProperty("type").GetString() != "Compose")
            {
                failures.Add($"{fileName}: the Create file body '{body}' is not the output of a Compose action.");
                return null;
            }

            return compose.Value.GetProperty("inputs");
        }

        failures.Add($"{fileName}: no OneDrive CreateFile action writes this file.");
        return null;
    }

    private static void AssertSameKeys(string file, string where, string[] expected, List<string> actual, List<string> failures)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
        var extra = actual.Except(expected, StringComparer.Ordinal).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            failures.Add($"{file} ({where}): missing [{string.Join(", ", missing)}], not accepted by the parser [{string.Join(", ", extra)}].");
        }
    }

    /// <summary>
    /// Turns the flow's template into one concrete file: literals are kept as the flow writes them, expressions become a
    /// canned value of the shape that expression produces (keyed by property name), and the array gets one entry built from
    /// the Select map. A key the parser does not know gets a string, so the parser reports it.
    /// </summary>
    private static string BuildSample(JsonElement compose, string arrayKey, JsonElement? select)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in compose.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals(arrayKey))
                {
                    writer.WriteStartArray();
                    if (select is not null)
                    {
                        writer.WriteStartObject();
                        foreach (var field in select.Value.EnumerateObject())
                        {
                            writer.WritePropertyName(field.Name);
                            WriteValue(writer, field.Name, field.Value);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    WriteValue(writer, property.Name, property.Value);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, JsonElement template)
    {
        if (template.ValueKind != JsonValueKind.String || !(template.GetString() ?? string.Empty).StartsWith('@'))
        {
            template.WriteTo(writer);
            return;
        }

        switch (name)
        {
            case "generatedAt":
                writer.WriteStringValue("2026-10-23T23:30:05.1234567Z");
                break;
            case "forDate":
                writer.WriteStringValue("2026-10-26");
                break;
            case "start":
                writer.WriteStringValue("2026-10-23T16:00:00+00:00");
                break;
            case "end":
                writer.WriteStringValue("2026-10-23T16:30:00+00:00");
                break;
            case "isAllDay":
                writer.WriteBooleanValue(false);
                break;
            case "subject":
                writer.WriteStringValue("Team stand-up");
                break;
            case "location":
                writer.WriteStringValue("Microsoft Teams Meeting");
                break;
            case "link":
                writer.WriteStringValue("https://outlook.office365.com/owa/?itemid=EXAMPLE");
                break;
            case "showAs":
                writer.WriteStringValue("busy");
                break;
            case "title":
                writer.WriteStringValue("Finish budget memo");
                break;
            case "source":
                writer.WriteStringValue("todo");
                break;
            case "due":
                writer.WriteStringValue("2026-10-24");
                break;
            case "note":
                writer.WriteStringValue("Overdue");
                break;
            default:
                writer.WriteStringValue("unexpected property from the flow");
                break;
        }
    }

    /// <summary>A tiny JSON Schema check: <c>type</c> (string or list), <c>required</c>, <c>properties</c>, <c>items</c>.</summary>
    private static void CheckAgainstSchema(JsonElement value, JsonElement schema, string path, List<string> failures)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()!).ToList()
                : [type.GetString()!];
            if (!allowed.Any(t => KindMatches(value, t)))
            {
                failures.Add($"{path}: the app writes {value.ValueKind} ({Shorten(value.GetRawText())}) but the schema wants {string.Join("|", allowed)}.");
                return;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    if (!value.TryGetProperty(name, out _))
                    {
                        failures.Add($"{path}: the schema requires '{name}', which the app does not write.");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(property.Name, out var child))
                    {
                        CheckAgainstSchema(child, property.Value, $"{path}.{property.Name}", failures);
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                CheckAgainstSchema(item, items, string.Create(CultureInfo.InvariantCulture, $"{path}[{index++}]"), failures);
            }
        }
    }

    private static bool KindMatches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static string Shorten(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");

    /// <summary>Real files produced by the app's own writers for <paramref name="file"/> (a contract key).</summary>
    private List<string> SamplesFor(string file) => file switch
    {
        "status.json" => [EmptyStatusSample(), BusyStatusSample()],
        "outbox/*.json" or @"outbox\*.json" => AlertSamples(),
        _ => [],
    };

    private static string EmptyStatusSample()
    {
        using var fx = new ExportsFixture();
        using var history = fx.OpenHistory();
        return new StatusExporter(fx.WorkProfile(), fx.Paths, history, () => [], () => [], fx.Clock).BuildJson(fx.Clock.GetUtcNow());
    }

    /// <summary>status.json with every array non-empty, including a null step.</summary>
    private string BusyStatusSample()
    {
        var now = _fx.Clock.GetUtcNow();
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.NeedsAttention, now.AddHours(-1), workflowId: "start-workday", workflowName: "Start Workday"));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddMinutes(-30), workflowId: "end-of-day", workflowName: "End of Day", stepMessage: "Exit code 2"));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Failed, now.AddMinutes(-20), workflowId: "no-step", workflowName: "No step", error: "Failed before a step", stepStatus: StepStatus.Completed));
        _history.SaveExecution(ExportsFixture.Run(ExecutionStatus.Completed, now.AddHours(-2)));

        var active = new List<ExecutionSnapshot> { ExportsFixture.Run(ExecutionStatus.Running, now.AddMinutes(-2), workflowId: "focus-mode") };
        var profile = _fx.WorkProfile();
        var workflow = TestSupport.LoadValid(_fx.Registry, profile, $$"""
            {
              "schemaVersion": 2, "id": "briefing", "name": "Morning briefing", "profileId": "work",
              "trigger": { "type": "schedule", "at": ["13:30", "17:00"], "timeZone": "{{ExportsFixture.Zone.Id}}" },
              "steps": [ { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.invalid/" } } ]
            }
            """);
        var workflows = new List<ProfileWorkflows> { new(profile, [workflow], []) };

        var json = new StatusExporter(profile, _fx.Paths, _history, () => active, () => workflows, _fx.Clock).BuildJson(now);
        using (var document = JsonDocument.Parse(json))
        {
            foreach (var array in new[] { "needsAttention", "failed", "runningNow", "scheduledToday" })
            {
                Assert.NotEmpty(document.RootElement.GetProperty(array).EnumerateArray());
            }
        }

        return json;
    }

    private List<string> AlertSamples()
    {
        var outbox = new AlertOutbox(_fx.WorkProfile(), _fx.Paths, _fx.Clock);
        var at = _fx.Clock.GetUtcNow();
        var withStep = new AlertRecord(AlertEvents.RunFailed, Guid.NewGuid(), at, ProfileId.Work, "start-workday", "Start Workday", Guid.NewGuid(), "open-erp", "Exit code 1 from [path].", "x.json");
        var withoutStep = withStep with { Event = AlertEvents.RunNeedsAttention, EventId = Guid.NewGuid(), Step = null };
        return [outbox.BuildJson(withStep), outbox.BuildJson(withoutStep)];
    }
}
