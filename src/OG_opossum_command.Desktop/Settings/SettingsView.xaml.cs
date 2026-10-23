using System.Windows.Controls;

namespace OG_opossum_command.Desktop.Settings;

/// <summary>Settings surface; its DataContext is a <see cref="SettingsViewModel"/> (set by the App resources DataTemplate).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
