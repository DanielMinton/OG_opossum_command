using System.Globalization;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.M365;

/// <summary>Inputs of <see cref="CalendarReadFileAction"/>.</summary>
/// <param name="MaxAge">How old the file's <c>generatedAt</c> may be before the result counts as stale.</param>
public sealed record CalendarReadFileInputs(TimeSpan MaxAge);

/// <summary>
/// <c>m365.calendar.readFile</c>: read the profile's calendar JSON file, written to OneDrive by the user's own
/// Power Automate flow (ADR-011, ADR-039). No sign-in, no Graph, no network: the app reads one local file. The path
/// comes from the profile, never from the workflow, so a file cannot point a profile at the other context's data.
/// A missing or mid-sync file completes the step with "calendar unavailable"; only a cross-profile path or a
/// malformed file fails it.
/// </summary>
public sealed class CalendarReadFileAction : WorkflowAction<CalendarReadFileInputs>
{
    public const string ActionId = "m365.calendar.readFile";

    /// <summary>Upper bound on <c>maxAgeMinutes</c> (one day).</summary>
    public const int MaxAgeMinutesLimit = 1440;

    private readonly CalendarService _calendar;

    public CalendarReadFileAction(CalendarService calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        _calendar = calendar;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Read calendar file";

    public override IdempotencyClass Idempotency => IdempotencyClass.Idempotent;

    public override TimeSpan DefaultTimeout => TimeSpan.FromSeconds(20);

    public override CalendarReadFileInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties("maxAgeMinutes");

        var minutes = inputs.OptionalInt("maxAgeMinutes", 1, MaxAgeMinutesLimit);
        if (inputs.Has("maxAgeMinutes") && minutes is null)
        {
            return null;
        }

        return new CalendarReadFileInputs(minutes is null ? CalendarService.DefaultMaxAge : TimeSpan.FromMinutes(minutes.Value));
    }

    public override async Task<ActionResult> ExecuteAsync(CalendarReadFileInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);

        var result = await _calendar.ReadAsync(context.Profile, inputs.MaxAge, cancellationToken).ConfigureAwait(false);
        var output = string.Create(
            CultureInfo.InvariantCulture,
            $"status={result.Status}; events={result.Snapshot?.Events.Count ?? 0}; generatedAt={result.Snapshot?.GeneratedAt.ToString("u", CultureInfo.InvariantCulture) ?? "-"}; attempts={result.Attempts}");

        return result.Status switch
        {
            CalendarStatus.Refused => ActionResult.Failure("calendar-refused", result.Message, output),
            CalendarStatus.Invalid => ActionResult.Failure("calendar-invalid", result.Message, output),
            _ => ActionResult.Success(result.Message, output),
        };
    }
}
