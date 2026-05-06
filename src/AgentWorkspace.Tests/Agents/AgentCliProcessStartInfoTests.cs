using System.Text;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Tests.Agents;

public sealed class AgentCliProcessStartInfoTests : IDisposable
{
    private readonly string _tempDir;

    public AgentCliProcessStartInfoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveExecutablePath_WindowsCmdShim_ReturnsCmdPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Path.Combine(_tempDir, "fake-agent.cmd");
        File.WriteAllText(shim, "@echo off\r\n");

        var resolved = AgentCliProcessStartInfo.ResolveExecutablePath(
            Path.Combine(_tempDir, "fake-agent"));

        Assert.Equal(shim, resolved);
    }

    [Fact]
    public void Create_WindowsCmdShim_WrapsWithCmdExe()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Path.Combine(_tempDir, "fake-agent.cmd");
        File.WriteAllText(shim, "@echo off\r\n");

        var psi = AgentCliProcessStartInfo.Create(
            shim,
            new[] { "exec", "hello world" },
            Encoding.UTF8);

        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "/d", "/c", shim, "exec", "hello world" },
            psi.ArgumentList.ToArray());
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.True(psi.RedirectStandardInput);
    }
}
