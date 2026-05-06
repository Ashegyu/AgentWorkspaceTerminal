using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AgentWorkspace.Abstractions.Agents;

/// <summary>
/// Builds <see cref="ProcessStartInfo"/> instances for agent CLI adapters.
/// Handles Windows npm shims (<c>.cmd</c>, <c>.bat</c>, <c>.ps1</c>) explicitly because
/// <c>UseShellExecute=false</c> does not reliably invoke those through file association.
/// </summary>
public static class AgentCliProcessStartInfo
{
    public static ProcessStartInfo Create(
        string executable,
        IEnumerable<string> arguments,
        Encoding? outputEncoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var resolved = ResolveExecutablePath(executable) ?? executable;
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding  = outputEncoding,
        };

        AddExecutableAndArguments(psi, resolved, arguments);
        return psi;
    }

    public static string? ResolveExecutablePath(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        if (HasDirectoryPart(executable))
        {
            foreach (var candidate in CandidatePaths(executable))
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        foreach (var dir in SearchDirectories())
        {
            foreach (var candidate in CandidatePaths(Path.Combine(dir, executable)))
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static void AddExecutableAndArguments(
        ProcessStartInfo psi,
        string executable,
        IEnumerable<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(executable);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(executable);
                AddArguments(psi, arguments);
                return;
            }

            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(executable);
                AddArguments(psi, arguments);
                return;
            }
        }

        psi.FileName = executable;
        AddArguments(psi, arguments);
    }

    private static void AddArguments(ProcessStartInfo psi, IEnumerable<string> arguments)
    {
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
    }

    private static bool HasDirectoryPart(string executable) =>
        Path.IsPathFullyQualified(executable) ||
        executable.Contains(Path.DirectorySeparatorChar) ||
        executable.Contains(Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> CandidatePaths(string basePath)
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Path.GetExtension(basePath)))
        {
            yield return basePath;
            yield break;
        }

        yield return basePath + ".exe";
        yield return basePath + ".cmd";
        yield return basePath + ".bat";
        yield return basePath + ".ps1";
        yield return basePath;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(dir)) yield return dir;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var npmDir = Path.Combine(appData, "npm");
                if (seen.Add(npmDir)) yield return npmDir;
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var localBin = Path.Combine(userProfile, ".local", "bin");
                if (seen.Add(localBin)) yield return localBin;
            }
        }
    }
}
