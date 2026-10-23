namespace OG_opossum_command.Core.Feedback;

/// <summary>The short sounds the dashboard can play (ADR-057).</summary>
public enum SoundCue
{
    /// <summary>A button press: a soft, short tick.</summary>
    Tap,

    /// <summary>Moving between pages: quieter than <see cref="Tap"/>.</summary>
    Navigate,

    /// <summary>A workflow was started: two rising notes.</summary>
    Start,

    /// <summary>A run completed: a soft three-note chime.</summary>
    Success,

    /// <summary>A run failed or needs attention: two low falling notes.</summary>
    Failure,
}

/// <summary>
/// When sounds play and when things move (ADR-057). Pure functions, so the rules are tested without a UI.
/// Windows settings always win: animation effects off, or high contrast on, turns motion off whatever the app setting says.
/// </summary>
public static class FeedbackPolicy
{
    /// <summary>Setting values are <c>"true"</c>/<c>"false"</c>; anything else (including unset) means on.</summary>
    public static bool IsOn(string? setting) => !string.Equals(setting?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Animations run only when Windows animation effects are on, high contrast is off, and the app setting is on.</summary>
    public static bool MotionEnabled(bool windowsAnimationEffects, bool highContrast, string? motionSetting) =>
        windowsAnimationEffects && !highContrast && IsOn(motionSetting);

    /// <summary>
    /// Whether <paramref name="cue"/> plays: never when sounds are off or the window is hidden; in quiet mode only a
    /// failure is heard, the same rule quiet mode applies to notifications.
    /// </summary>
    public static bool ShouldPlay(SoundCue cue, string? soundsSetting, bool quietMode, bool windowVisible) =>
        IsOn(soundsSetting) && windowVisible && (!quietMode || cue == SoundCue.Failure);
}
