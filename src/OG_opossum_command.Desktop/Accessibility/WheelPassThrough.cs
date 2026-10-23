using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OG_opossum_command.Desktop.Accessibility;

/// <summary>
/// Sends the mouse wheel past a control to the page that contains it (ADR-055).
/// A closed ComboBox would otherwise change its selection on the wheel (for the profile picker that silently
/// switched Work and Personal), and a list inside a scrolling page would swallow the wheel so the page stopped
/// scrolling under the pointer. An open ComboBox drop-down still scrolls itself.
/// </summary>
public static class WheelPassThrough
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(WheelPassThrough),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element)
        {
            return;
        }

        if (sender is ComboBox { IsDropDownOpen: true })
        {
            return;
        }

        e.Handled = true;
        if (VisualTreeHelper.GetParent(element) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
        }
    }
}
