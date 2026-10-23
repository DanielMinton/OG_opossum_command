using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Actions.Windows;

namespace OG_opossum_command.Core.Actions;

/// <summary>Registered actions keyed by their stable IDs.</summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, IWorkflowAction> _actions = new(StringComparer.Ordinal);

    public ActionRegistry(IEnumerable<IWorkflowAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var action in actions)
        {
            if (!_actions.TryAdd(action.Id, action))
            {
                throw new ArgumentException($"Action ID '{action.Id}' is registered twice.", nameof(actions));
            }
        }
    }

    /// <summary>All registered IDs, sorted.</summary>
    public IReadOnlyList<string> Ids => _actions.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>All registered actions.</summary>
    public IReadOnlyCollection<IWorkflowAction> Actions => _actions.Values;

    public bool Contains(string actionId) => _actions.ContainsKey(actionId);

    public bool TryGet(string actionId, out IWorkflowAction action) => _actions.TryGetValue(actionId, out action!);

    /// <summary>The built-in actions, each wired to the given launchers (real ones for the app, recording fakes for tests and smoke modes).</summary>
    public static ActionRegistry CreateDefault(LauncherSet launchers)
    {
        ArgumentNullException.ThrowIfNull(launchers);
        return new ActionRegistry(
        [
            new ProcessLaunchAction(launchers.Processes),
            new UrlOpenAction(launchers.Urls),
            new FolderOpenAction(launchers.Folders),
            new ScriptRunAction(launchers.Processes),
        ]);
    }
}
