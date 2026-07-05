using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="INotificationService"/> (TASK-061): shows in-app toasts via a
/// <see cref="WindowNotificationManager"/> anchored to the main window. The manager is created lazily on
/// first use (after a window exists) and reused. Showing is marshalled to the UI thread, so the download
/// notifier can call it from a background completion callback.
/// </summary>
public sealed class AvaloniaNotificationService : INotificationService
{
    private WindowNotificationManager? _manager;

    public void Notify(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Dispatcher.UIThread.Post(() =>
        {
            WindowNotificationManager? manager = Resolve();
            manager?.Show(new Notification(
                notification.Title, notification.Message, ToType(notification.Kind), onClick: notification.OnClick));
        });
    }

    private WindowNotificationManager? Resolve()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } window })
        {
            _manager = new WindowNotificationManager(window) { MaxItems = 4 };
        }

        return _manager;
    }

    private static NotificationType ToType(AppNotificationKind kind) => kind switch
    {
        AppNotificationKind.Success => NotificationType.Success,
        AppNotificationKind.Error => NotificationType.Error,
        _ => NotificationType.Information,
    };
}
