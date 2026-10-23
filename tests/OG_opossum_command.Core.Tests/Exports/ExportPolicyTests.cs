using OG_opossum_command.Core.Exports;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Tests.Exports;

/// <summary>The <c>exports</c> object of profile.json (ADR-050): defaults, strict parsing, fail closed, and the cross-OneDrive guard in both directions.</summary>
public sealed class ExportPolicyTests : IDisposable
{
    private readonly ExportsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Default_exports_folder_is_derived_from_OneDriveCommercial_without_touching_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "ogoc-never-created-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Resolve(name => name == AppPaths.OneDriveCommercialVariable ? root : null, localAppDataPath: @"C:\LocalAppData");

        Assert.Equal(Path.Combine(root, AppPaths.DefaultFolderName), paths.GetDefaultExportsFolder(ProfileId.Work));
        Assert.Equal(Path.Combine(root, AppPaths.DefaultFolderName, AppPaths.CalendarFileName), paths.GetDefaultCalendarFile(ProfileId.Work));
        Assert.Null(paths.GetDefaultExportsFolder(ProfileId.Personal));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void No_OneDrive_means_no_default_folder()
    {
        Assert.Null(AppPaths.ForRoot(_fx.Root.Root).GetDefaultExportsFolder(ProfileId.Work));
    }

    [Fact]
    public void Without_profile_json_Work_is_on_in_its_OneDrive_folder_and_Personal_is_off()
    {
        var catalog = _fx.LoadCatalog();

        var work = catalog.Get(ProfileId.Work).Exports;
        Assert.Equal(_fx.WorkFolder, work.Folder);
        Assert.True(work.Status && work.Alerts && work.Carryovers);
        Assert.Null(work.DisabledReason);

        var personal = catalog.Get(ProfileId.Personal).Exports;
        Assert.Null(personal.Folder);
        Assert.False(personal.Status || personal.Alerts || personal.Carryovers);
        Assert.Contains("Personal", personal.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_exports_object_without_a_folder_stays_off()
    {
        _fx.WritePolicy(ProfileId.Personal, """{ "exports": { "status": true, "alerts": true } }""");

        var catalog = _fx.LoadCatalog();

        Assert.Empty(catalog.Errors);
        var personal = catalog.Get(ProfileId.Personal).Exports;
        Assert.Null(personal.Folder);
        Assert.False(personal.Status);
        Assert.Contains("exports.folder", personal.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Personal_can_be_enabled_inside_its_own_OneDrive_and_unset_switches_stay_off()
    {
        _fx.WritePolicy(ProfileId.Personal, $$"""{ "exports": { "folder": {{System.Text.Json.JsonSerializer.Serialize(_fx.PersonalFolder)}}, "status": true } }""");

        var catalog = _fx.LoadCatalog();

        Assert.Empty(catalog.Errors);
        var personal = catalog.Get(ProfileId.Personal).Exports;
        Assert.Equal(_fx.PersonalFolder, personal.Folder);
        Assert.True(personal.Status);
        Assert.False(personal.Alerts);
        Assert.False(personal.Carryovers);
    }

    [Fact]
    public void Work_switches_can_be_turned_off_individually()
    {
        _fx.WritePolicy(ProfileId.Work, """{ "exports": { "alerts": false } }""");

        var work = _fx.LoadCatalog().Get(ProfileId.Work).Exports;

        Assert.True(work.Status);
        Assert.False(work.Alerts);
        Assert.True(work.Carryovers);
        Assert.Equal(_fx.WorkFolder, work.Folder);
    }

    [Fact]
    public void Personal_folder_inside_the_work_OneDrive_is_a_policy_error_and_fails_closed()
    {
        _fx.WritePolicy(ProfileId.Personal, $$"""{ "exports": { "folder": {{System.Text.Json.JsonSerializer.Serialize(_fx.WorkFolder)}}, "status": true } }""");

        var catalog = _fx.LoadCatalog();

        Assert.Contains(catalog.Errors, e => e.Message.Contains("work OneDrive", StringComparison.Ordinal));
        var personal = catalog.Get(ProfileId.Personal);
        Assert.Null(personal.Exports.Folder);
        Assert.False(personal.Exports.Status);
        Assert.Contains("fail closed", personal.Exports.DisabledReason, StringComparison.Ordinal);
        Assert.DoesNotContain(personal.AllowedActionIds, ProfileCatalog.IsM365Action);
    }

    [Fact]
    public void Work_folder_inside_the_personal_OneDrive_is_a_policy_error_and_fails_closed()
    {
        _fx.WritePolicy(ProfileId.Work, $$"""{ "exports": { "folder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_fx.PersonalOneDrive, "Stuff"))}} } }""");

        var catalog = _fx.LoadCatalog();

        Assert.Contains(catalog.Errors, e => e.Message.Contains("personal OneDrive", StringComparison.Ordinal));
        Assert.False(catalog.Get(ProfileId.Work).Exports.Status);
        Assert.Null(catalog.Get(ProfileId.Work).Exports.Folder);
    }

    [Theory]
    [InlineData("""{ "exports": { "status": true, "status": false } }""")]
    [InlineData("""{ "exports": { "statuss": true } }""")]
    [InlineData("""{ "exports": { "status": "yes" } }""")]
    [InlineData("""{ "exports": { "folder": "relative\\path" } }""")]
    [InlineData("""{ "exports": { "folder": "\\\\server\\share\\ogoc" } }""")]
    [InlineData("""{ "exports": { "folder": "//server/share/ogoc" } }""")]
    [InlineData("""{ "exports": { "folder": "\\\\?\\C:\\ogoc" } }""")]
    [InlineData("""{ "exports": { "folder": "C:\\ogoc\u0000x" } }""")]
    [InlineData("""{ "exports": [] }""")]
    [InlineData("""{ "exports": { } """)]
    public void A_broken_exports_object_disables_exports_for_that_profile(string json)
    {
        _fx.WritePolicy(ProfileId.Work, json);

        var catalog = _fx.LoadCatalog();

        Assert.NotEmpty(catalog.Errors);
        var work = catalog.Get(ProfileId.Work).Exports;
        Assert.Null(work.Folder);
        Assert.False(work.Status || work.Alerts || work.Carryovers);
        Assert.Contains("fail closed", work.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_profile_json_elsewhere_also_disables_exports()
    {
        _fx.WritePolicy(ProfileId.Work, """{ "allowedActions": ["no.such.action"] }""");

        var work = _fx.LoadCatalog().Get(ProfileId.Work);

        Assert.False(work.Exports.Status);
        Assert.Equal(ExportFolderState.Disabled, ExportFolder.Resolve(work, _fx.Paths, createIfParentExists: true).State);
    }

    [Fact]
    public void Personal_sharing_the_Work_folder_is_switched_off()
    {
        var shared = Path.Combine(_fx.Root.Root, "shared-exports");
        var folderJson = System.Text.Json.JsonSerializer.Serialize(shared);
        _fx.WritePolicy(ProfileId.Work, $$"""{ "exports": { "folder": {{folderJson}} } }""");
        _fx.WritePolicy(ProfileId.Personal, $$"""{ "exports": { "folder": {{folderJson}}, "status": true } }""");

        var catalog = _fx.LoadCatalog();

        Assert.Equal(shared, catalog.Get(ProfileId.Work).Exports.Folder);
        Assert.Null(catalog.Get(ProfileId.Personal).Exports.Folder);
        Assert.Contains("same folder", catalog.Get(ProfileId.Personal).Exports.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_normalisation_serves_the_catalog_and_the_use_time_guard()
    {
        Assert.True(OneDriveBoundary.TryNormalise(_fx.WorkFolder.ToUpperInvariant() + "\\", out var full, out var invalid));
        Assert.Null(invalid);
        Assert.True(OneDriveBoundary.IsSamePath(full, _fx.WorkFolder));

        // An unset %VAR% is "not available right now", not an error in the file.
        Assert.False(OneDriveBoundary.TryNormalise("%OGOC_TEST_SURELY_UNSET%\\ogoc", out _, out invalid));
        Assert.Null(invalid);

        Assert.False(OneDriveBoundary.TryNormalise("C:\\ogoc\0x", out _, out invalid));
        Assert.Contains("NUL", invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_time_guard_refuses_cross_OneDrive_folders_in_both_directions()
    {
        var personalIntoWork = _fx.Profile(ProfileId.Personal, new ExportSettings(_fx.WorkFolder, true, true, true));
        var workIntoPersonal = _fx.Profile(ProfileId.Work, new ExportSettings(_fx.PersonalFolder, true, true, true));

        Assert.Equal(ExportFolderState.Refused, ExportFolder.Resolve(personalIntoWork, _fx.Paths, createIfParentExists: true).State);
        Assert.Equal(ExportFolderState.Refused, ExportFolder.Resolve(workIntoPersonal, _fx.Paths, createIfParentExists: true).State);
        Assert.False(Directory.Exists(_fx.WorkFolder));
        Assert.False(Directory.Exists(_fx.PersonalFolder));
    }

    [Fact]
    public void Resolve_creates_only_the_app_folder_inside_an_existing_OneDrive_root()
    {
        var result = ExportFolder.Resolve(_fx.WorkProfile(), _fx.Paths, createIfParentExists: true);

        Assert.Equal(ExportFolderState.Ready, result.State);
        Assert.True(Directory.Exists(_fx.WorkFolder));
    }

    [Fact]
    public void Resolve_never_creates_a_missing_OneDrive_root()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);

        var result = ExportFolder.Resolve(fx.WorkProfile(), fx.Paths, createIfParentExists: true);

        Assert.Equal(ExportFolderState.FolderMissing, result.State);
        Assert.False(Directory.Exists(fx.WorkOneDrive));
    }

    [Fact]
    public void Resolve_never_creates_the_OneDrive_root_even_when_it_is_the_configured_folder()
    {
        using var fx = new ExportsFixture(createWorkOneDrive: false);

        var result = ExportFolder.Resolve(fx.WorkProfile(fx.WorkOneDrive), fx.Paths, createIfParentExists: true);

        Assert.Equal(ExportFolderState.FolderMissing, result.State);
        Assert.False(Directory.Exists(fx.WorkOneDrive));
    }
}
