using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentWorkspace.Daemon.Auth;

namespace AgentWorkspace.Tests.Daemon;

public sealed class SessionTokenTests
{
    [Fact]
    public void Generate_ProducesUniqueRandomTokens()
    {
        var t1 = SessionToken.Generate();
        var t2 = SessionToken.Generate();
        Assert.NotEqual(t1.Value, t2.Value);
        Assert.False(t1.Equals(t2));
    }

    [Fact]
    public void GeneratedToken_HasExpectedRawAndBase64Length()
    {
        var token = SessionToken.Generate();

        Assert.Equal(SessionToken.RawByteLength, token.Bytes.Length);
        // base64(24 bytes) = 32 chars, no padding required.
        Assert.Equal(32, token.Value.Length);
        Assert.DoesNotContain('=', token.Value);
    }

    [Fact]
    public void FromValue_RejectsBadBase64()
    {
        Assert.Throws<ArgumentException>(() => SessionToken.FromValue("not-base64-***"));
    }

    [Fact]
    public void FromValue_RejectsWrongLength()
    {
        // 16 bytes encoded → 24 chars (after pad strip), wrong length
        var shortValue = Convert.ToBase64String(new byte[16]);
        Assert.Throws<ArgumentException>(() => SessionToken.FromValue(shortValue));
    }

    [Fact]
    public void Equals_IsConstantTime_AndMatchesValue()
    {
        var t1 = SessionToken.Generate();
        var t2 = SessionToken.FromValue(t1.Value);
        Assert.True(t1.Equals(t2));
        Assert.Equal(t1.GetHashCode(), t2.GetHashCode());
    }

    [Fact]
    public void ToString_DoesNotLeakValue()
    {
        var token = SessionToken.Generate();
        var s = token.ToString();
        Assert.DoesNotContain(token.Value, s, StringComparison.Ordinal);
        Assert.Contains("redacted", s, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SessionTokenStoreTests : IDisposable
{
    private readonly string _root;

    public SessionTokenStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "awt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Cleanup best-effort.
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundtripsToken()
    {
        var token = SessionToken.Generate();
        var path = Path.Combine(_root, "session.token");

        SessionTokenStore.Save(token, path);
        var loaded = SessionTokenStore.Load(path);

        Assert.True(token.Equals(loaded));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = Path.Combine(_root, "session.token");
        var first = SessionToken.Generate();
        SessionTokenStore.Save(first, path);

        var second = SessionToken.Generate();
        SessionTokenStore.Save(second, path);

        var loaded = SessionTokenStore.Load(path);
        Assert.True(loaded.Equals(second));
        Assert.False(loaded.Equals(first));
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_root, "nested", "deep", "session.token");
        var token = SessionToken.Generate();

        SessionTokenStore.Save(token, nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_AppliesOwnerOnlyAcl()
    {
        var path = Path.Combine(_root, "session.token");
        var token = SessionToken.Generate();
        SessionTokenStore.Save(token, path);

        var info = new FileInfo(path);
        var acl = info.GetAccessControl();

        Assert.True(acl.AreAccessRulesProtected, "ACL should be protected (no inheritance).");

        var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var currentUser = WindowsIdentity.GetCurrent().User!;

        var hadOwnerRule = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            // Every remaining rule should map to the current user.
            Assert.Equal(currentUser, rule.IdentityReference);
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                hadOwnerRule = true;
            }
        }
        Assert.True(hadOwnerRule, "Current user must have FullControl Allow rule.");
    }
}
