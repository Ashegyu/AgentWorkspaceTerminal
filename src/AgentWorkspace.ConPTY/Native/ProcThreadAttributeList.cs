using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentWorkspace.ConPTY.Native;

/// <summary>
/// Owns an unmanaged proc-thread attribute list buffer used to attach a pseudo-console (and a Job
/// Object list) to a child process at <c>CreateProcessW</c> time.
/// </summary>
/// <remarks>
/// The attribute list captures pointers to caller-owned memory (the HPCON, the job handle array).
/// Callers must keep that memory alive at least until <c>CreateProcessW</c> returns; this class
/// pins the supplied job-handle array for as long as the list is alive.
/// </remarks>
internal sealed class ProcThreadAttributeList : IDisposable
{
    private nint _buffer;
    private GCHandle _jobHandlesPin;
    private nint[]? _jobHandlesArray;
    private bool _disposed;

    public nint Pointer => _buffer;

    public static ProcThreadAttributeList Create(nint pseudoConsole, nint[]? jobHandles)
    {
        // Caller must always pass HPCON; job list is optional.
        ArgumentOutOfRangeException.ThrowIfEqual(pseudoConsole, 0);

        int attrCount = 1 + (jobHandles is { Length: > 0 } ? 1 : 0);
        nuint size = 0;

        // First call computes the required buffer size; it is *expected* to fail with
        // ERROR_INSUFFICIENT_BUFFER (122). We rely on the out 'size' parameter regardless.
        NativeMethods.InitializeProcThreadAttributeList(0, attrCount, 0, ref size);
        if (size == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "InitializeProcThreadAttributeList sizing call returned 0.");
        }

        nint buffer = Marshal.AllocHGlobal((nint)size);
        var list = new ProcThreadAttributeList { _buffer = buffer };

        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(buffer, attrCount, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    buffer,
                    dwFlags: 0,
                    Attribute: NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    lpValue: pseudoConsole,
                    cbSize: (nuint)nint.Size,
                    lpPreviousValue: 0,
                    lpReturnSize: 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "UpdateProcThreadAttribute(PSEUDOCONSOLE) failed.");
            }

            if (jobHandles is { Length: > 0 })
            {
                // Pin the job handle array; CreateProcessW reads it asynchronously while the list
                // is in scope, so the GC must not move it.
                list._jobHandlesArray = jobHandles;
                list._jobHandlesPin = GCHandle.Alloc(jobHandles, GCHandleType.Pinned);

                if (!NativeMethods.UpdateProcThreadAttribute(
                        buffer,
                        dwFlags: 0,
                        Attribute: NativeMethods.PROC_THREAD_ATTRIBUTE_JOB_LIST,
                        lpValue: list._jobHandlesPin.AddrOfPinnedObject(),
                        cbSize: (nuint)(nint.Size * jobHandles.Length),
                        lpPreviousValue: 0,
                        lpReturnSize: 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "UpdateProcThreadAttribute(JOB_LIST) failed.");
                }
            }

            return list;
        }
        catch
        {
            list.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_buffer != 0)
        {
            NativeMethods.DeleteProcThreadAttributeList(_buffer);
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }

        if (_jobHandlesPin.IsAllocated)
        {
            _jobHandlesPin.Free();
        }

        _jobHandlesArray = null;
    }
}
