using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Actions;

/// <summary>
/// Trusted scripts only (ADR-031). Real runs use a .cmd through cmd.exe (a rule 7 throwaway); PowerShell behaviour,
/// including the execution-policy refusal, is exercised with fakes because policy on a managed PC may block .ps1
/// files and the app must never try to get around that.
/// </summary>
[Collection(RealProcesses.Name)]
public sealed class ScriptRunActionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly Profile _personal;

    public ScriptRunActionTests()
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

    private async Task<ExecutionSnapshot> RunAsync(ActionRegistry registry, string inputs)
    {
        using var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        using var engine = new WorkflowEngine(registry, history);
        var profile = _root.Profile(ProfileId.Personal, registry);
        var workflow = TestSupport.LoadValid(registry, profile, Workflow(inputs));
        var started = engine.Start(workflow, profile);
        using var cts = new CancellationTokenSource(Timeout);
        return await engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);
    }

    [Fact]
    public void Registered_as_a_non_idempotent_built_in_action()
    {
        Assert.True(_registry.TryGet(ScriptRunAction.ActionId, out var action));
        Assert.Equal(IdempotencyClass.NonIdempotent, action.Idempotency);
        Assert.Equal("Run script", action.DisplayName);
    }

    [Theory]
    [InlineData("""{ "interpreter": "cmd", "script": "hello.cmd" }""")]
    [InlineData("""{ "interpreter": "cmd", "script": "Hello_World.bat", "arguments": ["a", "b c"] }""")]
    [InlineData("""{ "interpreter": "powershell", "script": "report.ps1" }""")]
    public void Accepts_a_bare_script_name_with_the_matching_extension(string inputs)
    {
        var workflow = TestSupport.LoadValid(_registry, _personal, Workflow(inputs));

        var typed = Assert.IsType<ScriptRunInputs>(workflow.Steps[0].Inputs);
        Assert.False(typed.Script.Contains('\\', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "interpreter": "cmd", "script": "..\\outside.cmd" }""", "bare file name")]
    [InlineData("""{ "interpreter": "cmd", "script": "C:\\Windows\\System32\\evil.cmd" }""", "bare file name")]
    [InlineData("""{ "interpreter": "cmd", "script": "sub/hello.cmd" }""", "bare file name")]
    [InlineData("""{ "interpreter": "cmd", "script": "hello.ps1" }""", ".cmd or .bat")]
    [InlineData("""{ "interpreter": "powershell", "script": "hello.cmd" }""", ".ps1")]
    [InlineData("""{ "interpreter": "bash", "script": "hello.sh" }""", "\"powershell\" or \"cmd\"")]
    [InlineData("""{ "interpreter": "cmd", "script": "hello.cmd", "arguments": "a b" }""", "array of strings")]
    [InlineData("""{ "interpreter": "cmd", "script": "hello.cmd", "executionPolicy": "Bypass" }""", "Unknown property")]
    [InlineData("""{ "script": "hello.cmd" }""", "interpreter")]
    public void Rejects_paths_wrong_extensions_unknown_interpreters_and_stray_properties(string inputs, string expectedFragment)
    {
        var errors = TestSupport.LoadInvalid(_registry, _personal, Workflow(inputs));

        Assert.Contains(errors, e => e.Message.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_a_real_cmd_script_from_the_profile_scripts_folder_and_captures_its_output()
    {
        _root.WriteScript(ProfileId.Personal, "hello.cmd", "@echo off\r\necho hello %1 %2\r\necho oops 1>&2\r\nexit /b 0\r\n");
        var real = ActionRegistry.CreateDefault(new LauncherSet(new WindowsProcessLauncher(), _launchers.Urls, _launchers.Folders, _launchers.Dashboard));

        var result = await RunAsync(real, """{ "interpreter": "cmd", "script": "hello.cmd", "arguments": ["first", "second"] }""");

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var step = result.Steps[0];
        Assert.Contains("hello first second", step.Output, StringComparison.Ordinal);
        Assert.Contains("[stderr]", step.Output, StringComparison.Ordinal);
        Assert.Contains("oops", step.Output, StringComparison.Ordinal);
        Assert.Contains("exit code 0", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_cmd_script_that_exits_non_zero_fails_the_step_with_exit_code()
    {
        _root.WriteScript(ProfileId.Personal, "fail.cmd", "@exit /b 4\r\n");
        var real = ActionRegistry.CreateDefault(new LauncherSet(new WindowsProcessLauncher(), _launchers.Urls, _launchers.Folders, _launchers.Dashboard));

        var result = await RunAsync(real, """{ "interpreter": "cmd", "script": "fail.cmd" }""");

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("exit-code", result.Steps[0].ErrorCode);
        Assert.Contains("code 4", result.Steps[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_script_fails_before_any_process_starts()
    {
        var result = await RunAsync(_registry, """{ "interpreter": "cmd", "script": "nope.cmd" }""");

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("script-not-found", result.Steps[0].ErrorCode);
        Assert.Contains(_personal.ScriptsDirectory, result.Steps[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_launchers.Processes.Requests);
    }

    [Fact]
    public async Task PowerShell_is_started_with_an_explicit_argument_array_and_never_with_an_execution_policy_switch()
    {
        var path = _root.WriteScript(ProfileId.Personal, "report.ps1", "Write-Output 'hi'\r\n");
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 0, standardOutput: "hi");

        var result = await RunAsync(_registry, """{ "interpreter": "powershell", "script": "report.ps1", "arguments": ["-Name", "x"] }""");

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var request = Assert.Single(_launchers.Processes.Requests);
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", request.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-File", path, "-Name", "x"], request.Arguments);
        Assert.DoesNotContain(request.Arguments, a => a.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase));
        Assert.True(request.CaptureOutput);
        Assert.Equal(_personal.ScriptsDirectory, request.WorkingDirectory);
    }

    [Fact]
    public async Task An_execution_policy_refusal_is_reported_clearly_as_execution_policy_blocked()
    {
        _root.WriteScript(ProfileId.Personal, "report.ps1", "Write-Output 'hi'\r\n");
        const string refusal = "File C:\\x\\report.ps1 cannot be loaded because running scripts is disabled on this system. For more information, see about_Execution_Policies at https://go.microsoft.com/fwlink/?LinkID=135170.\r\n    + CategoryInfo          : SecurityError: (:) [], ParentContainsErrorRecordException\r\n    + FullyQualifiedErrorId : UnauthorizedAccess";
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 1, standardError: refusal);

        var result = await RunAsync(_registry, """{ "interpreter": "powershell", "script": "report.ps1" }""");

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ScriptRunAction.ExecutionPolicyBlockedCode, result.Steps[0].ErrorCode);
        Assert.Contains("execution policy", result.Steps[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never overrides", result.Steps[0].Message, StringComparison.Ordinal);
        Assert.Contains("running scripts is disabled", result.Steps[0].Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_PowerShell_failure_is_an_ordinary_exit_code_failure()
    {
        _root.WriteScript(ProfileId.Personal, "report.ps1", "exit 3\r\n");
        _launchers.Processes.Behavior = _ => new FakeProcess(exitCode: 3, standardError: "something else went wrong");

        var result = await RunAsync(_registry, """{ "interpreter": "powershell", "script": "report.ps1" }""");

        Assert.Equal("exit-code", result.Steps[0].ErrorCode);
    }

    [Fact]
    public async Task Cancellation_kills_the_script_process_tree()
    {
        _root.WriteScript(ProfileId.Personal, "slow.cmd", "@ping -n 30 127.0.0.1\r\n");
        var lingering = new FakeProcess();
        _launchers.Processes.Behavior = _ => lingering;
        using var history = SqliteHistoryStore.Open(_root.Paths.DatabasePath);
        using var engine = new WorkflowEngine(_registry, history);
        var workflow = TestSupport.LoadValid(_registry, _personal, Workflow("""{ "interpreter": "cmd", "script": "slow.cmd" }"""));

        var started = engine.Start(workflow, _personal);
        Assert.True(await TestSupport.WaitUntilAsync(() => _launchers.Processes.Requests.Count == 1, Timeout));
        engine.Cancel(started.Execution.Id);
        using var cts = new CancellationTokenSource(Timeout);
        var result = await engine.WaitForCompletionAsync(started.Execution.Id, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.True(lingering.WasKilled);
    }

    [Fact]
    public void Profile_allowlists_can_exclude_scripts()
    {
        var noScripts = _root.Profile(ProfileId.Personal, UrlOpenAction.ActionId, FolderOpenAction.ActionId, ProcessLaunchAction.ActionId);

        var errors = TestSupport.LoadInvalid(_registry, noScripts, Workflow("""{ "interpreter": "cmd", "script": "hello.cmd" }"""));

        Assert.Contains(errors, e => e.Message.Contains("not allowed in the Personal profile", StringComparison.Ordinal));
    }

    public void Dispose() => _root.Dispose();
}
