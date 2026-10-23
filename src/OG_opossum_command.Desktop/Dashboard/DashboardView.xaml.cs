using System.Windows.Controls;

namespace OG_opossum_command.Desktop.Dashboard;

/// <summary>Dashboard surface; its DataContext is a <see cref="DashboardViewModel"/> (set by the App resources DataTemplate).</summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }
}
