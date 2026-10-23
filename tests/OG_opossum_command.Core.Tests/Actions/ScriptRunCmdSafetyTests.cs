using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Actions;

/// <summary>
/// cmd.exe parses its command line again after <c>/c</c>, so for the cmd interpreter the argument array only stays an
/// argument array when no argument carries a cmd metacharacter (SEC-1, ADR-031). Real runs are rule 7 throwaways.
/// </summary>
[Collection(RealProcesses.Name)]
public sealed class ScriptRunCmdSafetyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly Profile _personal;

    public ScriptRunCmdSafetyTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _personal = _root.Profile(ProfileId.Personal, _registry);
    }

    private static string Workflow(string inputs) => $$"""
        {
          "schemaVersion": 2, "id": "wf", "name": "WF", "profileId": "personal", "trigger": { "type": "manual" },
          "steps": [ { "id": "run", "action": "windows.script.run", "inputs": {{inputs}} } ]
        }
        """;

    private async Task<ExecutionSnapshot> RunAsync(ActionRegistry registry, Profile profile, string inputs)
    {
        using var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        using var engine = new WorkflowEngine(registry, history);
        var workflow = TestSupport.LoadValid(registry, profile, Workflow(inputs));
        var started = engine.Start(workflow, profile);
        using var cts = new CancellationTokenSource(Timeout);
        return await engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
    }

    [Fact]
    public async Task A_real_cmd_script_in_a_folder_with_a_space_receives_an_argument_with_a_space()
    {
        var real = ActionRegistry.CreateDefault(new LauncherSet(new WindowsProcessLauncher(), _launchers.Urls, _launchers.Folders, _launchers.Dashboard));
        var scripts = Path.Combine(_root.Root, "my scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "echo-args.cmd"), "@echo off\r\necho one=[%~1] two=[%~2]\r\nexit /b 0\r\n");
        var profile = new Profile(ProfileId.Personal, _root.Paths.GetProfileWorkflowsDirectory(ProfileId.Personal), scripts, real.Ids);

        var result = await RunAsync(real, profile, """{ "interpreter": "cmd", "script": "echo-args.cmd", "arguments": ["b c", "plain"] }""");

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Contains("one=[b c] two=[plain]", result.Steps[0].Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData(">out.txt")]
    [InlineData("<in.txt")]
    [InlineData("a^b")]
    [InlineData("%USERPROFILE%")]
    [InlineData("!x!")]
    [InlineData("say \"hi\"")]
    [InlineData("(x)")]
    [InlineData("line\nbreak")]
    public void Cmd_arguments_with_shell_metacharacters_are_refused_at_load(string argument)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(argument);

        var errors = TestSupport.LoadInvalid(_registry, _personal, Workflow($$"""{ "interpreter": "cmd", "script": "hello.cmd", "arguments": ["ok", {{json}}] }"""));

        Assert.Contains(errors, e => e.Message.Contains("powershell interpreter", StringComparison.Ordinal));
    }

    [Fact]
    public void Cmd_script_names_with_spaces_are_refused_at_load()
    {
        var errors = TestSupport.LoadInvalid(_registry, _personal, Workflow("""{ "interpreter": "cmd", "script": "Hello World.bat" }"""));

        Assert.Contains(errors, e => e.Message.Contains("may not contain spaces", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerShell_arguments_are_not_subject_to_the_cmd_rules()
    {
        var workflow = TestSupport.LoadValid(_registry, _personal, Workflow("""{ "interpreter": "powershell", "script": "My Report.ps1", "arguments": ["a&b", "%TEMP%"] }"""));

        var typed = Assert.IsType<ScriptRunInputs>(workflow.Steps[0].Inputs);
        Assert.Equal(["a&b", "%TEMP%"], typed.Arguments);
    }

    [Fact]
    public async Task Cmd_starts_in_the_scripts_folder_with_a_relative_script_name()
    {
        _root.WriteScript(ProfileId.Personal, "hello.cmd", "@echo hi\r\n");

        var result = await RunAsync(_registry, _personal, """{ "interpreter": "cmd", "script": "hello.cmd", "arguments": ["x y"] }""");

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var request = Assert.Single(_launchers.Processes.Requests);
        Assert.Equal(["/d", "/c", @".\hello.cmd", "x y"], request.Arguments);
        Assert.Equal(_personal.ScriptsDirectory, request.WorkingDirectory);
    }

    [Fact]
    public async Task Cmd_arguments_are_checked_again_at_run_time_before_any_process_starts()
    {
        _root.WriteScript(ProfileId.Personal, "hello.cmd", "@echo hi\r\n");
        var action = new ScriptRunAction(_launchers.Processes);
        var context = new ActionContext(Guid.NewGuid(), "wf", "run", _personal);

        var result = await action.ExecuteAsync(new ScriptRunInputs(ScriptInterpreter.Cmd, "hello.cmd", ["a&calc"]), context, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("script-unsafe-for-cmd", result.ErrorCode);
        Assert.Empty(_launchers.Processes.Requests);
    }

    public void Dispose() => _root.Dispose();
}
