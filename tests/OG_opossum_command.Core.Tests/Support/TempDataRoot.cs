using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Tests.Support;

/// <summary>A throwaway data root under %TEMP% (rule 5c), deleted on dispose. Never the real data folder.</summary>
internal sealed class TempDataRoot : IDisposable
{
    public TempDataRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "OG_opossum_command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = AppPaths.ForRoot(Root);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    /// <summary>The shipped examples, copied next to the test assembly by the csproj (never read from the current directory).</summary>
    public static string ExamplesRoot => Path.Combine(AppContext.BaseDirectory, "examples", "workflows");

    /// <summary>A folder that exists but holds no examples, for tests that must not seed anything.</summary>
    public string EmptyExamplesRoot
    {
        get
        {
            var folder = Path.Combine(Root, "no-examples");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>A profile rooted here that may run every registered action.</summary>
    public Profile Profile(ProfileId id, ActionRegistry registry) =>
        new(id, Paths.GetProfileWorkflowsDirectory(id), registry.Ids);

    /// <summary>A profile rooted here with an explicit allowlist.</summary>
    public Profile Profile(ProfileId id, params string[] allowedActions) =>
        new(id, Paths.GetProfileWorkflowsDirectory(id), allowedActions);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; the OS cleans %TEMP% eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>Helpers shared by the tests.</summary>
internal static class TestSupport
{
    public static readonly string CmdPath = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");

    /// <summary>Parses workflow JSON that the test expects to be valid; fails the test with the errors otherwise.</summary>
    public static WorkflowDefinition LoadValid(ActionRegistry registry, Profile profile, string json)
    {
        var result = new WorkflowLoader(registry).LoadJson(json, profile, "<test>");
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.Workflow!;
    }

    /// <summary>Parses workflow JSON that the test expects to be invalid and returns the readable errors.</summary>
    public static IReadOnlyList<WorkflowValidationError> LoadInvalid(ActionRegistry registry, Profile profile, string json)
    {
        var result = new WorkflowLoader(registry).LoadJson(json, profile, "<test>");
        Assert.False(result.IsValid, "Expected validation errors, but the workflow loaded.");
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    /// <summary>Process IDs of every running <c>PING.EXE</c>.</summary>
    public static HashSet<int> PingPids()
    {
        var pids = new HashSet<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("PING"))
        {
            using (process)
            {
                pids.Add(process.Id);
            }
        }

        return pids;
    }

    /// <summary>Waits for a ping process that was not in <paramref name="before"/> and returns its ID; fails the test otherwise.</summary>
    public static async Task<int> WaitForNewPingAsync(HashSet<int> before, TimeSpan timeout)
    {
        int? found = null;
        var appeared = await WaitUntilAsync(
            () =>
            {
                found = PingPids().FirstOrDefault(pid => !before.Contains(pid), 0);
                return found != 0;
            },
            timeout);
        Assert.True(appeared, "ping never started");
        return found!.Value;
    }

    /// <summary>True once the ping process <paramref name="pid"/> is gone.</summary>
    public static Task<bool> WaitForPingGoneAsync(int pid, TimeSpan timeout) => WaitUntilAsync(() => !PingPids().Contains(pid), timeout);

    /// <summary>Polls until <paramref name="condition"/> holds or <paramref name="timeout"/> elapses.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
