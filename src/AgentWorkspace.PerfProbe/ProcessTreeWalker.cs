using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// Win32 toolhelp32 BFS over the process tree rooted at a given PID. Used by
/// <c>rss-full</c> to enumerate App.Wpf + every msedgewebview2.exe child it
/// spawned, so the full-stack ADR-008 #3 budget (WPF + WebView2 + xterm.js
/// renderers) can be summed instead of just the daemon-floor.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessTreeWalker
{
    /// <summary>
    /// One process node in the walked tree. <see cref="WorkingSetBytes"/> is the
    /// snapshot taken at enumeration time; the caller decides whether to re-sample.
    /// </summary>
    public readonly record struct ProcessNode(
        int    Pid,
        int    ParentPid,
        string Name,
        long   WorkingSetBytes);

    /// <summary>
    /// Walk the descendant tree of <paramref name="rootPid"/> (inclusive). Returns
    /// the root + every transitive child currently alive. Order is BFS by depth
    /// so renderer/host PIDs follow their parent in the resulting list.
    /// Processes that disappear mid-walk are silently skipped.
    /// </summary>
    public static IReadOnlyList<ProcessNode> Walk(int rootPid)
    {
        var allByParent = SnapshotByParent();
        var result      = new List<ProcessNode>();
        var queue       = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (TryReadProcess(pid, out var node))
            {
                result.Add(node);
            }
            if (allByParent.TryGetValue(pid, out var children))
            {
                foreach (var childPid in children)
                {
                    queue.Enqueue(childPid);
                }
            }
        }

        return result;
    }

    private static Dictionary<int, List<int>> SnapshotByParent()
    {
        var map = new Dictionary<int, List<int>>();

        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return map;
        }

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                if (!map.TryGetValue((int)entry.th32ParentProcessID, out var siblings))
                {
                    siblings = new List<int>();
                    map[(int)entry.th32ParentProcessID] = siblings;
                }
                siblings.Add((int)entry.th32ProcessID);
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return map;
    }

    private static bool TryReadProcess(int pid, out ProcessNode node)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // WorkingSet64 is documented to throw InvalidOperationException once a
            // process exits; we handle it via the catch.
            node = new ProcessNode(
                Pid:              pid,
                ParentPid:        ParentPidViaSnapshot(pid),
                Name:             p.ProcessName,
                WorkingSetBytes:  p.WorkingSet64);
            return true;
        }
        catch (ArgumentException) { /* PID gone */ }
        catch (InvalidOperationException) { /* exited mid-read */ }
        catch (System.ComponentModel.Win32Exception) { /* access denied — rare for own children */ }

        node = default;
        return false;
    }

    /// <summary>Single-PID parent lookup for the <see cref="ProcessNode.ParentPid"/> field.
    /// Walking the snapshot once per node is wasteful but keeps the API simple; the tree
    /// rss-full cares about is single-digit nodes (App.Wpf + WebView2 host + N renderers).</summary>
    private static int ParentPidViaSnapshot(int pid)
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE) return 0;
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>(),
            };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry)) return 0;
            do
            {
                if ((int)entry.th32ProcessID == pid)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }
            while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return 0;
    }

    private static class NativeMethods
    {
        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public static readonly nint INVALID_HANDLE_VALUE = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public nint th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int  pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
