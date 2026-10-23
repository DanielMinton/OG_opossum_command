using System.Globalization;
using OG_opossum_command.Core.Paths;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Core.Exports;

/// <summary>How a carryovers read ended.</summary>
public enum CarryoverStatus
{
    /// <summary>Items for today (possibly zero).</summary>
    Loaded,

    /// <summary>No <c>tomorrow.json</c> (the flow has not written one yet). Shown as "none".</summary>
    None,

    /// <summary>A valid file, but for another day; nothing is shown.</summary>
    NotToday,

    /// <summary>The file is half-written, an online-only placeholder, locked, or the folder is missing. Nothing is shown.</summary>
    Unavailable,

    /// <summary>The file is not in the expected shape (or too big, or contains NUL). Nothing is shown; the message says why.</summary>
    Invalid,

    /// <summary>Carryovers are off for the profile.</summary>
    Disabled,

    /// <summary>The folder is inside the other context's OneDrive or is a network path. Never read.</summary>
    Refused,
}

/// <summary>What one read produced; item content stays in memory and is never written to history.</summary>
/// <param name="Items">Today's items; empty unless <see cref="Status"/> is <see cref="CarryoverStatus.Loaded"/>.</param>
public sealed record CarryoverReadResult(ProfileId Profile, CarryoverStatus Status, string Message, IReadOnlyList<CarryoverItem> Items, DateTimeOffset? GeneratedAt, DateTimeOffset ReadAt);

/// <summary>
/// Reads <c>tomorrow.json</c> (ADR-051) from a profile's exports folder for the Today panel's "Carryovers from
/// yesterday" section. Tolerant: a missing file is "none", a bad file a readable message; nothing here throws for file
/// problems. The file is only read (never created or written), an online-only placeholder is never opened, files over
/// <see cref="CarryoverFileParser.MaxFileBytes"/> are not read, and items are shown only when <c>forDate</c> is the
/// local date today. Nothing in the file is ever opened or followed.
/// </summary>
public sealed class CarryoverService
{
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly Dictionary<ProfileId, CarryoverReadResult> _latest = [];
    private readonly Lock _gate = new();

    public CarryoverService(IAppPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after every read (on the reading thread). Never throws into the caller.</summary>
    public event EventHandler<CarryoverReadResult>? Changed;

    /// <summary>The most recent result for a profile, or <c>null</c> before the first read.</summary>
    public CarryoverReadResult? GetLatest(ProfileId profile)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(profile, out var result) ? result : null;
        }
    }

    /// <summary>Reads the profile's <c>tomorrow.json</c> now. Synchronous and small (at most 64 KB); call it off the UI thread if you like.</summary>
    public CarryoverReadResult Read(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var now = _time.GetUtcNow();
        var result = ReadCore(profile, now);
        lock (_gate)
        {
            _latest[profile.Id] = result;
        }

        var handlers = Changed;
        if (handlers is not null)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<CarryoverReadResult>>())
            {
                try
                {
                    handler(this, result);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A view failing is not a read failure.
                }
            }
        }

        return result;
    }

    /// <summary><see cref="Read"/> on the thread pool, for UI callers.</summary>
    public Task<CarryoverReadResult> ReadAsync(Profile profile, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(profile), cancellationToken);

    private CarryoverReadResult ReadCore(Profile profile, DateTimeOffset now)
    {
        CarryoverReadResult Result(CarryoverStatus status, string message, IReadOnlyList<CarryoverItem>? items = null, DateTimeOffset? generatedAt = null) =>
            new(profile.Id, status, message, items ?? [], generatedAt, now);

        if (!profile.Exports.Carryovers)
        {
            return Result(CarryoverStatus.Disabled, profile.Exports.DisabledReason ?? $"Carryovers are switched off for the {profile.DisplayName} profile.");
        }

        var folder = ExportFolder.Resolve(profile, _paths, createIfParentExists: false);
        switch (folder.State)
        {
            case ExportFolderState.Disabled:
                return Result(CarryoverStatus.Disabled, folder.Message);
            case ExportFolderState.Refused:
                return Result(CarryoverStatus.Refused, folder.Message);
            case ExportFolderState.FolderMissing:
                return Result(CarryoverStatus.Unavailable, folder.Message);
        }

        var path = Path.Combine(folder.FullPath!, AppPaths.CarryoversFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Result(CarryoverStatus.None, "No carryovers: the flow has not written tomorrow.json yet.");
            }

            // OneDrive files are reparse points themselves (cloud files), so only real links are refused.
            if (OneDriveBoundary.IsLink(info))
            {
                return Result(CarryoverStatus.Refused, "tomorrow.json is a symbolic link or junction; the app does not follow links.");
            }

            if (OneDriveBoundary.IsOnlineOnly(info.Attributes))
            {
                return Result(CarryoverStatus.Unavailable, $"tomorrow.json is an online-only OneDrive placeholder and the app never downloads files. In OneDrive, choose 'Always keep on this device' for the {AppPaths.DefaultFolderName} folder.");
            }

            if (info.Length > CarryoverFileParser.MaxFileBytes)
            {
                return Result(CarryoverStatus.Invalid, $"tomorrow.json is larger than {CarryoverFileParser.MaxFileBytes / 1024} KB and was not read. See docs/POWER_AUTOMATE_FLOW.md for the exact format.");
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Result(CarryoverStatus.Unavailable, "tomorrow.json is empty (OneDrive may be mid-sync).");
            }

            var parsed = CarryoverFileParser.Parse(text, AppPaths.CarryoversFileName);
            if (parsed.IsMalformedJson)
            {
                return Result(CarryoverStatus.Unavailable, "tomorrow.json is not complete JSON yet (OneDrive may be mid-sync); try Refresh in a moment.");
            }

            if (!parsed.IsValid)
            {
                var shown = string.Join("; ", parsed.Errors.Take(3).Select(e => $"{e.JsonPath}: {e.Message}"));
                return Result(CarryoverStatus.Invalid, ExportText.SingleLine($"tomorrow.json is not in the expected shape: {shown} See docs/POWER_AUTOMATE_FLOW.md for the exact format."));
            }

            var snapshot = parsed.Snapshot!;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).DateTime);
            if (snapshot.ForDate != today)
            {
                return Result(CarryoverStatus.NotToday, $"No carryovers for today (the file is for {snapshot.ForDate.ToString("ddd d MMM", CultureInfo.CurrentCulture)}).", generatedAt: snapshot.GeneratedAt);
            }

            var count = snapshot.Items.Count;
            return Result(
                CarryoverStatus.Loaded,
                count == 0 ? "Nothing carried over from yesterday." : string.Create(CultureInfo.CurrentCulture, $"{count} item(s) carried over from yesterday."),
                snapshot.Items,
                snapshot.GeneratedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Result(CarryoverStatus.Unavailable, $"tomorrow.json could not be read ({ExportText.Scrub(ex.Message, paths: _paths)}); OneDrive may be mid-sync.");
        }
    }
}
