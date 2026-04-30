using AgentWorkspace.Core.Redaction;

namespace AgentWorkspace.Tests.Redaction;

public sealed class RegexRedactionEngineTests
{
    private readonly RegexRedactionEngine _engine = new();

    // ── Env-style tokens ──────────────────────────────────────────────────────

    [Fact]
    public void OpenAiKey_AssignmentRedacted()
        => Assert.Equal("OPENAI_API_KEY=[REDACTED]",
            _engine.Redact("OPENAI_API_KEY=sk-foobar123"));

    [Fact]
    public void AnthropicKey_AssignmentRedacted()
        => Assert.Contains("ANTHROPIC_API_KEY=[REDACTED]",
            _engine.Redact("ANTHROPIC_API_KEY=sk-ant-zzz999"));

    [Fact]
    public void GitHubToken_AssignmentRedacted()
        => Assert.Equal("GITHUB_TOKEN=[REDACTED]",
            _engine.Redact("GITHUB_TOKEN=ghp_abcdefghijk"));

    [Fact]
    public void AzureEnv_AssignmentRedacted()
        => Assert.Contains("AZURE_*=[REDACTED]",
            _engine.Redact("AZURE_CLIENT_SECRET=mycoolsecret"));

    [Fact]
    public void AwsSecret_AssignmentRedacted()
        => Assert.Contains("AWS_*=[REDACTED]",
            _engine.Redact("AWS_SECRET_ACCESS_KEY=abc/def+ghi"));

    [Fact]
    public void AwsAccessKey_AssignmentRedacted()
        => Assert.Contains("AWS_*=[REDACTED]",
            _engine.Redact("AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE"));

    // ── Token-shaped secrets ──────────────────────────────────────────────────

    [Fact]
    public void OpenAiKey_LiteralRedacted()
    {
        var result = _engine.Redact("Authorization: Bearer sk-1234567890abcdefghij");
        Assert.DoesNotContain("sk-1234567890abcdefghij", result);
    }

    [Fact]
    public void AnthropicKey_LiteralRedacted()
    {
        var result = _engine.Redact("key=sk-ant-api01-abcdefg_HIJKLMnopqrstuv");
        Assert.DoesNotContain("sk-ant-api01-abcdefg_HIJKLMnopqrstuv", result);
    }

    [Fact]
    public void GitHubPat_LiteralRedacted()
    {
        var result = _engine.Redact("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        Assert.Contains("[REDACTED-GITHUB-TOKEN]", result);
    }

    [Fact]
    public void AwsAkid_LiteralRedacted()
    {
        var result = _engine.Redact("AKIAIOSFODNN7EXAMPLE in logs");
        Assert.Contains("[REDACTED-AWS-AKID]", result);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result);
    }

    // ── Bearer / JWT ──────────────────────────────────────────────────────────

    [Fact]
    public void BearerToken_Redacted()
    {
        var result = _engine.Redact("Authorization: Bearer abcdef1234567890ghijkl");
        Assert.Contains("Bearer [REDACTED]", result);
    }

    [Fact]
    public void Jwt_ThreeSegmentRedacted()
    {
        var result = _engine.Redact("Token: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c");
        Assert.Contains("[REDACTED-JWT]", result);
    }

    // ── SSH private key ───────────────────────────────────────────────────────

    [Fact]
    public void RsaPrivateKey_BlockRedacted()
    {
        const string key = """
            -----BEGIN RSA PRIVATE KEY-----
            MIIEpAIBAAKCAQEA1234abcd
            ZZZZ
            -----END RSA PRIVATE KEY-----
            """;
        var result = _engine.Redact($"Private key follows:\n{key}\nDone.");
        Assert.Contains("[REDACTED-PRIVATE-KEY]", result);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEA1234abcd", result);
    }

    [Fact]
    public void OpenSshPrivateKey_BlockRedacted()
    {
        const string key = """
            -----BEGIN OPENSSH PRIVATE KEY-----
            b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAAB
            -----END OPENSSH PRIVATE KEY-----
            """;
        var result = _engine.Redact(key);
        Assert.Contains("[REDACTED-PRIVATE-KEY]", result);
    }

    // ── Absolute home paths ───────────────────────────────────────────────────

    [Fact]
    public void WindowsUserPath_Redacted()
    {
        var result = _engine.Redact(@"Working dir: C:\Users\jgkim\Desktop\proj");
        Assert.Contains(@"C:\Users\[USER]", result);
        Assert.DoesNotContain(@"jgkim", result);
    }

    [Fact]
    public void UnixHomePath_Redacted()
    {
        var result = _engine.Redact("Working dir: /home/alice/project");
        Assert.Contains("/home/[USER]", result);
        Assert.DoesNotContain("alice", result);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyString_ReturnedAsIs()
        => Assert.Equal("", _engine.Redact(""));

    [Fact]
    public void NullSafe_ReturnedAsIs()
        => Assert.Null(_engine.Redact(null!));

    [Fact]
    public void NoMatch_ReturnsOriginal()
    {
        const string text = "Just a normal log line with no secrets.";
        Assert.Equal(text, _engine.Redact(text));
    }

    [Fact]
    public void Deterministic_SameInput_SameOutput()
    {
        const string input = "GITHUB_TOKEN=ghp_zzz123\nuser /home/bob/x";
        var first  = _engine.Redact(input);
        var second = _engine.Redact(input);
        Assert.Equal(first, second);
    }

    [Fact]
    public void MultiplePatterns_AllRedacted()
    {
        const string input = "OPENAI_API_KEY=sk-foo\nGITHUB_TOKEN=ghp_bar\nC:\\Users\\alice\\x";
        var result = _engine.Redact(input);
        Assert.Contains("OPENAI_API_KEY=[REDACTED]",  result);
        Assert.Contains("GITHUB_TOKEN=[REDACTED]",    result);
        Assert.Contains(@"C:\Users\[USER]",           result);
    }

    [Fact]
    public void DefaultRules_Has_14_Entries()
        => Assert.Equal(14, RegexRedactionEngine.DefaultRules.Count);
}
