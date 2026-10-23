using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Tests.Paths;

public sealed class AppPathsTests
{
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";

    private static Func<string, string?> Env(string? dataDir) =>
        name => name == AppPaths.DataDirVariable ? dataDir : null;

    [Fact]
    public void Uses_OGOC_DATA_DIR_when_set()
    {
        var paths = AppPaths.Resolve(Env(@"D:\scratch\ogoc-data"), LocalAppData);

        Assert.Equal(@"D:\scratch\ogoc-data", paths.DataRoot);
        Assert.True(paths.IsOverridden);
    }

    [Fact]
    public void Trims_whitespace_and_trailing_separator_from_override()
    {
        var paths = AppPaths.Resolve(Env(@"  D:\scratch\ogoc-data\  "), LocalAppData);

        Assert.Equal(@"D:\scratch\ogoc-data", paths.DataRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_LocalAppData_when_override_is_unset_or_blank(string? dataDir)
    {
        var paths = AppPaths.Resolve(Env(dataDir), LocalAppData);

        Assert.Equal(Path.Combine(LocalAppData, AppPaths.DefaultFolderName), paths.DataRoot);
        Assert.EndsWith(@"\OG_opossum_command", paths.DataRoot, StringComparison.Ordinal);
        Assert.False(paths.IsOverridden);
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(".")]
    [InlineData(@"\rooted-but-no-drive")]
    public void Rejects_relative_override_instead_of_using_current_directory(string dataDir)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppPaths.Resolve(Env(dataDir), LocalAppData));

        Assert.Contains(AppPaths.DataDirVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(dataDir, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_neither_override_nor_LocalAppData_is_available()
    {
        Assert.Throws<InvalidOperationException>(() => AppPaths.Resolve(Env(null), ""));
    }

    [Fact]
    public void ForRoot_rejects_relative_paths()
    {
        Assert.Throws<InvalidOperationException>(() => AppPaths.ForRoot(@"relative\root"));
    }

    [Fact]
    public void Every_child_path_lives_under_the_data_root()
    {
        var paths = AppPaths.Resolve(Env(@"D:\scratch\ogoc-data"), LocalAppData);

        var children = new[]
        {
            paths.DatabasePath,
            paths.ProfilesDirectory,
            paths.LogsDirectory,
            paths.GetProfileDirectory(ProfileId.Work),
            paths.GetProfileDirectory(ProfileId.Personal),
            paths.GetProfileWorkflowsDirectory(ProfileId.Work),
            paths.GetProfileWorkflowsDirectory(ProfileId.Personal),
        };
        Assert.All(children, child => Assert.StartsWith(paths.DataRoot + Path.DirectorySeparatorChar, child, StringComparison.Ordinal));
        Assert.Equal(children.Length, children.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Path.Combine(paths.DataRoot, "data.db"), paths.DatabasePath);
    }

    [Fact]
    public void Profile_workflow_folders_follow_the_spec_layout()
    {
        var paths = AppPaths.ForRoot(@"D:\scratch\ogoc-data");

        Assert.Equal(@"D:\scratch\ogoc-data\profiles\Work\workflows", paths.GetProfileWorkflowsDirectory(ProfileId.Work));
        Assert.Equal(@"D:\scratch\ogoc-data\profiles\Personal\workflows", paths.GetProfileWorkflowsDirectory(ProfileId.Personal));
    }

    [Fact]
    public void FromEnvironment_reads_the_process_environment()
    {
        // The build script and the test runner set OGOC_DATA_DIR to a scratch folder (rule 6).
        // When it is not set (a bare IDE run) the fallback must still be the per-user folder.
        var expected = Environment.GetEnvironmentVariable(AppPaths.DataDirVariable)?.Trim();
        var paths = AppPaths.FromEnvironment();

        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), paths.DataRoot);
            Assert.True(paths.IsOverridden);
        }
        else
        {
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.DefaultFolderName),
                paths.DataRoot);
            Assert.False(paths.IsOverridden);
        }
    }
}
