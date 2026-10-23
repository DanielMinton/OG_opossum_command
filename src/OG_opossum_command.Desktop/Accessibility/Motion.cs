using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OG_opossum_command.Core.Feedback;

namespace OG_opossum_command.Desktop.Accessibility;

/// <summary>
/// The app's motion, as attached properties (ADR-057). Every animation goes through here so one gate,
/// <see cref="IsEnabled"/>, turns all of it off: Windows "Animation effects" off, high contrast on, or the app's own
/// Motion setting off. Only <c>Opacity</c> and transforms are animated (never brushes), so light, dark and high
/// contrast keep switching live. Endless animations run only while their element is visible.
/// </summary>
public static class Motion
{
    private static readonly Duration Fast = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration Medium = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration Slow = new(TimeSpan.FromMilliseconds(420));
    private static readonly IEasingFunction EaseOut = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
    private static readonly IEasingFunction Breathe = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static string? s_setting;

    static Motion()
    {
        SystemParameters.StaticPropertyChanged += (_, _) => Recompute();
        Recompute();
    }

    /// <summary>True when animations may run.</summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>Smoke runs take snapshots and must see the final frame, so they switch motion off.</summary>
    internal static bool ForceDisabled
    {
        get;
        set
        {
            field = value;
            Recompute();
        }
    }

    /// <summary>The app's Motion setting (<see cref="Core.History.SettingKeys.Motion"/>); applied immediately.</summary>
    public static void ApplySetting(string? value)
    {
        s_setting = value;
        Recompute();
    }

    /// <summary>Raised when <see cref="IsEnabled"/> flips (Windows setting, high contrast, or the app setting).</summary>
    public static event Action? EnabledChanged;

    // Elements with endless or hover animations, so a change of IsEnabled reaches them at once (ADR-057: one gate).
    private static readonly List<WeakReference<FrameworkElement>> s_tracked = [];

    private static void Recompute()
    {
        var enabled = !ForceDisabled && FeedbackPolicy.MotionEnabled(SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast, s_setting);
        if (enabled == IsEnabled)
        {
            return;
        }

        IsEnabled = enabled;
        s_tracked.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in s_tracked.ToList())
        {
            if (weak.TryGetTarget(out var element))
            {
                if (!enabled)
                {
                    ResetTransforms(element);
                }

                UpdateLoops(element);
            }
        }

        EnabledChanged?.Invoke();
    }

    private static void Track(FrameworkElement element)
    {
        if (!(bool)element.GetValue(TrackedProperty))
        {
            element.SetValue(TrackedProperty, true);
            s_tracked.Add(new WeakReference<FrameworkElement>(element));
        }
    }

    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "Tracked", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    /// <summary>Puts a hovered or pressed element back to rest, with no animation.</summary>
    private static void ResetTransforms(FrameworkElement element)
    {
        if (element.GetValue(TransformSetProperty) is TransformSet t)
        {
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            t.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        }

        if (element is Control { Template: { } template } control && template.FindName("Glow", control) is UIElement glow)
        {
            glow.BeginAnimation(UIElement.OpacityProperty, null);
            glow.Opacity = element.IsMouseOver && element.IsEnabled ? 1 : 0;
        }
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ---------------------------------------------------------------- Entrance: fade in and rise on Loaded

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));

    public static bool GetEntrance(DependencyObject d) => (bool)d.GetValue(EntranceProperty);

    public static void SetEntrance(DependencyObject d, bool value) => d.SetValue(EntranceProperty, value);

    /// <summary>Delay before the entrance starts, in milliseconds (for staggering sections).</summary>
    public static readonly DependencyProperty EntranceDelayProperty = DependencyProperty.RegisterAttached(
        "EntranceDelay", typeof(int), typeof(Motion), new PropertyMetadata(0));

    public static int GetEntranceDelay(DependencyObject d) => (int)d.GetValue(EntranceDelayProperty);

    public static void SetEntranceDelay(DependencyObject d, int value) => d.SetValue(EntranceDelayProperty, value);

    /// <summary>
    /// Inside an ItemsControl with an AlternationCount, adds the item's AlternationIndex times this many
    /// milliseconds to the delay, so items arrive one after another.
    /// </summary>
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(int), typeof(Motion), new PropertyMetadata(0));

    public static int GetStagger(DependencyObject d) => (int)d.GetValue(StaggerProperty);

    public static void SetStagger(DependencyObject d, int value) => d.SetValue(StaggerProperty, value);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnEntranceLoaded;
        if ((bool)e.NewValue)
        {
            element.Loaded += OnEntranceLoaded;
        }
    }

    private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!IsEnabled)
        {
            return;
        }

        var delay = GetEntranceDelay(element);
        var stagger = GetStagger(element);
        if (stagger > 0 && FindContainer(element) is { } container)
        {
            delay += ItemsControl.GetAlternationIndex(container) * stagger;
        }

        // Key frames hold the start values from t=0 until the delay, so a staggered element stays hidden until its
        // turn instead of showing, vanishing and fading in. Opacity ends at the element's own value (a disabled tile is dimmer).
        var wait = TimeSpan.FromMilliseconds(delay);
        var t = Transforms(element);
        element.BeginAnimation(UIElement.OpacityProperty, Held(0, element.Opacity, wait, Medium.TimeSpan));
        t.Translate.BeginAnimation(TranslateTransform.YProperty, Held(14, 0, wait, Slow.TimeSpan));
    }

    private static DoubleAnimationUsingKeyFrames Held(double from, double to, TimeSpan wait, TimeSpan duration) => new()
    {
        FillBehavior = FillBehavior.Stop,
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(wait)),
            new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(wait + duration), EaseOut),
        },
    };

    private static DependencyObject? FindContainer(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentPresenter or ContentControl && ItemsControl.ItemsControlFromItemContainer(current) is not null)
            {
                return current;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- Press: lift on hover, sink on press, spring back

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
        "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressChanged));

    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);

    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.MouseEnter -= OnHoverEnter;
        element.MouseLeave -= OnHoverLeave;
        element.PreviewMouseLeftButtonDown -= OnPressDown;
        element.PreviewMouseLeftButtonUp -= OnPressUp;
        element.LostMouseCapture -= OnPressUp;
        if ((bool)e.NewValue)
        {
            Track(element);
            element.MouseEnter += OnHoverEnter;
            element.MouseLeave += OnHoverLeave;
            element.PreviewMouseLeftButtonDown += OnPressDown;
            element.PreviewMouseLeftButtonUp += OnPressUp;
            element.LostMouseCapture += OnPressUp;
        }
    }

    private static void OnHoverEnter(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!element.IsEnabled)
        {
            return;
        }

        FadeGlow(element, 1);
        if (IsEnabled)
        {
            Transforms(element).Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, Medium) { EasingFunction = EaseOut });
        }
    }

    private static void OnHoverLeave(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        FadeGlow(element, 0);
        if (IsEnabled)
        {
            var t = Transforms(element);
            t.Translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Medium) { EasingFunction = EaseOut });
            ScaleTo(t.Scale, 1, Medium, Spring);
        }
    }

    private static void OnPressDown(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (IsEnabled && element.IsEnabled)
        {
            ScaleTo(Transforms(element).Scale, 0.965, Fast, EaseOut);
        }
    }

    private static void OnPressUp(object sender, RoutedEventArgs e)
    {
        if (IsEnabled)
        {
            ScaleTo(Transforms((FrameworkElement)sender).Scale, 1, Slow, Spring);
        }
    }

    /// <summary>Fades the template part named <c>Glow</c>, if the control has one (instant when motion is off).</summary>
    private static void FadeGlow(FrameworkElement element, double to)
    {
        if (element is Control { Template: { } template } control && template.FindName("Glow", control) is UIElement glow)
        {
            if (IsEnabled)
            {
                glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, Medium) { EasingFunction = EaseOut });
            }
            else
            {
                glow.BeginAnimation(UIElement.OpacityProperty, null);
                glow.Opacity = to;
            }
        }
    }

    private static void ScaleTo(ScaleTransform scale, double to, Duration duration, IEasingFunction easing)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = easing });
    }

    // ---------------------------------------------------------------- Pop: grow in whenever the element becomes visible

    public static readonly DependencyProperty PopProperty = DependencyProperty.RegisterAttached(
        "Pop", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPopChanged));

    public static bool GetPop(DependencyObject d) => (bool)d.GetValue(PopProperty);

    public static void SetPop(DependencyObject d, bool value) => d.SetValue(PopProperty, value);

    /// <summary>The vertical scale a Pop starts from (0.2 by default; larger for big panels, so they settle rather than grow).</summary>
    public static readonly DependencyProperty PopFromProperty = DependencyProperty.RegisterAttached(
        "PopFrom", typeof(double), typeof(Motion), new PropertyMetadata(0.2));

    public static double GetPopFrom(DependencyObject d) => (double)d.GetValue(PopFromProperty);

    public static void SetPopFrom(DependencyObject d, double value) => d.SetValue(PopFromProperty, value);

    private static void OnPopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnPopVisible;
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += OnPopVisible;
        }
    }

    private static void OnPopVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsEnabled && sender is FrameworkElement element)
        {
            var scale = Transforms(element).Scale;
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(GetPopFrom(element), 1, Slow) { EasingFunction = Spring, FillBehavior = FillBehavior.Stop });
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Fast) { FillBehavior = FillBehavior.Stop });
        }
    }

    // ---------------------------------------------------------------- Pulse and Spin: endless, only while visible

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));

    public static bool GetPulse(DependencyObject d) => (bool)d.GetValue(PulseProperty);

    private static readonly DependencyProperty PulsingProperty = DependencyProperty.RegisterAttached(
        "Pulsing", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static void SetPulse(DependencyObject d, bool value) => d.SetValue(PulseProperty, value);

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));

    public static bool GetSpin(DependencyObject d) => (bool)d.GetValue(SpinProperty);

    public static void SetSpin(DependencyObject d, bool value) => d.SetValue(SpinProperty, value);

    private static void OnLoopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        Track(element);
        element.IsVisibleChanged -= OnLoopVisible;
        element.IsVisibleChanged += OnLoopVisible;
        UpdateLoops(element);
    }

    private static void OnLoopVisible(object sender, DependencyPropertyChangedEventArgs e) => UpdateLoops((FrameworkElement)sender);

    private static void UpdateLoops(FrameworkElement element)
    {
        var run = IsEnabled && element.IsVisible;
        var pulsing = (bool)element.GetValue(PulsingProperty);
        if (run && GetPulse(element))
        {
            if (!pulsing)
            {
                element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(1300)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = Breathe,
                });
                element.SetValue(PulsingProperty, true);
            }
        }
        else if (pulsing)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.SetValue(PulsingProperty, false);
        }

        var rotate = Transforms(element).Rotate;
        if (run && GetSpin(element))
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(1100)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    // ---------------------------------------------------------------- CountTo: a number that counts up to its value

    /// <summary>The number a TextBlock shows; NaN shows a dash. It counts from the previous value when motion is on.</summary>
    public static readonly DependencyProperty CountToProperty = DependencyProperty.RegisterAttached(
        "CountTo", typeof(double), typeof(Motion), new PropertyMetadata(double.NaN, OnCountToChanged));

    public static double GetCountTo(DependencyObject d) => (double)d.GetValue(CountToProperty);

    public static void SetCountTo(DependencyObject d, double value) => d.SetValue(CountToProperty, value);

    /// <summary>Text after the number, e.g. <c>%</c>.</summary>
    public static readonly DependencyProperty CountSuffixProperty = DependencyProperty.RegisterAttached(
        "CountSuffix", typeof(string), typeof(Motion), new PropertyMetadata(string.Empty));

    public static string GetCountSuffix(DependencyObject d) => (string)d.GetValue(CountSuffixProperty);

    public static void SetCountSuffix(DependencyObject d, string value) => d.SetValue(CountSuffixProperty, value);

    private static readonly DependencyProperty CountShownProperty = DependencyProperty.RegisterAttached(
        "CountShown", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnCountShownChanged));

    private static void OnCountToChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        var to = (double)e.NewValue;
        if (double.IsNaN(to))
        {
            text.BeginAnimation(CountShownProperty, null);
            text.Text = "—";
            return;
        }

        // Count on from the number on screen, so a second update mid-count does not jump.
        var from = e.OldValue is double old && !double.IsNaN(old) ? (double)text.GetValue(CountShownProperty) : 0;
        if (IsEnabled && Math.Abs(to - from) >= 1)
        {
            text.BeginAnimation(CountShownProperty, new DoubleAnimation(from, to, Slow) { EasingFunction = EaseOut });
        }
        else
        {
            text.BeginAnimation(CountShownProperty, null);
            text.SetValue(CountShownProperty, to);
            ShowCount(text, to);
        }
    }

    private static void OnCountShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text)
        {
            ShowCount(text, (double)e.NewValue);
        }
    }

    private static void ShowCount(TextBlock text, double value) =>
        text.Text = Math.Round(value).ToString("N0", CultureInfo.CurrentCulture) + GetCountSuffix(text);

    // ---------------------------------------------------------------- Shared transform stack

    private sealed record TransformSet(ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Translate);

    private static readonly DependencyProperty TransformSetProperty = DependencyProperty.RegisterAttached(
        "TransformSet", typeof(TransformSet), typeof(Motion));

    /// <summary>Gives the element one scale, rotate and translate that every Motion feature shares.</summary>
    private static TransformSet Transforms(UIElement element)
    {
        if (element.GetValue(TransformSetProperty) is TransformSet existing)
        {
            return existing;
        }

        var set = new TransformSet(new ScaleTransform(), new RotateTransform(), new TranslateTransform());
        element.RenderTransform = new TransformGroup { Children = { set.Scale, set.Rotate, set.Translate } };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.SetValue(TransformSetProperty, set);
        return set;
    }
}
