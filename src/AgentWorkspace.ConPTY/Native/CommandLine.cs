using System.Collections.Generic;
using System.Text;

namespace AgentWorkspace.ConPTY.Native;

/// <summary>
/// Command-line composition that follows the rules
/// <see href="https://learn.microsoft.com/cpp/cpp/main-function-command-line-args">documented</see>
/// for <c>CommandLineToArgvW</c>. This is the canonical Win32 quoting algorithm, used by msvcrt
/// startup and <c>System.Diagnostics.Process</c> in .NET.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Builds a Win32 command line from an executable name and an argument list.
    /// </summary>
    public static string Build(string command, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, command);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends a single argument with the minimum quoting and escaping needed for
    /// <c>CommandLineToArgvW</c> to round-trip the value back to <paramref name="argument"/>.
    /// </summary>
    public static void AppendArgument(StringBuilder sb, string argument)
    {
        // Empty arguments must be quoted, otherwise they vanish.
        if (argument.Length == 0)
        {
            sb.Append("\"\"");
            return;
        }

        bool needsQuoting = false;
        foreach (char c in argument)
        {
            if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Escape every preceding backslash plus this quote.
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(c);
        }
        // Trailing backslashes must double up because they precede the closing quote.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    /// <summary>
    /// Builds the Unicode environment block expected by CreateProcessW. The block is a
    /// double-null-terminated, sorted list of "KEY=VALUE\0" entries.
    /// </summary>
    public static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        // Windows requires the block to be sorted case-insensitively by key.
        var keys = new List<string>(environment.Keys);
        keys.Sort(System.StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            sb.Append(key).Append('=').Append(environment[key]).Append('\0');
        }
        sb.Append('\0');
        return sb.ToString();
    }
}
