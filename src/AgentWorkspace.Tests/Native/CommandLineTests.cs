using System.Text;
using AgentWorkspace.ConPTY.Native;

namespace AgentWorkspace.Tests.Native;

/// <summary>
/// Round-trip tests for <c>CommandLine.Build</c> against <c>CommandLineToArgvW</c>'s rules.
/// </summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("simple", new[] { "a", "b" }, "simple a b")]
    [InlineData("with space", new string[0], "\"with space\"")]
    [InlineData("a", new[] { "with space" }, "a \"with space\"")]
    [InlineData("a", new[] { "" }, "a \"\"")]
    [InlineData("a", new[] { "has\"quote" }, "a \"has\\\"quote\"")]
    [InlineData("a", new[] { "trailing\\" }, "a trailing\\")]
    [InlineData("a", new[] { "trailing\\ in quoted" }, "a \"trailing\\ in quoted\"")]
    [InlineData("a", new[] { "back\\\\slash" }, "a back\\\\slash")]
    public void Build_QuotesArgumentsCorrectly(string command, string[] args, string expected)
    {
        Assert.Equal(expected, CommandLine.Build(command, args));
    }

    [Fact]
    public void BuildEnvironmentBlock_IsSortedAndDoubleNullTerminated()
    {
        var env = new Dictionary<string, string>
        {
            ["ZAPP"] = "1",
            ["ALPHA"] = "two",
            ["middle"] = "x",
        };

        string block = CommandLine.BuildEnvironmentBlock(env);

        // Expect sorted, case-insensitive, with each entry terminated by '\0' and trailing '\0'.
        string[] parts = block.Split('\0');
        // parts: ["ALPHA=two", "middle=x", "ZAPP=1", "", ""]
        Assert.Equal("ALPHA=two", parts[0]);
        Assert.Equal("middle=x", parts[1]);
        Assert.Equal("ZAPP=1", parts[2]);
        Assert.Equal(string.Empty, parts[3]);
    }
}
