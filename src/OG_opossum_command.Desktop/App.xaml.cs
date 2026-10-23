using System.Windows;

namespace OG_opossum_command.Desktop;

/// <summary>WPF application. Constructed by <see cref="Program"/> only for interactive runs.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
