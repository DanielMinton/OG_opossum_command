namespace OG_opossum_command.Core.Tests.Support;

/// <summary>A clock the test moves by hand. Only <see cref="GetUtcNow"/> is overridden; the scheduler is ticked explicitly, so no timer is needed.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}
