using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentWorkspace.ConPTY.Native;

/// <summary>
/// Win32 Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, ensuring that the
/// entire descendant process tree is terminated when the job handle is closed.
/// </summary>
/// <remarks>
/// <para>
/// We do not set <c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> because pwsh / cmd / wsl already handle
/// child grouping correctly under modern Windows; allowing breakaway would let stray processes
/// outlive the pane.
/// </para>
/// <para>
/// Disposal closes the handle, which the kernel converts into termination of remaining members
/// once all open references drop. We pin the lifetime via <see cref="SafeFileHandle"/> so that
/// the GC cannot release the handle before <c>CreateProcessW</c> consumes it via the
/// proc-thread attribute list.
/// </para>
/// </remarks>
internal sealed class JobObject : IDisposable
{
    public SafeFileHandle Handle { get; }

    public JobObject()
    {
        Handle = NativeMethods.CreateJobObjectW(0, null);
        if (Handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            Handle.Dispose();
            throw new Win32Exception(err, "CreateJobObjectW failed.");
        }

        var info = default(JobObjectExtendedLimitInformation);
        info.BasicLimitInformation.LimitFlags = JobObjectLimitFlags.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!NativeMethods.SetInformationJobObject(
                Handle,
                JobObjectInfoClass.JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            int err = Marshal.GetLastWin32Error();
            Handle.Dispose();
            throw new Win32Exception(err, "SetInformationJobObject failed.");
        }
    }

    public void Terminate(uint exitCode = 1)
    {
        if (Handle.IsInvalid || Handle.IsClosed)
        {
            return;
        }
        // Best effort; if the job has already been torn down by KILL_ON_JOB_CLOSE the call simply
        // fails with ERROR_INVALID_HANDLE, which is fine.
        _ = NativeMethods.TerminateJobObject(Handle, exitCode);
    }

    public void Dispose()
    {
        // Closing the handle triggers KILL_ON_JOB_CLOSE for any remaining members.
        Handle.Dispose();
    }
}
