// THROWAWAY SPIKE — NOT PRODUCTION, NOT FOR MERGE.
// Process creation under our own control, so the show-state is decided BEFORE the
// target's first user-mode instruction rather than reacted to afterwards.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtabExtension.Spike.ProcessControl;

/// <summary>
/// The one thing every mechanism tested so far could not do: act before an HWND exists.
///
/// <para><c>STARTUPINFOW.wShowWindow</c> with <c>STARTF_USESHOWWINDOW</c> is supplied at
/// process creation, and Windows uses it as the show-state the first time the new GUI
/// process calls <c>ShowWindow</c> with <c>SW_SHOWDEFAULT</c> for its first overlapped
/// window. No injection, no message sent into the target, no HWND required.</para>
///
/// <para><c>CREATE_SUSPENDED</c> is deliberate and is what gives this spike a temporal
/// boundary nothing else has had: the pid, the authoritative process handle and the
/// external observer are all established while the process is frozen, before it has
/// executed a single instruction of its own.</para>
///
/// <para><b>This class never manipulates a window.</b> No <c>ShowWindow</c>, no
/// <c>SetWindowPos</c>, no hook. Out-of-process <c>SW_HIDE</c> actuation is the actuator
/// that was proven to crash ETABS and is forbidden here by construction.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeProcessLaunch
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const ushort SwHide = 0;
    private const uint InfiniteFail = 0xFFFFFFFF;

    /// <summary>A created, still-suspended process we own outright.</summary>
    internal sealed record SuspendedProcess(
        int ProcessId,
        int ThreadId,
        nint ProcessHandle,
        nint ThreadHandle);

    /// <summary>
    /// Creates <paramref name="executablePath"/> suspended, with the initial window
    /// show-state set to hidden. Throws <see cref="Win32Exception"/> with the real last
    /// error if creation fails — a spike that guessed here would be worthless.
    /// </summary>
    internal static SuspendedProcess CreateSuspendedHidden(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var startupInfo = new StartupInfoW
        {
            cb = Marshal.SizeOf<StartupInfoW>(),
            dwFlags = StartfUseShowWindow,
            wShowWindow = SwHide
        };

        // lpApplicationName is the exact path, and lpCommandLine is null: no command-line
        // parsing, no PATH search, no "which ETABS did we actually start" ambiguity.
        var created = CreateProcessW(
            executablePath,
            null,
            nint.Zero,
            nint.Zero,
            false,
            CreateSuspended | CreateUnicodeEnvironment,
            nint.Zero,
            Path.GetDirectoryName(executablePath),
            ref startupInfo,
            out var info);

        if (!created)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"CreateProcessW failed for '{executablePath}'.");
        }

        return new SuspendedProcess(
            info.dwProcessId,
            info.dwThreadId,
            info.hProcess,
            info.hThread);
    }

    /// <summary>Releases the frozen primary thread. This is the instant ETABS begins to run.</summary>
    internal static void Resume(SuspendedProcess process)
    {
        var previous = ResumeThread(process.ThreadHandle);
        if (previous == InfiniteFail)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"ResumeThread failed for pid {process.ProcessId}.");
        }
    }

    /// <summary>
    /// Terminates the process we created, by the handle we have held since creation.
    /// Never by pid lookup: a pid is only unambiguous while a handle keeps it from being
    /// recycled, and this spike is the thing holding that handle.
    /// </summary>
    internal static void TerminateOwned(SuspendedProcess process)
    {
        _ = TerminateProcess(process.ProcessHandle, 1);
    }

    internal static void CloseHandles(SuspendedProcess process)
    {
        if (process.ThreadHandle != nint.Zero)
        {
            _ = CloseHandle(process.ThreadHandle);
        }

        if (process.ProcessHandle != nint.Zero)
        {
            _ = CloseHandle(process.ProcessHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string? lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
