using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OG_opossum_command.Core.Engine;
using OG_opossum_command.Core.Hosting;
using OG_opossum_command.Core.Profiles;

namespace OG_opossum_command.Desktop.CommandPalette;

/// <summary>One row of the palette: a workflow in a profile.</summary>
public sealed record PaletteItem(ProfileId Profile, string WorkflowId, string Label, string Aliases)
{
    /// <summary>Screen-reader name.</summary>
    public string AutomationName => string.IsNullOrEmpty(Aliases) ? Label : $"{Label}, also {Aliases}";
}

/// <summary>Type to search workflows by name or alias; Enter runs the selected one (spec §14 command palette).</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly CoreHost _host;

    public CommandPaletteViewModel(CoreHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        Query = string.Empty;
        Refresh();
    }

    /// <summary>Raised after a workflow was started (or focus-existing applied) with the feedback line; the window closes on it.</summary>
    public event Action<string>? Ran;

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial PaletteItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "Type to search; Enter runs, Esc closes.";

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>Moves the selection by <paramref name="delta"/> rows, clamped.</summary>
    public void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        index = Math.Clamp(index + delta, 0, Items.Count - 1);
        Selected = Items[index];
    }

    [RelayCommand]
    private void Run()
    {
        var item = Selected ?? Items.FirstOrDefault();
        if (item is null)
        {
            Message = "No workflow matches.";
            return;
        }

        try
        {
            var started = _host.Run(item.Profile, item.WorkflowId);
            Ran?.Invoke(started.WasAlreadyRunning
                ? $"{started.Execution.WorkflowName} is already running; showing that run."
                : $"Queued {started.Execution.WorkflowName}.");
        }
        catch (ActionNotAllowedException ex)
        {
            Message = ex.Message;
        }
        catch (KeyNotFoundException ex)
        {
            Message = ex.Message;
        }
        catch (ObjectDisposedException)
        {
            Message = "The app is shutting down.";
        }
    }

    private void Refresh()
    {
        var previous = Selected?.WorkflowId;
        Items.Clear();
        foreach (var match in _host.Search(Query))
        {
            Items.Add(new PaletteItem(
                match.Profile.Id,
                match.Workflow.Id,
                $"[{match.Profile.DisplayName}] {match.Workflow.Name}",
                string.Join(", ", match.Workflow.Aliases)));
        }

        Selected = Items.FirstOrDefault(i => i.WorkflowId == previous) ?? Items.FirstOrDefault();
        Message = Items.Count == 0 ? "No workflow matches." : "Enter runs the highlighted workflow; Esc closes.";
    }
}
