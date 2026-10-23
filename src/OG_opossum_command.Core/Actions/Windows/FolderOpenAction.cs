using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.Windows;

/// <summary>Inputs of <see cref="FolderOpenAction"/>: a full folder path; <c>%VAR%</c> references are expanded at run time.</summary>
public sealed record FolderOpenInputs(string Path);

/// <summary><c>windows.folder.open</c>: show a folder in the file manager.</summary>
public sealed class FolderOpenAction : WorkflowAction<FolderOpenInputs>
{
    public const string ActionId = "windows.folder.open";

    private readonly IFolderOpener _opener;

    public FolderOpenAction(IFolderOpener opener)
    {
        ArgumentNullException.ThrowIfNull(opener);
        _opener = opener;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Open folder";

    public override IdempotencyClass Idempotency => IdempotencyClass.Idempotent;

    public override TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);

    public override FolderOpenInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties("path");

        var path = inputs.RequiredString("path");
        if (path is null)
        {
            return null;
        }

        if (!System.IO.Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(path)))
        {
            inputs.AddError("path", $"'path' must be a full folder path (got '{path}'). %VAR% references are allowed.");
            return null;
        }

        return new FolderOpenInputs(path);
    }

    public override Task<ActionResult> ExecuteAsync(FolderOpenInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        var folder = Environment.ExpandEnvironmentVariables(inputs.Path);
        if (!Directory.Exists(folder))
        {
            return Task.FromResult(ActionResult.Failure("folder-not-found", $"Folder not found: {folder}"));
        }

        _opener.Open(folder);
        return Task.FromResult(ActionResult.Success($"Opened {folder}"));
    }
}
