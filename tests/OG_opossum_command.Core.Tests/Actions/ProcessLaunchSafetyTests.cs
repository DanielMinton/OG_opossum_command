using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Actions;

/// <summary>
/// <c>windows.process.launch</c> may not start shells or script hosts (SEC-3): that would bypass every
/// <c>windows.script.run</c> rule. A NUL in a path is a readable load error, never an exception (SEC-6). Fakes only.
/// </summary>
public sealed class ProcessLaunchSafetyTests : IDisposable
{
    private readonly TempDataRoot _root = new();
    private readonly RecordingLaunchers _launchers = new();
    private readonly ActionRegistry _registry;
    private readonly Profile _work;

    public ProcessLaunchSafetyTests()
    {
        _registry = ActionRegistry.CreateDefault(_launchers.Set);
        _work = _root.Profile(ProfileId.Work, _registry);
    }

    private static string Workflow(string inputs) => $$"""
        {
          "schemaVersion": 1, "id": "wf", "name": "WF", "profileId": "work", "trigger": { "type": "manual" },
          "steps": [ { "id": "go", "action": "windows.process.launch", "inputs": {{inputs}} } ]
        }
        """;

    [Theory]
    [InlineData(@"%SystemRoot%\\System32\\cmd.exe")]
    [InlineData(@"%SystemRoot%\\System32\\CMD.EXE")]
    [InlineData(@"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    [InlineData(@"C:\\Program Files\\PowerShell\\7\\pwsh.exe")]
    [InlineData(@"%SystemRoot%\\System32\\wscript.exe")]
    [InlineData(@"%SystemRoot%\\System32\\cscript.exe")]
    [InlineData(@"%SystemRoot%\\System32\\mshta.exe")]
    [InlineData(@"%SystemRoot%\\System32\\rundll32.exe")]
    [InlineData(@"%SystemRoot%\\System32\\wsl.exe")]
    public void Shells_and_script_hosts_are_refused_at_load_with_a_pointer_to_script_run(string path)
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Workflow($$"""{ "path": "{{path}}", "arguments": ["-Command", "Get-Date"] }"""));

        Assert.Contains(errors, e => e.Message.Contains(ScriptRunAction.ActionId, StringComparison.Ordinal));
    }

    [Fact]
    public void An_ordinary_program_still_loads()
    {
        var workflow = TestSupport.LoadValid(_registry, _work, Workflow("""{ "path": "%SystemRoot%\\System32\\notepad.exe" }"""));

        Assert.IsType<ProcessLaunchInputs>(workflow.Steps[0].Inputs);
    }

    [Fact]
    public async Task A_shell_is_refused_again_at_run_time_before_any_process_starts()
    {
        var action = new ProcessLaunchAction(_launchers.Processes);
        var context = new ActionContext(Guid.NewGuid(), "wf", "go", _work);

        var result = await action.ExecuteAsync(new ProcessLaunchInputs(TestSupport.CmdPath, ["/c", "echo"], null, WaitForExit: true), context, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ProcessLaunchAction.InterpreterRefusedCode, result.ErrorCode);
        Assert.Empty(_launchers.Processes.Requests);
    }

    [Theory]
    [InlineData("""{ "path": "C:\\a\u0000b.exe" }""", "path")]
    [InlineData("""{ "path": "%SystemRoot%\\System32\\notepad.exe", "workingDirectory": "C:\\a\u0000b" }""", "workingDirectory")]
    public void A_NUL_character_in_a_path_is_a_readable_load_error(string inputs, string property)
    {
        var errors = TestSupport.LoadInvalid(_registry, _work, Workflow(inputs));

        Assert.Contains(errors, e => e.Message.Contains("NUL", StringComparison.Ordinal) && e.Message.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void A_NUL_character_in_a_folder_path_is_a_readable_load_error_not_a_parent_folder()
    {
        // Without the check, %VAR% expansion would cut "C:\Users\0evil" to "C:\Users" and open that instead.
        var json = """
            {
              "schemaVersion": 1, "id": "wf", "name": "WF", "profileId": "work", "trigger": { "type": "manual" },
              "steps": [ { "id": "go", "action": "windows.folder.open", "inputs": { "path": "C:\\Users\u0000evil" } } ]
            }
            """;

        var errors = TestSupport.LoadInvalid(_registry, _work, json);

        Assert.Contains(errors, e => e.Message.Contains("NUL", StringComparison.Ordinal));
    }

    public void Dispose() => _root.Dispose();
}
