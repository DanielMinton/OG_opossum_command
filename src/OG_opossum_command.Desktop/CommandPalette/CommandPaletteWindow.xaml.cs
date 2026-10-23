using System.Windows;
using System.Windows.Input;

namespace OG_opossum_command.Desktop.CommandPalette;

/// <summary>Small topmost search box opened by the global hotkey. Enter runs, Esc or losing focus closes.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _viewModel;
    private readonly Action<string>? _onRan;

    public CommandPaletteWindow(CommandPaletteViewModel viewModel, Action<string>? onRan)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _onRan = onRan;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Ran += OnRan;
        Loaded += (_, _) => QueryBox.Focus();
        Deactivated += (_, _) =>
        {
            if (!SuppressCloseOnDeactivate)
            {
                Close();
            }
        };
        Closed += (_, _) => viewModel.Ran -= OnRan;
    }

    /// <summary>Set by the smoke runner, which shows the window off-screen without activating it.</summary>
    public bool SuppressCloseOnDeactivate { get; init; }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                return;
            case Key.Enter:
                e.Handled = true;
                _viewModel.RunCommand.Execute(null);
                return;
            case Key.Down:
                e.Handled = true;
                _viewModel.Move(+1);
                return;
            case Key.Up:
                e.Handled = true;
                _viewModel.Move(-1);
                return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnRan(string message)
    {
        _onRan?.Invoke(message);
        Close();
    }
}
