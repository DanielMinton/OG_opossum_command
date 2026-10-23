using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Workflows;

public sealed class WorkflowLoaderTests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly ActionRegistry _registry = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);
    private readonly Profile _work;
    private readonly Profile _personal;

    public WorkflowLoaderTests()
    {
        _work = _root.Profile(ProfileId.Work, _registry);
        _personal = _root.Profile(ProfileId.Personal, _registry);
    }

    private const string Minimal = """
        {
          "schemaVersion": 1,
          "id": "demo",
          "name": "Demo",
          "profileId": "work",
          "trigger": { "type": "manual" },
          "steps": [
            { "id": "open", "action": "windows.url.open", "inputs": { "url": "https://example.com/" } }
          ]
        }
        """;

    [Fact]
    public void Loads_a_minimal_valid_workflow_with_defaults()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, Minimal);

        Assert.Equal("demo", workflow.Id);
        Assert.Equal(ProfileId.Work, workflow.ProfileId);
        Assert.Equal(1, workflow.MaxParallelSteps);
        Assert.Equal(OnDuplicatePolicy.FocusExisting, workflow.OnDuplicate);
        Assert.False(workflow.RequiresApproval);
        var step = Assert.Single(workflow.Steps);
        Assert.Equal("Open web page", step.Name);
        Assert.Null(step.Timeout);
        Assert.Equal(OnFailurePolicy.Stop, step.OnFailure);
        Assert.IsType<UrlOpenInputs>(step.Inputs);
        Assert.Equal(64, workflow.ContentHash.Length);
    }

    [Fact]
    public void All_shipped_examples_validate_in_their_own_profile()
    {
        var loader = new WorkflowLoader(_registry);
        var work = Directory.GetFiles(Path.Combine(TempDataRoot.ExamplesRoot, "work"), "*.json");
        var personal = Directory.GetFiles(Path.Combine(TempDataRoot.ExamplesRoot, "personal"), "*.json");

        Assert.Equal(2, work.Length);
        Assert.Equal(2, personal.Length);
        foreach (var file in work)
        {
            var result = loader.LoadFile(file, _work);
            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("EXAMPLE", result.Workflow!.Description, StringComparison.Ordinal);
        }

        foreach (var file in personal)
        {
            var result = loader.LoadFile(file, _personal);
            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        }
    }

    [Fact]
    public void Rejects_a_work_example_placed_in_the_personal_folder()
    {
        var file = Path.Combine(TempDataRoot.ExamplesRoot, "work", "start-workday.json");

        var result = new WorkflowLoader(_registry).LoadFile(file, _personal);

        var error = Assert.Single(result.Errors);
        Assert.Equal("profileId", error.JsonPath);
        Assert.Contains("Personal profile folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_properties_with_the_allowed_list()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Minimal.Replace("\"name\": \"Demo\"", "\"name\": \"Demo\", \"nmae\": \"typo\"", StringComparison.Ordinal));

        var error = Assert.Single(errors);
        Assert.Equal("nmae", error.JsonPath);
        Assert.Contains("Unknown property 'nmae'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Allowed:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_newer_schema_version()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Minimal.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 3", StringComparison.Ordinal));

        var error = Assert.Single(errors);
        Assert.Equal("schemaVersion", error.JsonPath);
        Assert.Contains("understands schemaVersion 1 to 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_requiresApproval_true_as_reserved()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Minimal.Replace("\"trigger\"", "\"requiresApproval\": true, \"trigger\"", StringComparison.Ordinal));

        var error = Assert.Single(errors);
        Assert.Equal("requiresApproval", error.JsonPath);
        Assert.Contains("reserved", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:display")]
    [InlineData("example.com")]
    public void Rejects_non_http_urls(string url)
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Minimal.Replace("https://example.com/", url, StringComparison.Ordinal));

        var error = Assert.Single(errors);
        Assert.Equal("steps[0].inputs.url", error.JsonPath);
        Assert.Contains("http:// or https://", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_process_arguments_given_as_a_shell_string()
    {
        const string json = """
            {
              "schemaVersion": 1, "id": "p", "name": "P", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [
                { "id": "run", "action": "windows.process.launch",
                  "inputs": { "path": "%SystemRoot%\\System32\\notepad.exe", "arguments": "a.txt b.txt" } }
              ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _work, json);

        var error = Assert.Single(errors);
        Assert.Equal("steps[0].inputs.arguments", error.JsonPath);
        Assert.Contains("array of strings", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notepad.exe", "full path")]
    [InlineData("%SystemRoot%\\System32\\script.cmd", ".exe")]
    public void Rejects_process_paths_that_are_relative_or_not_exe(string path, string expectedFragment)
    {
        var json = $$"""
            {
              "schemaVersion": 1, "id": "p", "name": "P", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [ { "id": "run", "action": "windows.process.launch", "inputs": { "path": "{{path.Replace("\\", "\\\\", StringComparison.Ordinal)}}" } } ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _work, json);

        var error = Assert.Single(errors);
        Assert.Equal("steps[0].inputs.path", error.JsonPath);
        Assert.Contains(expectedFragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_actions_and_lists_the_known_ones()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Minimal.Replace("windows.url.open", "windows.registry.write", StringComparison.Ordinal));

        var error = Assert.Single(errors);
        Assert.Equal("steps[0].action", error.JsonPath);
        Assert.Contains("windows.folder.open, windows.process.launch, windows.script.run, windows.url.open", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_actions_outside_the_profile_allowlist_at_load_time()
    {
        var restricted = _root.Profile(ProfileId.Work, FolderOpenAction.ActionId);

        var errors = TestSupport.LoadInvalid(_registry, restricted, Minimal);

        var error = Assert.Single(errors);
        Assert.Equal("steps[0].action", error.JsonPath);
        Assert.Contains("not allowed in the Work profile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_dependency_cycles_unknown_dependencies_and_duplicate_step_ids()
    {
        const string cycle = """
            {
              "schemaVersion": 1, "id": "c", "name": "C", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [
                { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://a.example/" }, "dependsOn": ["b"] },
                { "id": "b", "action": "windows.url.open", "inputs": { "url": "https://b.example/" }, "dependsOn": ["a"] }
              ]
            }
            """;
        const string unknown = """
            {
              "schemaVersion": 1, "id": "u", "name": "U", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [ { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://a.example/" }, "dependsOn": ["nope"] } ]
            }
            """;
        const string duplicate = """
            {
              "schemaVersion": 1, "id": "d", "name": "D", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [
                { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://a.example/" } },
                { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://b.example/" } }
              ]
            }
            """;

        Assert.Contains(TestSupport.LoadInvalid(_registry, _work, cycle), e => e.JsonPath == "steps" && e.Message.Contains("cycle: a, b", StringComparison.Ordinal));
        Assert.Contains(TestSupport.LoadInvalid(_registry, _work, unknown), e => e.JsonPath == "steps[0].dependsOn" && e.Message.Contains("unknown step 'nope'", StringComparison.Ordinal));
        Assert.Contains(TestSupport.LoadInvalid(_registry, _work, duplicate), e => e.JsonPath == "steps[1].id" && e.Message.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_invalid_json_with_a_line_number_instead_of_throwing()
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, "{ \"schemaVersion\": 1,\n  \"id\": }");

        var error = Assert.Single(errors);
        Assert.Equal("$", error.JsonPath);
        Assert.StartsWith("Not valid JSON (line 2", error.Message, StringComparison.Ordinal);
        Assert.Equal("<test>: $: " + error.Message, error.ToString());
    }

    [Fact]
    public void Collects_every_error_in_one_pass()
    {
        const string json = """
            {
              "schemaVersion": 1, "id": "Bad Id", "name": "", "profileId": "nobody", "trigger": { "type": "cron" },
              "execution": { "maxParallelSteps": 99, "onDuplicate": "queue" },
              "steps": [ { "id": "a", "action": "windows.url.open", "inputs": { "url": "https://a.example/" }, "timeoutSeconds": 0, "onFailure": "retry" } ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _work, json);

        var paths = errors.Select(e => e.JsonPath).ToList();
        Assert.Contains("id", paths);
        Assert.Contains("name", paths);
        Assert.Contains("profileId", paths);
        Assert.Contains("trigger.type", paths);
        Assert.Contains("execution.maxParallelSteps", paths);
        Assert.Contains("execution.onDuplicate", paths);
        Assert.Contains("steps[0].timeoutSeconds", paths);
        Assert.Contains("steps[0].onFailure", paths);
    }

    [Fact]
    public void LoadProfile_reports_duplicate_ids_across_files_and_keeps_the_first()
    {
        Directory.CreateDirectory(_work.WorkflowsDirectory);
        File.WriteAllText(Path.Combine(_work.WorkflowsDirectory, "a.json"), Minimal);
        File.WriteAllText(Path.Combine(_work.WorkflowsDirectory, "b.json"), Minimal);
        File.WriteAllText(Path.Combine(_work.WorkflowsDirectory, "notes.txt"), "ignored");

        var loaded = new WorkflowLoader(_registry).LoadProfile(_work);

        Assert.Single(loaded.Workflows);
        Assert.EndsWith("a.json", loaded.Workflows[0].SourcePath, StringComparison.OrdinalIgnoreCase);
        var error = Assert.Single(loaded.Errors);
        Assert.EndsWith("b.json", error.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already used by a.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadProfile_with_a_missing_folder_yields_nothing_and_no_errors()
    {
        var loaded = new WorkflowLoader(_registry).LoadProfile(_personal);

        Assert.Empty(loaded.Workflows);
        Assert.Empty(loaded.Errors);
    }

    public void Dispose() => _root.Dispose();
}
