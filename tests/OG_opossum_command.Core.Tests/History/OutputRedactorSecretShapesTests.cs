using OG_opossum_command.Core.History;

namespace OG_opossum_command.Core.Tests.History;

/// <summary>The secret shapes that script and environment output commonly carry (SEC-2), and harmless text that must survive.</summary>
public sealed class OutputRedactorSecretShapesTests
{
    [Theory]
    [InlineData("DB_PASSWORD=hunter2", "hunter2", "DB_")]
    [InlineData("GITHUB_TOKEN=abc123def456", "abc123def456", "GITHUB_")]
    [InlineData("AZURE_CLIENT_SECRET=Zx9~verysecret", "Zx9~verysecret", "AZURE_")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA", "Authorization")]
    [InlineData("Authorization: Negotiate YIIGhgYGKwYBBQUCoIIGejCCBnag", "YIIGhgYGKwYBBQUCoIIGejCCBnag", "Authorization")]
    [InlineData("\"password\": \"correct horse battery\", \"user\": \"dan\"", "horse battery", "\"user\": \"dan\"")]
    [InlineData("key id AKIAIOSFODNN7EXAMPLE in use", "AKIAIOSFODNN7EXAMPLE", "in use")]
    [InlineData("pushed with ghp_16C7e42F292c6912E7710c838347Ae178B4a done", "ghp_16C7e42F292c6912E7710c838347Ae178B4a", "done")]
    [InlineData("using github_pat_11ABCDEFG0123456789_abcdefghijklmnop now", "github_pat_11ABCDEFG0123456789", "now")]
    [InlineData("OPENAI key sk-proj-abcdefghijklmnopqrstuvwxyz012345 set", "abcdefghijklmnopqrstuvwxyz012345", "set")]
    [InlineData("slack xoxb-123456789012-abcdefghijkl posted", "xoxb-123456789012-abcdefghijkl", "posted")]
    [InlineData("remote: https://dan:s3cretPass@git.example.com/repo.git", "s3cretPass", "@git.example.com/repo.git")]
    [InlineData("tool --token abc123xyz --verbose", "abc123xyz", "--verbose")]
    [InlineData("tool --password=Pa55word! run", "Pa55word!", "run")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=AbCdEf0123456789AbCdEf0123456789AbCdEf01234=;EntityPath=q", "AbCdEf0123456789", "EntityPath=q")]
    public void Scrubs_env_dumps_auth_schemes_prefixed_tokens_userinfo_and_flags(string input, string secret, string keptFragment)
    {
        var redacted = OutputRedactor.Redact(input)!;

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains(keptFragment, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token count: 5 words")]
    [InlineData("tokens=5")]
    [InlineData("Basic setup finished")]
    [InlineData("Opened https://outlook.office.com/calendar/view/day")]
    [InlineData("Listening on http://localhost:8080/api@v2")]
    [InlineData("Saved C:\\Users\\dan\\task-list.txt")]
    [InlineData("Negotiate the agenda with the team")]
    public void Leaves_harmless_text_that_merely_resembles_a_secret_alone(string input)
    {
        Assert.Equal(input, OutputRedactor.Redact(input));
    }
}
