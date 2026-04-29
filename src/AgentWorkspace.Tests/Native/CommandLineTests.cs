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

    [Theory]
    [InlineData("한글 인자")]                // BMP CJK + space
    [InlineData("中文")]                     // BMP CJK no space
    [InlineData("日本語 テスト")]            // BMP CJK mixed
    [InlineData("🎉")]                       // surrogate pair (4-byte UTF-8)
    [InlineData("hello 🎉 world")]           // mixed ASCII + emoji + spaces
    [InlineData("path with \"quote\" inside")] // quote escaping with non-ASCII context
    public void Build_PreservesUnicodeArguments(string arg)
    {
        // Round-trip via the OS: pass the built command line through CommandLineToArgvW and
        // confirm the recovered argv[1] matches the original argument.
        string built = CommandLine.Build("dummy.exe", new[] { arg });
        string[] parsed = ParseCommandLineViaWin32(built);

        Assert.Equal(2, parsed.Length);
        Assert.Equal("dummy.exe", parsed[0]);
        Assert.Equal(arg, parsed[1]);
    }

    [Fact]
    public void Build_PreservesMultipleUnicodeArguments()
    {
        string[] originals = { "한글", "🎉 emoji", "中文", "with space", "quote\"inside" };
        string built = CommandLine.Build("cmd.exe", originals);
        string[] parsed = ParseCommandLineViaWin32(built);

        Assert.Equal(originals.Length + 1, parsed.Length);
        Assert.Equal("cmd.exe", parsed[0]);
        for (int i = 0; i < originals.Length; i++)
        {
            Assert.Equal(originals[i], parsed[i + 1]);
        }
    }

    /// <summary>
    /// Calls Win32 <c>CommandLineToArgvW</c> on the produced command line — this is the canonical
    /// Windows parser that <c>main()</c> startup uses, so a successful round-trip means a real
    /// child process would see the same argument list.
    /// </summary>
    private static string[] ParseCommandLineViaWin32(string commandLine)
    {
        nint argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                nint p = System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * nint.Size);
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(p) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);

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
