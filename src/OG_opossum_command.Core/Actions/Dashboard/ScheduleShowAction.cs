using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Calendar;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.Dashboard;

/// <summary>Inputs of <see cref="ScheduleShowAction"/>: none.</summary>
public sealed record ScheduleShowInputs
{
    public static ScheduleShowInputs Instance { get; } = new();
}

/// <summary>
/// <c>dashboard.schedule.show</c>: bring the dashboard forward with its Today panel showing the profile's latest
/// calendar read (usually the <c>m365.calendar.readFile</c> step just before it). It never reads the file itself,
/// so the allowlist on the read action cannot be sidestepped. The UI side effect goes through
/// <see cref="IDashboardPresenter"/>, which tests and the smoke modes replace with a recorder (rule 7).
/// </summary>
public sealed class ScheduleShowAction : WorkflowAction<ScheduleShowInputs>
{
    public const string ActionId = "dashboard.schedule.show";

    private readonly CalendarService _calendar;
    private readonly IDashboardPresenter _presenter;

    public ScheduleShowAction(CalendarService calendar, IDashboardPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(presenter);
        _calendar = calendar;
        _presenter = presenter;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Show today's schedule";

    public override IdempotencyClass Idempotency => IdempotencyClass.Idempotent;

    public override TimeSpan DefaultTimeout => TimeSpan.FromSeconds(10);

    public override ScheduleShowInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties();
        return ScheduleShowInputs.Instance;
    }

    public override Task<ActionResult> ExecuteAsync(ScheduleShowInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var latest = _calendar.GetLatest(context.Profile.Id);
        _presenter.ShowSchedule(context.Profile.Id);
        var message = latest is null
            ? "Showed the Today panel (no calendar has been read yet for this profile)"
            : $"Showed the Today panel ({latest.Message})";
        return Task.FromResult(ActionResult.Success(message));
    }
}
