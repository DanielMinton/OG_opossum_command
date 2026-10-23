using System.Windows.Controls;
using H.NotifyIcon;
using OG_opossum_command.Core.Profiles;
using OG_opossum_command.Desktop.Shell;

namespace OG_opossum_command.Desktop.Tray;

/// <summary>
/// The notification-area icon (ADR-004): Open, Switch profile (Work / Personal), Quit. Double-click opens the
/// dashboard. Built in code rather than XAML so the smoke runner can create and dispose it deterministically.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ShellViewModel _shell;
    private readonly MenuItem _workItem;
    private readonly MenuItem _personalItem;
    private bool _disposed;

    public TrayIcon(App app, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;

        var menu = new ContextMenu();

        var open = new MenuItem { Header = "_Open OG Opossum Command Central" };
        open.Click += (_, _) => app.ShowDashboard();
        menu.Items.Add(open);

        var palette = new MenuItem { Header = "_Command palette" };
        palette.Click += (_, _) => shell.OpenPaletteCommand.Execute(null);
        menu.Items.Add(palette);

        var settings = new MenuItem { Header = "S_ettings" };
        settings.Click += (_, _) =>
        {
            shell.ShowSettingsCommand.Execute(null);
            app.ShowDashboard();
        };
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        var switchProfile = new MenuItem { Header = "_Switch profile" };
        _workItem = new MenuItem { Header = ProfileId.Work.DisplayName(), IsCheckable = true };
        _workItem.Click += (_, _) => shell.SwitchProfileCommand.Execute(ProfileId.Work);
        _personalItem = new MenuItem { Header = ProfileId.Personal.DisplayName(), IsCheckable = true };
        _personalItem.Click += (_, _) => shell.SwitchProfileCommand.Execute(ProfileId.Personal);
        switchProfile.Items.Add(_workItem);
        switchProfile.Items.Add(_personalItem);
        menu.Items.Add(switchProfile);

        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "_Quit" };
        quit.Click += (_, _) => shell.QuitCommand.Execute(null);
        menu.Items.Add(quit);

        menu.Opened += (_, _) => SyncProfileChecks();
        SyncProfileChecks();

        _icon = new TaskbarIcon
        {
            ToolTipText = "OG Opossum Command Central",
            // From the WindowsDesktop shared framework (System.Drawing.Common ships with WPF); no extra package (ADR-027).
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenu = menu,
        };
        _icon.TrayMouseDoubleClick += (_, _) => app.ShowDashboard();
        _icon.TrayLeftMouseUp += (_, _) => app.ShowDashboard();
        _icon.ForceCreate();
    }

    /// <summary>The underlying icon, for balloon notifications; <c>null</c> once disposed.</summary>
    public TaskbarIcon? Icon => _disposed ? null : _icon;

    private void SyncProfileChecks()
    {
        _workItem.IsChecked = _shell.ActiveProfile == ProfileId.Work;
        _personalItem.IsChecked = _shell.ActiveProfile == ProfileId.Personal;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Dispose();
    }
}
