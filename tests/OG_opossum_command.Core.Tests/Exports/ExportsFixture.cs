using OG_opossum_command.Core.Actions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.History;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Exports;

/// <summary>
/// A temp data root with two pretend OneDrive roots ("OneDrive - Work", "OneDrive - Personal") under %TEMP%, handed to
/// <see cref="AppPaths.Resolve"/>, a fixed UTC-7 local zone, and a manual clock. The real OneDrive is never consulted.
/// </summary>
internal sealed class ExportsFixture : IDisposable
{
    /// <summary>Arizona: UTC-7 all year (no DST), and a zone ID the workflow loader accepts for schedules.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("US Mountain Standard Time");

    /// <summary>2026-10-23 12:00 local (19:00 UTC): far from midnight so "today" cannot flip.</summary>
    public static readonly DateTimeOffset Noon = new(2026, 10, 23, 19, 0, 0, TimeSpan.Zero);

    public ExportsFixture(bool createWorkOneDrive = true, bool createPersonalOneDrive = true)
    {
        WorkOneDrive = Path.Combine(Root.Root, "OneDrive - Work");
        PersonalOneDrive = Path.Combine(Root.Root, "OneDrive - Personal");
        if (createWorkOneDrive)
        {
            Directory.CreateDirectory(WorkOneDrive);
        }

        if (createPersonalOneDrive)
        {
            Directory.CreateDirectory(PersonalOneDrive);
        }

        var data = Path.Combine(Root.Root, "data");
        Paths = AppPaths.Resolve(
            name => name switch
            {
                AppPaths.DataDirVariable => data,
                AppPaths.OneDriveCommercialVariable => WorkOneDrive,
                AppPaths.OneDriveConsumerVariable => PersonalOneDrive,
                _ => null,
            },
            localAppDataPath: string.Empty);
        Directory.CreateDirectory(Paths.DataRoot);
        Clock = new ManualTimeProvider(Noon) { Zone = Zone };
    }

    public TempDataRoot Root { get; } = new();

    public string WorkOneDrive { get; }

    public string PersonalOneDrive { get; }

    public AppPaths Paths { get; }

    public ManualTimeProvider Clock { get; }

    /// <summary>The Work exports folder: <c>&lt;Work OneDrive&gt;\OG_opossum_command</c>.</summary>
    public string WorkFolder => Path.Combine(WorkOneDrive, AppPaths.DefaultFolderName);

    public string PersonalFolder => Path.Combine(PersonalOneDrive, AppPaths.DefaultFolderName);

    /// <summary>Writes a profile.json for <paramref name="profile"/>.</summary>
    public void WritePolicy(ProfileId profile, string json)
    {
        var folder = Paths.GetProfileDirectory(profile);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ProfileCatalog.PolicyFileName), json);
    }

    public ProfileCatalog LoadCatalog() => ProfileCatalog.Load(Paths, Registry);

    public ActionRegistry Registry { get; } = ActionRegistry.CreateDefault(new RecordingLaunchers().Set);

    /// <summary>A profile with explicit export settings (bypasses the catalog, to test the use-time guard).</summary>
    public Profile Profile(ProfileId id, ExportSettings exports) =>
        new(id, Paths.GetProfileWorkflowsDirectory(id), Paths.GetProfileScriptsDirectory(id), Registry.Ids, null, exports);

    public Profile WorkProfile(string? folder = null) => Profile(ProfileId.Work, new ExportSettings(folder ?? WorkFolder, true, true, true));

    public SqliteHistoryStore OpenHistory() => SqliteHistoryStore.Open(Paths.DatabasePath);

    public static ExecutionSnapshot Run(
        ExecutionStatus status,
        DateTimeOffset at,
        ProfileId profile = ProfileId.Work,
        string workflowId = "wf",
        string workflowName = "Workflow",
        string? error = null,
        string? stepMessage = null,
        Guid? id = null,
        StepStatus? stepStatus = null)
    {
        var finished = status is ExecutionStatus.Queued or ExecutionStatus.Running or ExecutionStatus.Cancelling ? (DateTimeOffset?)null : at;
        var stepState = stepStatus ?? status switch
        {
            ExecutionStatus.Failed => StepStatus.Failed,
            ExecutionStatus.Completed => StepStatus.Completed,
            ExecutionStatus.Running => StepStatus.Running,
            ExecutionStatus.NeedsAttention => StepStatus.Interrupted,
            _ => StepStatus.Pending,
        };
        var steps = new List<StepSnapshot>
        {
            new("first", "First", "windows.url.open", 0, stepState, at.AddSeconds(-5), stepState is StepStatus.Running ? null : at, stepState == StepStatus.Failed ? "exit" : null, stepMessage, "OUTPUT-SHOULD-NEVER-LEAK"),
        };
        return new ExecutionSnapshot(id ?? Guid.NewGuid(), profile, workflowId, workflowName, status, at.AddSeconds(-10), at.AddSeconds(-5), finished, error, steps);
    }

    /// <summary>Every file below <paramref name="folder"/> (recursively), as relative paths.</summary>
    public static IReadOnlyList<string> FilesUnder(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal).ToList()
            : [];

    public void Dispose() => Root.Dispose();
}
