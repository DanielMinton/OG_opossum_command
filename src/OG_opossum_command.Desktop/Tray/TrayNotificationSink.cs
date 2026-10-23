using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using OG_opossum_command.Core.Notifications;
using OG_opossum_command.Desktop.Feedback;

namespace OG_opossum_command.Desktop.Tray;

/// <summary>Shows notifications as tray balloons (ADR-037). Events arrive on worker threads, so every call is marshalled to the dispatcher.</summary>
internal sealed class TrayNotificationSink : INotificationSink
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<TaskbarIcon?> _icon;

    public TrayNotificationSink(Dispatcher dispatcher, Func<TaskbarIcon?> icon)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(icon);
        _dispatcher = dispatcher;
        _icon = icon;
    }

    public void Show(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            var icon = _icon();
            if (icon is null)
            {
                return;
            }

            var kind = notification.Level switch
            {
                NotificationLevel.Error => NotificationIcon.Error,
                NotificationLevel.Warning => NotificationIcon.Warning,
                _ => NotificationIcon.Info,
            };
            try
            {
                // Silent while the app sounds the result itself (window visible, Sounds on), so it is never chime plus ding (ADR-057).
                icon.ShowNotification(notification.Title, notification.Message, kind, sound: !UiFeedback.PlaysResultSounds);
            }
            catch (InvalidOperationException)
            {
                // Icon not created yet or already disposed; a missed balloon is not worth a crash.
            }
        });
    }
}
