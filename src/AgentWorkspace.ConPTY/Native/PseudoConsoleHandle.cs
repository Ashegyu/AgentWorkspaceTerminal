using System;
using Microsoft.Win32.SafeHandles;

namespace AgentWorkspace.ConPTY.Native;

/// <summary>
/// Owns the lifetime of an HPCON returned by <c>CreatePseudoConsole</c>.
/// Disposal calls <c>ClosePseudoConsole</c>, which the OS guarantees will not return until the
/// pseudo-console is fully torn down.
/// </summary>
internal sealed class PseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PseudoConsoleHandle()
        : base(ownsHandle: true)
    {
    }

    public PseudoConsoleHandle(nint existing)
        : base(ownsHandle: true)
    {
        SetHandle(existing);
    }

    protected override bool ReleaseHandle()
    {
        if (handle != 0)
        {
            NativeMethods.ClosePseudoConsole(handle);
        }
        return true;
    }
}
