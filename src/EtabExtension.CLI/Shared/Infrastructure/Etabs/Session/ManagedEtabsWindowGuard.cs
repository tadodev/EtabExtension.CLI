// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>One top-level Windows window, as observed from outside the process that owns it.</summary>
/// <param name="Handle">The raw <c>HWND</c>.</param>
/// <param name="ProcessId">The process that owns the window's thread.</param>
/// <param name="IsVisible">Whether Windows currently considers the window visible.</param>
public readonly record struct TopLevelWindow(nint Handle, int ProcessId, bool IsVisible);

/// <summary>
/// The Windows window-station boundary behind one named seam, so the guard's targeting
/// rule is exercisable without user32 and without ETABS.
///
/// <para>Enumeration deliberately returns EVERY top-level window with its owning process
/// id rather than pre-filtering: the "only the exact owned process is ever touched" rule
/// is the property under test, so it has to live in the guard where a test can drive
/// foreign windows past it.</para>
/// </summary>
public interface ITopLevelWindows
{
    IReadOnlyList<TopLevelWindow> Enumerate();

    void Hide(nint handle);

    void Show(nint handle);
}

/// <summary>
/// Suppression of the on-screen windows of ONE exact, proven-owned ETABS process, for
/// the interval in which CSI's own visibility state is not yet established.
///
/// <para><b>Why this exists.</b> The supervised #20 live certification measured a real
/// ETABS window on screen for 5.19 s during background session creation, before
/// <c>open-model</c> was ever called. <c>cOAPI.Hide()</c> returned success and
/// <c>cOAPI.Visible()</c> still reported visible, and the Windows HWND telemetry agreed
/// with <c>Visible()</c> — so the CSI oracle was accurate and the CSI actuator was
/// simply late. A convergence wait alone therefore cannot fix the defect: it only
/// measures the window it is waiting through. Something has to hold the window down
/// while CSI catches up.</para>
///
/// <para><b>Why it is safe.</b> It never searches for "an ETABS window". It is handed the
/// authoritative <see cref="IOwnedEtabsProcess"/> the launcher opened by exact identity
/// (pid + process start time + executable path) after a preflight census proved zero
/// pre-existing ETABS processes, and it hides only windows whose owning process id is
/// that pid. Holding that handle open is also what makes the pid safe to compare against:
/// Windows will not recycle a pid while a handle on the process is open. Once the process
/// has exited the guard stops permanently rather than risk a recycled pid.</para>
///
/// <para><b>Why it is a latch.</b> Suppression ends exactly once. An explicit
/// <c>open-model</c> ends it through <see cref="ReleaseForExplicitUserAction"/>, which
/// also restores the exact handles this guard hid; shutdown ends it through
/// <see cref="IDisposable.Dispose"/>, which restores nothing. There is no re-arm, so a
/// background command running after the user asked to see ETABS cannot take the window
/// away again.</para>
/// </summary>
public interface IManagedEtabsWindowGuard : IDisposable
{
    /// <summary>The exact process this guard is allowed to act on. Never a bare pid.</summary>
    ManagedProcessIdentity Identity { get; }

    /// <summary>False once the guard has been released or disposed. Never true again.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Ends suppression because the USER asked to see ETABS, and restores the windows this
    /// guard hid that are still, at that moment, its own.
    ///
    /// <para>The restore is not cosmetic. <c>cOAPI.Visible()</c> may be derived from the
    /// real window state, in which case this guard's suppression is itself what a later
    /// <c>Visible()</c> read reports — and then CSI's own <c>Unhide</c> can refuse as
    /// "already visible" (Cardex documents that error) and leave the engineer with
    /// nothing on screen. Putting the windows back first means the requested reveal
    /// happens whichever way CSI is implemented.</para>
    /// </summary>
    void ReleaseForExplicitUserAction();
}

/// <summary>
/// Creates a guard over an already-proven owned process. Takes the authoritative handle
/// rather than a pid so there is no signature through which an unproven or global ETABS
/// process could be guarded at all.
/// </summary>
public interface IManagedEtabsWindowGuardFactory
{
    IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess);
}

/// <inheritdoc cref="IManagedEtabsWindowGuard" />
public sealed class ManagedEtabsWindowGuard : IManagedEtabsWindowGuard
{
    /// <summary>
    /// How often the owned process's windows are re-checked. ETABS builds its UI on its
    /// own schedule — the #20 timeline shows the window arriving 5.15 s after process
    /// creation — so a single sweep would prove nothing. This is a poll interval inside a
    /// suppression loop, not a wait for a fixed duration: nothing on the startup path
    /// blocks on it.
    /// </summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Bounded join so teardown cannot hang on a stuck sweep.</summary>
    public static readonly TimeSpan PumpJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IOwnedEtabsProcess _owned;
    private readonly ITopLevelWindows _windows;
    private readonly TimeSpan _sweepInterval;
    private readonly ManualResetEventSlim _terminate = new(false);
    private readonly List<nint> _suppressed = [];
    private readonly object _gate = new();
    private readonly Thread? _pump;
    private bool _active = true;
    private bool _terminated;

    internal ManagedEtabsWindowGuard(
        IOwnedEtabsProcess owned,
        ITopLevelWindows windows,
        TimeSpan sweepInterval,
        bool startPump)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sweepInterval, TimeSpan.Zero);

        _owned = owned;
        _windows = windows;
        _sweepInterval = sweepInterval;
        if (startPump)
        {
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "etabs-window-guard"
            };
            _pump.Start();
        }
    }

    /// <inheritdoc />
    public ManagedProcessIdentity Identity => _owned.Identity;

    /// <inheritdoc />
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Sweeps completed, for tests and for reasoning about a live run.</summary>
    internal int Sweeps { get; private set; }

    /// <summary>Windows this guard has hidden, in the order it first hid them.</summary>
    internal IReadOnlyList<nint> Suppressed
    {
        get
        {
            lock (_gate)
            {
                return [.. _suppressed];
            }
        }
    }

    /// <summary>The last sweep failure, if any. A sweep is best-effort by design.</summary>
    internal Exception? LastSweepError { get; private set; }

    /// <summary>
    /// One suppression pass over the exact owned process's windows.
    ///
    /// <para>Every window that is not owned by <see cref="Identity"/> is skipped without
    /// being read, hidden, shown, or otherwise touched. That is the whole targeting rule,
    /// and it is stated once, here.</para>
    /// </summary>
    internal void SweepOnce()
    {
        lock (_gate)
        {
            if (!_active)
            {
                return;
            }

            // A pid is only provably ours while the authoritative handle keeps it from
            // being recycled. After exit it is somebody else's pid in waiting, so the
            // guard stops for good rather than acting on it.
            if (_owned.HasExited)
            {
                _active = false;
                return;
            }

            foreach (var window in _windows.Enumerate())
            {
                if (window.ProcessId != _owned.Identity.Pid || !window.IsVisible)
                {
                    continue;
                }

                _windows.Hide(window.Handle);
                if (!_suppressed.Contains(window.Handle))
                {
                    _suppressed.Add(window.Handle);
                }
            }

            Sweeps++;
        }
    }

    /// <inheritdoc />
    public void ReleaseForExplicitUserAction() => Terminate(restore: true);

    /// <summary>
    /// Deterministic teardown. Restores nothing: shutdown is not a reveal, and a process
    /// that is about to exit must not flash a window on its way out.
    /// </summary>
    public void Dispose() => Terminate(restore: false);

    private void Terminate(bool restore)
    {
        nint[] restoreTargets = [];
        lock (_gate)
        {
            if (_terminated)
            {
                // Already latched. A second call — reveal then shutdown, or two shutdown
                // paths — must never re-arm anything or restore twice.
                return;
            }

            _terminated = true;
            _active = false;
            if (restore)
            {
                restoreTargets = [.. _suppressed];
            }

            _suppressed.Clear();
        }

        _terminate.Set();
        _ = _pump?.Join(PumpJoinTimeout);
        RestoreStillOwned(restoreTargets);
        _terminate.Dispose();
    }

    /// <summary>
    /// Shows the saved handles that are STILL top-level windows of the exact owned process.
    ///
    /// <para><b>Why ownership is re-proven here.</b> Suppression filters by owning process
    /// id on every sweep, but what gets saved is a raw <c>HWND</c> value, and the open
    /// process handle does not protect it. A handle keeps Windows from recycling the
    /// <i>pid</i> while the process object lives; it says nothing about an <c>HWND</c>
    /// value, which Windows is free to hand to an entirely different window — in an
    /// entirely different process — the moment ETABS destroys the one we hid. Restoring
    /// from the saved list alone would then <c>ShowWindow</c> a stranger's window, which is
    /// a worse defect than the one this guard exists to fix.</para>
    ///
    /// <para>So the list is re-observed against a fresh census: a handle that has
    /// disappeared, or that now belongs to another process, is skipped rather than shown.
    /// An exited owned process skips the restore entirely — after exit its pid is no longer
    /// provably ours and there is nothing of ours left to show.</para>
    /// </summary>
    private void RestoreStillOwned(nint[] targets)
    {
        if (targets.Length == 0 || _owned.HasExited)
        {
            return;
        }

        HashSet<nint> ownedNow;
        try
        {
            ownedNow = [.. _windows.Enumerate()
                .Where(window => window.ProcessId == _owned.Identity.Pid)
                .Select(window => window.Handle)];
        }
        catch (Exception exception)
        {
            // Ownership could not be re-proven, so nothing is touched. The CSI Unhide that
            // follows is the authoritative transition and does not depend on this.
            LastSweepError = exception;
            return;
        }

        foreach (var handle in targets)
        {
            if (!ownedNow.Contains(handle))
            {
                continue;
            }

            try
            {
                _windows.Show(handle);
            }
            catch (Exception exception)
            {
                LastSweepError = exception;
            }
        }
    }

    private void Pump()
    {
        do
        {
            try
            {
                SweepOnce();
            }
            catch (Exception exception)
            {
                // Best-effort by construction: this is defence in depth over the CSI hide,
                // and a transient window-station failure must not take the daemon down.
                LastSweepError = exception;
            }
        }
        while (!_terminate.Wait(_sweepInterval));
    }
}

/// <summary>
/// The production factory. Windows-only by construction — the sidecar drives ETABS
/// through COM on Windows and nowhere else, and a silently inert guard would reintroduce
/// exactly the "reported success, showed a window" failure #20 rejected.
/// </summary>
public sealed class WindowsManagedEtabsWindowGuardFactory : IManagedEtabsWindowGuardFactory
{
    public static readonly WindowsManagedEtabsWindowGuardFactory Instance = new();

    /// <inheritdoc />
    public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess)
    {
        ArgumentNullException.ThrowIfNull(ownedProcess);
        return OperatingSystem.IsWindows()
            ? new ManagedEtabsWindowGuard(
                ownedProcess,
                new Win32TopLevelWindows(),
                ManagedEtabsWindowGuard.DefaultSweepInterval,
                startPump: true)
            : throw new PlatformNotSupportedException(
                "Managed ETABS window suppression requires Windows.");
    }
}

/// <summary>
/// The user32 boundary. Walks the desktop's top-level Z-order chain rather than taking an
/// enumeration callback, so no unmanaged function pointer and no <c>AllowUnsafeBlocks</c>
/// is needed — the same reasoning that keeps <c>WindowsFileIdentity</c> on classic
/// <c>DllImport</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32TopLevelWindows : ITopLevelWindows
{
    private const uint GwHwndNext = 2;
    private const int SwHide = 0;
    private const int SwShow = 5;

    /// <summary>A ceiling on the Z-order walk so a malformed chain cannot spin forever.</summary>
    private const int WindowScanLimit = 20_000;

    public IReadOnlyList<TopLevelWindow> Enumerate()
    {
        var windows = new List<TopLevelWindow>();
        var handle = GetTopWindow(nint.Zero);
        for (var scanned = 0; handle != nint.Zero && scanned < WindowScanLimit; scanned++)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new(handle, unchecked((int)processId), IsWindowVisible(handle)));
            handle = GetWindow(handle, GwHwndNext);
        }

        return windows;
    }

    public void Hide(nint handle) => _ = ShowWindow(handle, SwHide);

    public void Show(nint handle) => _ = ShowWindow(handle, SwShow);

    // Classic DllImport rather than the source-generated LibraryImport, for the reason
    // already recorded on WindowsFileIdentity: LibraryImport requires AllowUnsafeBlocks
    // project-wide, which is far too broad a change for a handful of P/Invokes.
    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetTopWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
