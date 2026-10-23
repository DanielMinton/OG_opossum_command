using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using OG_opossum_command.Core.Feedback;

namespace OG_opossum_command.Desktop.Feedback;

/// <summary>
/// Plays the app's short sounds (ADR-057). The cues are synthesized once, off the UI thread, by
/// <see cref="ToneSynth"/>; until they are ready a cue is simply skipped. Whether a cue plays is decided by
/// <see cref="FeedbackPolicy.ShouldPlay"/> with the live settings, quiet mode, and whether the window is visible.
/// Nothing plays in smoke runs (no player is installed).
/// </summary>
public static class UiFeedback
{
    private static SoundCuePlayer? s_player;
    private static Func<string?>? s_soundsSetting;
    private static Func<bool>? s_quietMode;

    /// <summary>Attached to any button: the cue its Click plays.</summary>
    public static readonly DependencyProperty CueProperty = DependencyProperty.RegisterAttached(
        "Cue", typeof(SoundCue?), typeof(UiFeedback), new PropertyMetadata(null, OnCueChanged));

    public static SoundCue? GetCue(DependencyObject element) => (SoundCue?)element?.GetValue(CueProperty);

    public static void SetCue(DependencyObject element, SoundCue? value) => element?.SetValue(CueProperty, value);

    /// <summary>Installs the player. <paramref name="soundsSetting"/> and <paramref name="quietMode"/> are read on every cue.</summary>
    internal static void Install(SoundCuePlayer player, Func<string?> soundsSetting, Func<bool> quietMode)
    {
        s_player = player;
        s_soundsSetting = soundsSetting;
        s_quietMode = quietMode;
    }

    /// <summary>Plays <paramref name="cue"/> if the settings allow it; never throws.</summary>
    public static void Play(SoundCue cue)
    {
        if (s_player is null || s_soundsSetting is null || s_quietMode is null)
        {
            return;
        }

        var visible = Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };
        if (FeedbackPolicy.ShouldPlay(cue, s_soundsSetting(), s_quietMode(), visible))
        {
            s_player.Play(cue);
        }
    }

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
