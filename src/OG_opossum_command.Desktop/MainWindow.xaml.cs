using System.Windows;

namespace OG_opossum_command.Desktop;

/// <summary>Dashboard shell window. Its DataContext is the <see cref="Shell.ShellViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _loaded.TrySetResult();
    }

    /// <summary>Completes once WPF has raised <see cref="FrameworkElement.Loaded"/> (used by <c>--smoke-ui</c>).</summary>
    public Task LoadedTask => _loaded.Task;
}
