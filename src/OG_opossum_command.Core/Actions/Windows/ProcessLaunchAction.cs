using System.Collections.Frozen;
using System.Globalization;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.Windows;

/// <summary>Inputs of <see cref="ProcessLaunchAction"/>.</summary>
/// <param name="Path">Full path to an <c>.exe</c>; <c>%VAR%</c> environment references are expanded at run time.</param>
/// <param name="Arguments">Argument array, passed through without any shell parsing. <c>%VAR%</c> is expanded in each.</param>
/// <param name="WorkingDirectory">Optional working directory (expanded); defaults to the process default.</param>
/// <param name="WaitForExit">When true the step waits for the process to exit and fails on a non-zero exit code; cancellation kills the whole process tree.</param>
public sealed record ProcessLaunchInputs(string Path, IReadOnlyList<string> Arguments, string? WorkingDirectory, bool WaitForExit);

/// <summary><c>windows.process.launch</c>: start an executable from a path and an argument array.</summary>
public sealed class ProcessLaunchAction : WorkflowAction<ProcessLaunchInputs>
{
    public const string ActionId = "windows.process.launch";

    /// <summary>Stable error code when the path names a shell or script host (ADR-031: scripts only run through <c>windows.script.run</c>).</summary>
    public const string InterpreterRefusedCode = "interpreter-refused";

    private const string InterpreterRefusal =
        "Shells and script hosts (cmd, powershell, pwsh, wscript, cscript, mshta, ...) cannot be started with windows.process.launch. Put the script in the profile's scripts folder and use windows.script.run.";

    /// <summary>
    /// File names of shells, script hosts, and binaries that run arbitrary inline code. Starting them here would skip
    /// every <c>windows.script.run</c> rule (trusted folder, explicit interpreter, no <c>-ExecutionPolicy</c>).
    /// </summary>
    private static readonly FrozenSet<string> Interpreters = new[]
    {
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "wsl.exe", "bash.exe", "conhost.exe", "forfiles.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IProcessLauncher _launcher;

    public ProcessLaunchAction(IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Launch program";

    public override IdempotencyClass Idempotency => IdempotencyClass.NonIdempotent;

    /// <summary>Generous because a step may wait for a long-running tool; a launch without waiting returns at once.</summary>
    public override TimeSpan DefaultTimeout => TimeSpan.FromMinutes(5);

    public override ProcessLaunchInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties("path", "arguments", "workingDirectory", "waitForExit");

        var path = inputs.RequiredString("path");
        var arguments = inputs.OptionalStringArray("arguments") ?? [];
        var workingDirectory = inputs.OptionalString("workingDirectory");
        var waitForExit = inputs.OptionalBool("waitForExit") ?? false;

        if (path is not null)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (path.Contains('\0', StringComparison.Ordinal))
            {
                inputs.AddError("path", "'path' contains a NUL character (\\u0000).");
            }
            else if (!System.IO.Path.IsPathFullyQualified(expanded))
            {
                inputs.AddError("path", $"'path' must be a full path to an executable (got '{path}'). %VAR% references are allowed.");
            }
            else if (!string.Equals(System.IO.Path.GetExtension(expanded), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                inputs.AddError("path", $"'path' must point to an .exe file. Scripts run with {ScriptRunAction.ActionId}.");
            }
            else if (IsInterpreter(expanded))
            {
                inputs.AddError("path", InterpreterRefusal);
            }
        }

        if (workingDirectory is not null)
        {
            var expandedFolder = Environment.ExpandEnvironmentVariables(workingDirectory);
            if (workingDirectory.Contains('\0', StringComparison.Ordinal))
            {
                inputs.AddError("workingDirectory", "'workingDirectory' contains a NUL character (\\u0000).");
            }
            else if (!System.IO.Path.IsPathFullyQualified(expandedFolder))
            {
                inputs.AddError("workingDirectory", "'workingDirectory' must be a full path.");
            }
        }

        return path is null ? null : new ProcessLaunchInputs(path, arguments, workingDirectory, waitForExit);
    }

    /// <summary>True when the fully qualified, NUL-free <paramref name="fullPath"/> names a shell or script host (compared by file name, any case).</summary>
    public static bool IsInterpreter(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // GetFullPath drops trailing dots and spaces, so "cmd.exe." is still cmd.exe.
        return Interpreters.Contains(System.IO.Path.GetFileName(System.IO.Path.GetFullPath(fullPath)));
    }

    public override async Task<ActionResult> ExecuteAsync(ProcessLaunchInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var executable = Environment.ExpandEnvironmentVariables(inputs.Path);
        if (!File.Exists(executable))
        {
            return ActionResult.Failure("executable-not-found", $"Program not found: {executable}");
        }

        // Checked again after expansion: a %VAR% can change between load and run.
        if (IsInterpreter(executable))
        {
            return ActionResult.Failure(InterpreterRefusedCode, InterpreterRefusal);
        }

        var workingDirectory = inputs.WorkingDirectory is null ? null : Environment.ExpandEnvironmentVariables(inputs.WorkingDirectory);
        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            return ActionResult.Failure("working-directory-not-found", $"Working folder not found: {workingDirectory}");
        }

        var arguments = inputs.Arguments.Select(Environment.ExpandEnvironmentVariables).ToList();
        var request = new ProcessLaunchRequest(executable, arguments, workingDirectory, CaptureOutput: inputs.WaitForExit);

        ILaunchedProcess process;
        try
        {
            process = _launcher.Start(request);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ActionResult.Failure("launch-failed", $"Windows refused to start {executable}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return ActionResult.Failure("launch-failed", $"Could not start {executable}: {ex.Message}");
        }

        using (process)
        {
            var pid = process.ProcessId.ToString(CultureInfo.InvariantCulture);
            if (!inputs.WaitForExit)
            {
                return ActionResult.Success($"Started {System.IO.Path.GetFileName(executable)} (pid {pid})");
            }

            ProcessExit exit;
            try
            {
                exit = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation or timeout: the in-memory token is not the process; kill the tree explicitly (spec §06).
                process.KillTree();
                throw;
            }

            var output = string.IsNullOrEmpty(exit.StandardError)
                ? exit.StandardOutput
                : $"{exit.StandardOutput}{Environment.NewLine}[stderr]{Environment.NewLine}{exit.StandardError}";
            var exitCode = exit.ExitCode.ToString(CultureInfo.InvariantCulture);
            return exit.ExitCode == 0
                ? ActionResult.Success($"{System.IO.Path.GetFileName(executable)} finished (exit code 0)", output)
                : ActionResult.Failure("exit-code", $"{System.IO.Path.GetFileName(executable)} exited with code {exitCode}", output);
        }
    }
}
