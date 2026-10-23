using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.Windows;

/// <summary>The interpreters <see cref="ScriptRunAction"/> knows. Each maps to one fixed executable under <c>%SystemRoot%</c>.</summary>
public enum ScriptInterpreter
{
    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>), run with <c>-NoProfile -NonInteractive -File</c>.</summary>
    PowerShell,

    /// <summary><c>cmd.exe /d /c</c> for <c>.cmd</c> and <c>.bat</c> files.</summary>
    Cmd,
}

/// <summary>Inputs of <see cref="ScriptRunAction"/>.</summary>
/// <param name="Interpreter">Which interpreter runs the script.</param>
/// <param name="Script">Bare file name inside the profile's <c>scripts</c> folder; never a path.</param>
/// <param name="Arguments">
/// Argument array appended after the script path. For PowerShell it is passed as-is with <c>%VAR%</c> expanded. For cmd,
/// which re-parses its command line, no argument may contain <c>&amp; | &lt; &gt; ^ % ! " ( )</c> or a line break.
/// </param>
public sealed record ScriptRunInputs(ScriptInterpreter Interpreter, string Script, IReadOnlyList<string> Arguments);

/// <summary>
/// <c>windows.script.run</c>: run a trusted script that lives in the profile's own <c>scripts</c> folder (ADR-031).
/// The interpreter is explicit, arguments are an array, stdout/stderr are captured with a hard cap, and a script
/// that PowerShell's execution policy refuses is reported as <c>execution-policy-blocked</c>. The app never passes
/// <c>-ExecutionPolicy</c> or touches the policy (rule 2).
/// </summary>
public sealed partial class ScriptRunAction : WorkflowAction<ScriptRunInputs>
{
    public const string ActionId = "windows.script.run";

    /// <summary>Stable error code when PowerShell refused to run the script because of execution policy.</summary>
    public const string ExecutionPolicyBlockedCode = "execution-policy-blocked";

    private static readonly SearchValues<char> CmdMetacharacters = SearchValues.Create("&|<>^%!\"()\r\n");

    private readonly IProcessLauncher _launcher;

    public ScriptRunAction(IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Run script";

    public override IdempotencyClass Idempotency => IdempotencyClass.NonIdempotent;

    public override TimeSpan DefaultTimeout => TimeSpan.FromMinutes(5);

    /// <summary>Full path of the interpreter executable; always under <c>%SystemRoot%</c>.</summary>
    public static string InterpreterPath(ScriptInterpreter interpreter) => interpreter switch
    {
        ScriptInterpreter.PowerShell => Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
        ScriptInterpreter.Cmd => Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe"),
        _ => throw new ArgumentOutOfRangeException(nameof(interpreter), interpreter, "Unknown interpreter."),
    };

    /// <summary>True when the interpreter's output says execution policy stopped the script.</summary>
    public static bool LooksLikeExecutionPolicyBlock(string? output) =>
        !string.IsNullOrEmpty(output) && ExecutionPolicyPattern().IsMatch(output);

    public override ScriptRunInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties("interpreter", "script", "arguments");

        var interpreterText = inputs.RequiredString("interpreter");
        var script = inputs.RequiredString("script");
        var arguments = inputs.OptionalStringArray("arguments") ?? [];

        ScriptInterpreter? interpreter = interpreterText switch
        {
            "powershell" => ScriptInterpreter.PowerShell,
            "cmd" => ScriptInterpreter.Cmd,
            null => null,
            _ => null,
        };
        if (interpreterText is not null && interpreter is null)
        {
            inputs.AddError("interpreter", $"'interpreter' must be \"powershell\" or \"cmd\" (got '{interpreterText}').");
        }

        if (script is not null)
        {
            if (!ScriptNamePattern().IsMatch(script) || script.Contains("..", StringComparison.Ordinal))
            {
                inputs.AddError("script", $"'script' must be a bare file name inside the profile's scripts folder (letters, digits, spaces, '.', '_', '-'), not a path (got '{script}').");
                script = null;
            }
            else if (interpreter is not null)
            {
                var extension = Path.GetExtension(script).ToLowerInvariant();
                var expected = interpreter == ScriptInterpreter.PowerShell ? new[] { ".ps1" } : [".cmd", ".bat"];
                if (!expected.Contains(extension))
                {
                    inputs.AddError("script", $"'script' must end in {string.Join(" or ", expected)} for the {interpreterText} interpreter (got '{script}').");
                    script = null;
                }
                else if (interpreter == ScriptInterpreter.Cmd && script.Contains(' ', StringComparison.Ordinal))
                {
                    inputs.AddError("script", $"'script' may not contain spaces for the cmd interpreter (got '{script}'); rename the file, for example with '-' or '_'.");
                    script = null;
                }
            }
        }

        if (interpreter == ScriptInterpreter.Cmd && CmdArgumentProblem(arguments) is { } problem)
        {
            inputs.AddError("arguments", problem);
        }

        return interpreter is null || script is null ? null : new ScriptRunInputs(interpreter.Value, script, arguments);
    }

    /// <summary>
    /// Why <paramref name="arguments"/> cannot be handed to <c>cmd.exe</c>, or <c>null</c> when they can. cmd parses its
    /// command line again after <c>/c</c>, so an argument array is only an argument array when no argument carries a
    /// character cmd treats specially (ADR-031).
    /// </summary>
    public static string? CmdArgumentProblem(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var bad = arguments.FirstOrDefault(a => a.AsSpan().ContainsAny(CmdMetacharacters));
        return bad is null
            ? null
            : $"cmd arguments may not contain & | < > ^ % ! \" ( ) or line breaks, because cmd.exe would interpret them (got '{bad.ReplaceLineEndings(" ")}'). Use the powershell interpreter for parameterised scripts.";
    }

    public override async Task<ActionResult> ExecuteAsync(ScriptRunInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);

        var scriptsFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Profile.ScriptsDirectory));
        var scriptPath = Path.GetFullPath(Path.Combine(scriptsFolder, inputs.Script));
        if (!scriptPath.StartsWith(scriptsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Failure("script-outside-folder", $"Refused: '{inputs.Script}' does not resolve inside {scriptsFolder}.");
        }

        if (!File.Exists(scriptPath))
        {
            return ActionResult.Failure("script-not-found", $"Script not found: {scriptPath}. Trusted scripts for the {context.Profile.DisplayName} profile live in {scriptsFolder}.");
        }

        var interpreterPath = InterpreterPath(inputs.Interpreter);
        if (!File.Exists(interpreterPath))
        {
            return ActionResult.Failure("interpreter-not-found", $"Interpreter not found: {interpreterPath}");
        }

        var expandedArguments = inputs.Arguments.Select(Environment.ExpandEnvironmentVariables).ToList();
        var arguments = new List<string>();
        if (inputs.Interpreter == ScriptInterpreter.PowerShell)
        {
            // Never -ExecutionPolicy: the policy is the organisation's decision, not this app's (rule 2).
            arguments.AddRange(["-NoProfile", "-NonInteractive", "-File", scriptPath]);
        }
        else
        {
            var refusal = CmdRefusal(inputs.Script, scriptsFolder, expandedArguments);
            if (refusal is not null)
            {
                return ActionResult.Failure("script-unsafe-for-cmd", refusal);
            }

            // cmd re-parses everything after /c. Starting it in the scripts folder and naming the script as .\<name>
            // keeps the folder path (which may hold spaces or & ^ %) off that line, and because the line after /c
            // never starts with a quote, cmd's quote stripping never touches the quoted arguments.
            arguments.AddRange(["/d", "/c", $@".\{inputs.Script}"]);
        }

        arguments.AddRange(expandedArguments);
        var request = new ProcessLaunchRequest(interpreterPath, arguments, scriptsFolder, CaptureOutput: true);

        ILaunchedProcess process;
        try
        {
            process = _launcher.Start(request);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ActionResult.Failure("launch-failed", $"Windows refused to start {Path.GetFileName(interpreterPath)}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return ActionResult.Failure("launch-failed", $"Could not start {Path.GetFileName(interpreterPath)}: {ex.Message}");
        }

        using (process)
        {
            ProcessExit exit;
            try
            {
                exit = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.KillTree();
                throw;
            }

            var output = string.IsNullOrEmpty(exit.StandardError)
                ? exit.StandardOutput
                : $"{exit.StandardOutput}{Environment.NewLine}[stderr]{Environment.NewLine}{exit.StandardError}";
            var name = Path.GetFileName(scriptPath);
            if (exit.ExitCode == 0)
            {
                return ActionResult.Success($"{name} finished (exit code 0)", output);
            }

            if (inputs.Interpreter == ScriptInterpreter.PowerShell && (LooksLikeExecutionPolicyBlock(exit.StandardError) || LooksLikeExecutionPolicyBlock(exit.StandardOutput)))
            {
                return ActionResult.Failure(
                    ExecutionPolicyBlockedCode,
                    $"PowerShell's execution policy on this PC does not allow {name} to run. The app never overrides that policy. Options: ask IT whether signed or local scripts can be allowed, or rewrite the script as a .cmd file.",
                    output);
            }

            return ActionResult.Failure("exit-code", $"{name} exited with code {exit.ExitCode.ToString(CultureInfo.InvariantCulture)}", output);
        }
    }

    /// <summary>Run-time re-check for cmd, after <c>%VAR%</c> expansion; <c>null</c> when the script may start.</summary>
    private static string? CmdRefusal(string script, string scriptsFolder, IReadOnlyList<string> expandedArguments)
    {
        if (script.Contains(' ', StringComparison.Ordinal))
        {
            return $"Refused: '{script}' contains a space, which the cmd interpreter cannot run safely; rename the file.";
        }

        if (scriptsFolder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // cmd cannot use a UNC folder as its current directory and would fall back to the Windows folder.
            return $"Refused: cmd scripts cannot run from a network folder ({scriptsFolder}). Use the powershell interpreter or a local data folder.";
        }

        return CmdArgumentProblem(expandedArguments);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$")]
    private static partial Regex ScriptNamePattern();

    // "running scripts is disabled on this system", "is not digitally signed", PSSecurityException, UnauthorizedAccess, about_Execution_Policies
    [GeneratedRegex(@"running scripts is disabled|not digitally signed|PSSecurityException|UnauthorizedAccess|about_Execution_Policies|execution policy", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionPolicyPattern();
}
