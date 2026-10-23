using System.Windows.Controls;

namespace OG_opossum_command.Desktop.History;

/// <summary>History surface; its DataContext is a <see cref="HistoryViewModel"/> (set by the App resources DataTemplate).</summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }
}
