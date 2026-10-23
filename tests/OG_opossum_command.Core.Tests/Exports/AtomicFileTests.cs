using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Tests.Support;

namespace OG_opossum_command.Core.Tests.Exports;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDataRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string Folder
    {
        get
        {
            var folder = Path.Combine(_root.Root, "exports");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static string TempName(string target) => $".{target}.{Guid.NewGuid():N}{AtomicFile.TempSuffix}";

    [Fact]
    public async Task A_briefly_locked_target_is_replaced_after_a_retry()
    {
        var path = Path.Combine(Folder, "status.json");
        File.WriteAllText(path, "old");

        // OneDrive or antivirus holds the previous file for a moment.
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await holder.DisposeAsync();
        });

        AtomicFile.WriteAllText(path, "new");
        await release;

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(["status.json"], Directory.GetFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Without_overwrite_an_existing_target_fails_at_once()
    {
        var path = Path.Combine(Folder, "alert.json");
        File.WriteAllText(path, "first");

        Assert.Throws<IOException>(() => AtomicFile.WriteAllText(path, "second", overwrite: false));
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Orphaned_temporary_files_older_than_10_minutes_are_deleted_and_nothing_else()
    {
        var now = DateTimeOffset.UtcNow;
        var folder = Folder;
        var stale = Path.Combine(folder, TempName("status.json"));
        var fresh = Path.Combine(folder, TempName("status.json"));
        var foreign = new[] { "notes.ogoc-tmp", ".status.json.NOTHEX.ogoc-tmp", "status.json", $"{TempName("x")}.bak" };
        var nested = Path.Combine(folder, "outbox");
        Directory.CreateDirectory(nested);
        var nestedStale = Path.Combine(nested, TempName("a.json"));
        foreach (var file in foreign.Select(n => Path.Combine(folder, n)).Append(stale).Append(fresh).Append(nestedStale))
        {
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddHours(-1));
        }

        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime.AddMinutes(-2));

        Assert.Equal(1, AtomicFile.DeleteOrphans(folder, now));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(nestedStale));
        Assert.All(foreign, n => Assert.True(File.Exists(Path.Combine(folder, n)), n));
    }

    [Fact]
    public void A_missing_folder_sweeps_nothing() =>
        Assert.Equal(0, AtomicFile.DeleteOrphans(Path.Combine(_root.Root, "missing"), DateTimeOffset.UtcNow));
}
