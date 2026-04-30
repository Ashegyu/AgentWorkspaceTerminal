using System.Text.Json;
using AgentWorkspace.Abstractions.Agents;
using AgentWorkspace.Abstractions.Policy;
using AgentWorkspace.Core.Policy;

namespace AgentWorkspace.Tests.Policy;

public sealed class ActionRequestPolicyMapperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ActionRequestEvent Evt(string type, string? json = null)
        => new("action-1", type, type, json is null ? null : (JsonElement?)Parse(json));

    [Fact]
    public void Bash_WithCommand_MapsToExecuteCommand()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Bash", """{"command":"rm -rf /"}"""));

        var cmd = Assert.IsType<ExecuteCommand>(result);
        Assert.Equal("rm -rf /", cmd.Cmd);
    }

    [Fact]
    public void Bash_WithoutInput_ReturnsNull()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(Evt("Bash"));
        Assert.Null(result);
    }

    [Fact]
    public void Bash_LowercaseShell_AlsoMaps()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("shell", """{"command":"ls"}"""));
        Assert.IsType<ExecuteCommand>(result);
    }

    [Fact]
    public void Read_MapsToReadFile()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Read", """{"file_path":"C:\\proj\\foo.cs"}"""));

        var rf = Assert.IsType<ReadFile>(result);
        Assert.Equal(@"C:\proj\foo.cs", rf.Path);
    }

    [Fact]
    public void Write_MapsToWriteFile_WithContentLength()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Write", """{"file_path":"C:\\proj\\foo.cs","content":"hello"}"""));

        var wf = Assert.IsType<WriteFile>(result);
        Assert.Equal(@"C:\proj\foo.cs", wf.Path);
        Assert.Equal(5, wf.ContentLength);
    }

    [Fact]
    public void Edit_MapsToWriteFile_UsingNewString()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Edit", """{"file_path":"C:\\proj\\foo.cs","old_string":"foo","new_string":"barbaz"}"""));

        var wf = Assert.IsType<WriteFile>(result);
        Assert.Equal(6, wf.ContentLength);
    }

    [Fact]
    public void MultiEdit_AlsoMapsToWriteFile()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("MultiEdit", """{"file_path":"C:\\proj\\foo.cs","new_string":"x"}"""));
        Assert.IsType<WriteFile>(result);
    }

    [Fact]
    public void WebFetch_MapsToNetworkCall_GET()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("WebFetch", """{"url":"https://example.com/x"}"""));

        var nc = Assert.IsType<NetworkCall>(result);
        Assert.Equal("https://example.com/x", nc.Url.ToString());
        Assert.Equal("GET", nc.Method);
    }

    [Fact]
    public void WebFetch_WithBadUrl_ReturnsNull()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("WebFetch", """{"url":"not-a-url"}"""));
        Assert.Null(result);
    }

    [Fact]
    public void UnknownTool_ReturnsNull()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("WeirdTool", """{"foo":"bar"}"""));
        Assert.Null(result);
    }

    [Fact]
    public void Read_AcceptsAlternatePathProperty()
    {
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Read", """{"path":"alt.txt"}"""));
        var rf = Assert.IsType<ReadFile>(result);
        Assert.Equal("alt.txt", rf.Path);
    }

    // ── Polish 4: nullable-input helpers (RequirePath / TryGetString) ────────

    [Fact]
    public void TryGetString_NullInput_ReturnsNull()
        => Assert.Null(ActionRequestPolicyMapper.TryGetString(null, "command"));

    [Fact]
    public void TryGetString_NonObjectInput_ReturnsNull()
    {
        var arr = Parse("""[1,2,3]""");
        Assert.Null(ActionRequestPolicyMapper.TryGetString(arr, "command"));
    }

    [Fact]
    public void TryGetString_MissingProperty_ReturnsNull()
    {
        var obj = Parse("""{"foo":"bar"}""");
        Assert.Null(ActionRequestPolicyMapper.TryGetString(obj, "command"));
    }

    [Fact]
    public void TryGetString_NonStringValue_ReturnsNull()
    {
        var obj = Parse("""{"command":42}""");
        Assert.Null(ActionRequestPolicyMapper.TryGetString(obj, "command"));
    }

    [Fact]
    public void TryGetString_StringValue_ReturnsIt()
    {
        var obj = Parse("""{"command":"echo hi"}""");
        Assert.Equal("echo hi", ActionRequestPolicyMapper.TryGetString(obj, "command"));
    }

    [Fact]
    public void RequirePath_PrefersFilePathOverPath()
    {
        var obj = Parse("""{"file_path":"a.txt","path":"b.txt"}""");
        Assert.Equal("a.txt", ActionRequestPolicyMapper.RequirePath(obj));
    }

    [Fact]
    public void RequirePath_FallsBackToPath()
    {
        var obj = Parse("""{"path":"only.txt"}""");
        Assert.Equal("only.txt", ActionRequestPolicyMapper.RequirePath(obj));
    }

    [Fact]
    public void RequirePath_NullInput_ReturnsNull()
        => Assert.Null(ActionRequestPolicyMapper.RequirePath(null));

    [Fact]
    public void RequirePath_NeitherProperty_ReturnsNull()
    {
        var obj = Parse("""{"unrelated":"x"}""");
        Assert.Null(ActionRequestPolicyMapper.RequirePath(obj));
    }

    [Fact]
    public void Write_MissingFilePath_ReturnsNull()
    {
        // Regression guard: future mappers using RequirePath must still bail on missing path.
        var result = ActionRequestPolicyMapper.ToProposedAction(
            Evt("Write", """{"content":"hi"}"""));
        Assert.Null(result);
    }
}
