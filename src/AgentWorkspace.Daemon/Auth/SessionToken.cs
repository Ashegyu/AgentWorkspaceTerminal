using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace AgentWorkspace.Daemon.Auth;

/// <summary>
/// 16-byte (24 raw → 32 base64) bearer token written to %LOCALAPPDATA%\AgentWorkspace\session.token.
/// Per ADR-003 the file ACL is restricted so only the current user can read/write the token.
/// </summary>
public sealed class SessionToken : IEquatable<SessionToken>
{
    public const int RawByteLength = 24;
    public const string FileName = "session.token";

    private readonly byte[] _bytes;
    private readonly string _value;

    private SessionToken(byte[] bytes, string value)
    {
        _bytes = bytes;
        _value = value;
    }

    public string Value => _value;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static SessionToken Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawByteLength);
        var value = Convert.ToBase64String(bytes);
        return new SessionToken(bytes, value);
    }

    public static SessionToken FromValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Token is not valid base64.", nameof(value), ex);
        }

        if (bytes.Length != RawByteLength)
        {
            throw new ArgumentException(
                $"Token must decode to {RawByteLength} bytes (got {bytes.Length}).",
                nameof(value));
        }

        return new SessionToken(bytes, value);
    }

    public bool Equals(SessionToken? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);

    public override bool Equals(object? obj) => obj is SessionToken t && Equals(t);

    public override int GetHashCode()
    {
        Span<byte> span = stackalloc byte[sizeof(int)];
        _bytes.AsSpan(0, sizeof(int)).CopyTo(span);
        return BitConverter.ToInt32(span);
    }

    public override string ToString() => "[redacted-session-token]";
}

public static class SessionTokenStore
{
    /// <summary>Default location: %LOCALAPPDATA%\AgentWorkspace\session.token.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentWorkspace",
        SessionToken.FileName);

    /// <summary>
    /// Persists <paramref name="token"/> to <paramref name="path"/> with an ACL granting
    /// FullControl to the current user only (inheritance disabled, all inherited ACEs removed).
    /// </summary>
    public static void Save(SessionToken token, string path)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Path has no directory component.", nameof(path));
        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // Create with FileShare.None and write bytes synchronously.
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(token.Value);
            fs.Write(bytes, 0, bytes.Length);
        }

        ApplyOwnerOnlyAcl(path);
    }

    /// <summary>Reads and validates the token at <paramref name="path"/>.</summary>
    public static SessionToken Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var raw = File.ReadAllText(path).Trim();
        return SessionToken.FromValue(raw);
    }

    /// <summary>
    /// Hardens <paramref name="path"/> so only the current user has any access. Inheritance is
    /// disabled and all inherited ACEs are stripped.
    /// </summary>
    internal static void ApplyOwnerOnlyAcl(string path)
    {
        var fileInfo = new FileInfo(path);
        var acl = fileInfo.GetAccessControl();

        // Disable inheritance and remove all inherited ACEs.
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Strip every existing rule.
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            acl.RemoveAccessRuleAll(rule);
        }

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve the current Windows user SID.");

        acl.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        acl.SetOwner(currentUser);

        fileInfo.SetAccessControl(acl);
    }
}
