using OG_opossum_command.Core.Actions.Launchers;
using OG_opossum_command.Core.Workflows;

namespace OG_opossum_command.Core.Actions.Windows;

/// <summary>Inputs of <see cref="UrlOpenAction"/>: one absolute http or https URL.</summary>
public sealed record UrlOpenInputs(Uri Url);

/// <summary><c>windows.url.open</c>: hand an http/https URL to the default browser. Other schemes are rejected at load time and again at run time.</summary>
public sealed class UrlOpenAction : WorkflowAction<UrlOpenInputs>
{
    public const string ActionId = "windows.url.open";

    private readonly IUrlOpener _opener;

    public UrlOpenAction(IUrlOpener opener)
    {
        ArgumentNullException.ThrowIfNull(opener);
        _opener = opener;
    }

    public override string Id => ActionId;

    public override string DisplayName => "Open web page";

    public override IdempotencyClass Idempotency => IdempotencyClass.Idempotent;

    public override TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);

    public override UrlOpenInputs? Parse(JsonObjectReader inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.RejectUnknownProperties("url");

        var text = inputs.RequiredString("url");
        if (text is null)
        {
            return null;
        }

        if (!TryParseHttpUrl(text, out var url))
        {
            inputs.AddError("url", $"'url' must be an absolute http:// or https:// address (got '{text}').");
            return null;
        }

        return new UrlOpenInputs(url);
    }

    public override Task<ActionResult> ExecuteAsync(UrlOpenInputs inputs, ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseHttpUrl(inputs.Url.OriginalString, out var url))
        {
            return Task.FromResult(ActionResult.Failure("url-rejected", $"Refused to open non-http URL: {inputs.Url.OriginalString}"));
        }

        _opener.Open(url);
        return Task.FromResult(ActionResult.Success($"Opened {url.Host}"));
    }

    /// <summary>True only for absolute http/https URLs with a host.</summary>
    public static bool TryParseHttpUrl(string text, out Uri url)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(parsed.Host))
        {
            url = parsed;
            return true;
        }

        url = null!;
        return false;
    }
}
