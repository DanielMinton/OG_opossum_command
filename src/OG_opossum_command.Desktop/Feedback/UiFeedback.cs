using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using OG_opossum_command.Core.Feedback;

namespace OG_opossum_command.Desktop.Feedback;

/// <summary>
/// Plays the app's short sounds (ADR-057). The cues are synthesized once, off the UI thread, by
/// <see cref="ToneSynth"/>; until they are ready a cue is simply skipped. Whether a cue plays is decided by
/// <see cref="FeedbackPolicy.ShouldPlay"/> with the Sounds and quiet-mode settings (kept here, updated by Settings, so
/// a click never touches the database) and whether the window is visible. Nothing plays in smoke runs (no player).
/// </summary>
public static class UiFeedback
{
    private static SoundCuePlayer? s_player;
    private static volatile string? s_soundsSetting;
    private static volatile bool s_quietMode;

    /// <summary>Attached to any button: the cue its Click plays.</summary>
    public static readonly DependencyProperty CueProperty = DependencyProperty.RegisterAttached(
        "Cue", typeof(SoundCue?), typeof(UiFeedback), new PropertyMetadata(null, OnCueChanged));

    public static SoundCue? GetCue(DependencyObject element) => (SoundCue?)element?.GetValue(CueProperty);

    public static void SetCue(DependencyObject element, SoundCue? value) => element?.SetValue(CueProperty, value);

    /// <summary>Installs the player with the current settings.</summary>
    internal static void Install(SoundCuePlayer player, string? soundsSetting, bool quietMode)
    {
        s_player = player;
        s_soundsSetting = soundsSetting;
        s_quietMode = quietMode;
    }

    /// <summary>Called by Settings when Sounds or quiet mode changes.</summary>
    internal static void UpdateSettings(string? soundsSetting, bool quietMode)
    {
        s_soundsSetting = soundsSetting;
        s_quietMode = quietMode;
    }

    /// <summary>
    /// True when the app itself sounds a run result: the player is installed, Sounds is on and the window is visible.
    /// Tray balloons are then shown silently, so a result is never chime plus Windows ding (ADR-057).
    /// </summary>
    internal static bool PlaysResultSounds => s_player is not null && FeedbackPolicy.IsOn(s_soundsSetting) && WindowVisible;

    private static bool WindowVisible => Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

    /// <summary>Plays <paramref name="cue"/> if the settings allow it; never throws.</summary>
    public static void Play(SoundCue cue)
    {
        if (s_player is { } player && FeedbackPolicy.ShouldPlay(cue, s_soundsSetting, s_quietMode, WindowVisible))
        {
            player.Play(cue);
        }
    }

    /// <summary>Plays <paramref name="cue"/> as a preview after the user turned Sounds on, even in quiet mode.</summary>
    public static void Preview(SoundCue cue) => s_player?.Play(cue);

    private static void OnCueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnClick;
        if (e.NewValue is SoundCue)
        {
            button.Click += OnClick;
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d && GetCue(d) is { } cue)
        {
            Play(cue);
        }
    }
}

/// <summary>One preloaded <see cref="SoundPlayer"/> per cue. Windows plays one such sound at a time; a new cue replaces the last.</summary>
internal sealed class SoundCuePlayer : IDisposable
{
    private readonly Dictionary<SoundCue, SoundPlayer> _players = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Synthesizes and loads every cue on the thread pool.</summary>
    public Task LoadAsync() => Task.Run(() =>
    {
        foreach (var cue in Enum.GetValues<SoundCue>())
        {
            var player = new SoundPlayer(new MemoryStream(ToneSynth.RenderWav(cue), writable: false));
            player.Load();
            lock (_gate)
            {
                if (_disposed)
                {
                    player.Stream?.Dispose();
                    player.Dispose();
                    return;
                }

                _players[cue] = player;
            }
        }
    });

    public void Play(SoundCue cue)
    {
        SoundPlayer? player;
        lock (_gate)
        {
            if (_disposed || !_players.TryGetValue(cue, out player))
            {
                return;
            }
        }

        try
        {
            player.Play();
        }
        catch (InvalidOperationException)
        {
            // No audio device, or the sound system refused: sounds are decoration, so stay silent.
        }
        catch (TimeoutException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var player in _players.Values)
            {
                player.Stream?.Dispose();
                player.Dispose();
            }

            _players.Clear();
        }
    }
}
