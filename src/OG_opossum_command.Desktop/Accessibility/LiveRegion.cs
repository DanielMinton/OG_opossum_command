using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace OG_opossum_command.Desktop.Accessibility;

/// <summary>
/// <c>LiveRegion.Announce="True"</c> on a TextBlock makes it a polite live region that screen readers actually announce.
/// WPF only reports <see cref="AutomationProperties.LiveSettingProperty"/>; nothing is read until the app raises
/// <see cref="AutomationEvents.LiveRegionChanged"/>, which this does whenever the text changes.
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached(
        "Announce",
        typeof(bool),
        typeof(LiveRegion),
        new PropertyMetadata(false, OnAnnounceChanged));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static bool GetAnnounce(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(AnnounceProperty);
    }

    public static void SetAnnounce(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AnnounceProperty, value);
    }

    private static void OnAnnounceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || e.NewValue is not true)
        {
            return;
        }

        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        // Subscribe only while loaded: the descriptor holds a strong reference to the element.
        text.Loaded += (_, _) => TextDescriptor.AddValueChanged(text, OnTextChanged);
        text.Unloaded += (_, _) => TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        if (text.IsLoaded)
        {
            TextDescriptor.AddValueChanged(text, OnTextChanged);
        }
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock { Text.Length: > 0 } text)
        {
            // After layout: a line collapsed while empty (ADR-057) has only just become visible when its text arrives.
            text.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var peer = UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
        }
    }
}
