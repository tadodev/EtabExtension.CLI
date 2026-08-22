// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class ManagedEtabsWindowErrorCodes
{
    /// <summary>
    /// The exact-owned window census still reported a visible top-level window after the
    /// bounded suppression deadline. This is the authoritative background-readiness
    /// failure — <c>cOAPI.Visible()</c> is not.
    /// </summary>
    public const string SuppressionNotConfirmed = "ETABS_WINDOW_SUPPRESSION_NOT_CONFIRMED";

    /// <summary>
    /// The user explicitly asked to see ETABS and no owned top-level window became visible
    /// within the bounded deadline. "Open in ETABS" that leaves nothing on screen has not
    /// done what was asked, whatever CSI reports.
    /// </summary>
    public const string RevealNotConfirmed = "ETABS_WINDOW_REVEAL_NOT_CONFIRMED";

    /// <summary>The owned process exited, so its windows can no longer be reasoned about.</summary>
    public const string OwnedProcessGone = "ETABS_WINDOW_OWNED_PROCESS_GONE";
}

/// <summary>One top-level Windows window, as observed from outside the process that owns it.</summary>
/// <param name="Handle">The raw <c>HWND</c>.</param>
/// <param name="ProcessId">The process that owns the window's thread.</param>
/// <param name="IsVisible">Whether Windows currently considers the window visible.</param>
public readonly record struct TopLevelWindow(nint Handle, int ProcessId, bool IsVisible);

/// <summary>
/// What the exact-owned window census established, and what it rests on.
/// </summary>
/// <param name="Confirmed">Whether the requested Windows state was actually observed.</param>
/// <param name="Observations">How many censuses the answer rests on.</param>
/// <param name="Waited">Monotonic time spent reaching it, or spent before giving up.</param>
/// <param name="ObservedWindows">
/// The owned top-level windows the last census saw visible — the offenders on a failed
/// suppression, the evidence on a confirmed reveal.
/// </param>
/// <param name="Diagnostic">Bounded failure text when <paramref name="Confirmed"/> is false.</param>
public sealed record ManagedEtabsWindowConfirmation(
    bool Confirmed,
    int Observations,
    TimeSpan Waited,
    IReadOnlyList<nint> ObservedWindows,
    string? Diagnostic);

/// <summary>
/// How long the exact-owned window census is given, and how hard the guard sweeps.
/// </summary>
/// <param name="ConfirmationDeadline">
/// Ceiling on a suppression or reveal confirmation. <c>ShowWindow</c> against a window
/// owned by another process is not synchronous for the caller, so a single read after a
/// hide proves nothing; five seconds is a ceiling, not a delay.
/// </param>
/// <param name="PollInterval">How often the census is re-taken while confirming.</param>
/// <param name="BackstopSweepInterval">
/// How often the guard sweeps with no event to prompt it. This is the BACKSTOP, not the
/// mechanism — see <see cref="IOwnedWindowSurfaceMonitor"/> for why shortening it would
/// not have fixed what #20 measured.
/// </param>
/// <param name="Clock">The monotonic bounded-wait seam.</param>
public sealed record ManagedEtabsWindowPolicy(
    TimeSpan ConfirmationDeadline,
    TimeSpan PollInterval,
    TimeSpan BackstopSweepInterval,
    IManagedEtabsClock Clock)
{
    public static ManagedEtabsWindowPolicy Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        SystemManagedEtabsClock.Instance);

    /// <summary>Zero or negative intervals would make the confirmation loops unbounded.</summary>
    public TimeSpan PollInterval { get; } = PollInterval > TimeSpan.Zero
        ? PollInterval
        : throw new ArgumentOutOfRangeException(nameof(PollInterval));

    public TimeSpan BackstopSweepInterval { get; } = BackstopSweepInterval > TimeSpan.Zero
        ? BackstopSweepInterval
        : throw new ArgumentOutOfRangeException(nameof(BackstopSweepInterval));
}

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
/// Tells the guard, for ONE exact process, that a window of that process just surfaced.
///
/// <para><b>Why an event and not a shorter poll.</b> The #20 certification measured the
/// polling guard working — sustained exposure collapsed from 5.19 s to two flickers of
/// ~234 ms and ~462 ms — but flickers are what a sampler leaves behind. A window that
/// appears just after a tick stays up until the next one, so the exposure a poll can
/// guarantee is bounded by its own period; halving the period halves the residual and
/// removes nothing. An edge-triggered subscription is a different kind of guarantee: the
/// guard is woken BY the state change rather than discovering it on the next sample, so
/// exposure is bounded by scheduler latency instead of by a sampling window. It is not
/// zero — nothing outside the ETABS process can take a window down before it is
/// composited without injecting into that process — but it bounds the RACE rather than
/// its probability.</para>
///
/// <para>The subscription is process-scoped at the operating-system level, which is what
/// keeps this exact-owned: it is not a desktop-wide watch that filters afterwards.</para>
/// </summary>
public interface IOwnedWindowSurfaceMonitor : IDisposable
{
    /// <summary>
    /// Begins delivery for exactly <paramref name="processId"/>.
    /// <paramref name="onSurfaced"/> runs when a window of that process is created or
    /// shown; <paramref name="onBackstopTick"/> runs on a timer regardless, so a missed
    /// event still cannot leave a window up indefinitely.
    /// </summary>
    void Start(int processId, Action onSurfaced, Action onBackstopTick);

    /// <summary>Whether the operating-system subscription is actually installed.</summary>
    bool Subscribed { get; }
}

/// <summary>
/// Suppression of the on-screen windows of ONE exact, proven-owned ETABS process, and the
/// authority on whether that suppression is actually in effect.
///
/// <para><b>Why this is the authority.</b> The #20 certification settled it with evidence:
/// on ETABS 23.3 <c>cOAPI.Hide()</c> returns success and <c>cOAPI.Visible()</c> then stays
/// true indefinitely — 94 reads across 10.014 s — while the real windows were being
/// suppressed the whole time. CSI's flag is not a late transition; it is not a transition
/// at all. So CSI is telemetry here, and the exact-owned HWND census is the product's
/// acceptance gate for both background readiness and an explicit reveal.</para>
///
/// <para><b>Why it is safe.</b> It never searches for "an ETABS window". It is handed the
/// authoritative <see cref="IOwnedEtabsProcess"/> the launcher opened by exact identity
/// (pid + process start time + executable path) after a preflight census proved zero
/// pre-existing ETABS processes, and it acts only on windows whose owning process id is
/// that pid. Holding that handle open is also what makes the pid safe to compare against:
/// Windows will not recycle a pid while a handle on the process is open. Once the process
/// has exited the guard stops permanently rather than risk a recycled pid.</para>
///
/// <para><b>Why it is a latch.</b> Suppression ends exactly once. An explicit
/// <c>open-model</c> ends it through <see cref="ReleaseForExplicitUserAction"/>, which
/// also restores the handles it hid that are still its own; shutdown ends it through
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
    /// The background-readiness gate: sweeps and re-observes until the exact-owned census
    /// reports NO visible top-level window, or the bounded deadline is spent.
    ///
    /// <para>This is what "background UI suppression = CONFIRMED" means. It is an
    /// observation of Windows, not a report of what CSI was asked for.</para>
    /// </summary>
    ManagedEtabsWindowConfirmation ConfirmSuppressed();

    /// <summary>
    /// The explicit-reveal gate: observes until the exact-owned census reports at least one
    /// VISIBLE top-level window, or the bounded deadline is spent. Never hides anything —
    /// it is only ever called after suppression has been permanently retired.
    /// </summary>
    ManagedEtabsWindowConfirmation ConfirmRevealed();

    /// <summary>
    /// Ends suppression because the USER asked to see ETABS, and restores the windows this
    /// guard hid that are still, at that moment, its own.
    ///
    /// <para>The restore is not cosmetic, and it is now load bearing rather than defensive.
    /// With <c>cOAPI.Visible()</c> stuck true, the CSI policy reads "already visible" and
    /// issues no <c>Unhide</c> at all — so putting our own windows back is what actually
    /// puts ETABS on the engineer's screen.</para>
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
    private readonly IOwnedEtabsProcess _owned;
    private readonly ITopLevelWindows _windows;
    private readonly IOwnedWindowSurfaceMonitor? _monitor;
    private readonly ManagedEtabsWindowPolicy _policy;
    private readonly List<nint> _suppressed = [];
    private readonly object _gate = new();
    private bool _active = true;
    private bool _terminated;

    internal ManagedEtabsWindowGuard(
        IOwnedEtabsProcess owned,
        ITopLevelWindows windows,
        ManagedEtabsWindowPolicy policy,
        IOwnedWindowSurfaceMonitor? monitor)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(policy);

        _owned = owned;
        _windows = windows;
        _policy = policy;
        _monitor = monitor;

        // Subscribed before this constructor returns, so the caller's next blocking call —
        // ApplicationStart, which is exactly where #20 measured the window — is covered.
        _monitor?.Start(owned.Identity.Pid, OnOwnedWindowSurfaced, OnBackstopTick);
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

    /// <summary>Suppression passes prompted by an operating-system window event.</summary>
    internal int EventPasses { get; private set; }

    /// <summary>Suppression passes prompted only by the backstop timer.</summary>
    internal int BackstopPasses { get; private set; }

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

    /// <summary>Whether the operating-system window subscription is installed.</summary>
    internal bool Subscribed => _monitor?.Subscribed ?? false;

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

            foreach (var handle in OwnedVisibleHandles())
            {
                _windows.Hide(handle);
                if (!_suppressed.Contains(handle))
                {
                    _suppressed.Add(handle);
                }
            }
        }
    }

    /// <inheritdoc />
    public ManagedEtabsWindowConfirmation ConfirmSuppressed() => Confirm(
        wantVisible: false,
        ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
        "an owned ETABS top-level window is still on screen");

    /// <inheritdoc />
    public ManagedEtabsWindowConfirmation ConfirmRevealed() => Confirm(
        wantVisible: true,
        ManagedEtabsWindowErrorCodes.RevealNotConfirmed,
        "no owned ETABS top-level window became visible");

    /// <summary>
    /// The one confirmation loop, in both directions.
    ///
    /// <para>It polls rather than reading once because <c>ShowWindow</c> against a window
    /// owned by another process is not synchronous for the caller: the request is delivered
    /// to the owning thread, so an immediate re-read proves nothing either way. While
    /// suppression is still active each iteration also sweeps, so a window that keeps
    /// coming back is fought rather than merely observed — and if it wins, the offending
    /// handles are named in the diagnostic.</para>
    /// </summary>
    private ManagedEtabsWindowConfirmation Confirm(
        bool wantVisible,
        string errorCode,
        string summary)
    {
        var started = _policy.Clock.Timestamp;
        var observations = 0;
        while (true)
        {
            if (_owned.HasExited)
            {
                return new(
                    Confirmed: false,
                    observations,
                    _policy.Clock.ElapsedSince(started),
                    [],
                    EtabsApiDiagnosticFormatter.Bounded(string.Join(
                        "; ",
                        ManagedEtabsWindowErrorCodes.OwnedProcessGone,
                        $"ownedPid={_owned.Identity.Pid}",
                        "the owned ETABS process exited before its window state could be confirmed")));
            }

            if (!wantVisible)
            {
                // Best-effort, like every other pass: a window station that fails here is
                // reported by the observation below rather than thrown at the caller.
                SafeSweep();
            }

            List<nint> visible;
            try
            {
                lock (_gate)
                {
                    visible = OwnedVisibleHandles();
                }
            }
            catch (Exception exception)
            {
                LastSweepError = exception;
                return new(
                    Confirmed: false,
                    observations,
                    _policy.Clock.ElapsedSince(started),
                    [],
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "ITopLevelWindows.Enumerate",
                        exception));
            }

            observations++;
            var waited = _policy.Clock.ElapsedSince(started);
            if (visible.Count > 0 == wantVisible)
            {
                return new(Confirmed: true, observations, waited, visible, Diagnostic: null);
            }

            if (waited >= _policy.ConfirmationDeadline)
            {
                return new(
                    Confirmed: false,
                    observations,
                    waited,
                    visible,
                    EtabsApiDiagnosticFormatter.Bounded(string.Join(
                        "; ",
                        errorCode,
                        $"ownedPid={_owned.Identity.Pid}",
                        $"observations={observations}",
                        $"waitedMs={(long)waited.TotalMilliseconds}",
                        $"visibleOwnedWindows={visible.Count}",
                        $"handles=[{string.Join(", ", visible.Select(handle => $"0x{handle:X}"))}]",
                        summary)));
            }

            _policy.Clock.Wait(_policy.PollInterval);
        }
    }

    /// <summary>
    /// The exact-owned census: the handles of top-level windows that Windows currently
    /// reports visible AND that belong to the proven-owned process. Callers hold the gate.
    /// </summary>
    private List<nint> OwnedVisibleHandles() =>
    [
        .. _windows.Enumerate()
            .Where(window => window.ProcessId == _owned.Identity.Pid && window.IsVisible)
            .Select(window => window.Handle)
    ];

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

        // Delivery stops before the restore, so no in-flight event can hide what we put
        // back, and no hook or thread outlives the guard.
        _monitor?.Dispose();
        RestoreStillOwned(restoreTargets);
    }

    private void OnOwnedWindowSurfaced()
    {
        EventPasses++;
        SafeSweep();
    }

    private void OnBackstopTick()
    {
        BackstopPasses++;
        SafeSweep();
    }

    private void SafeSweep()
    {
        try
        {
            SweepOnce();
        }
        catch (Exception exception)
        {
            // Best-effort by construction: a transient window-station failure must not take
            // the daemon down. The bounded confirmation above is what actually gates
            // readiness, and it reports the failure truthfully.
            LastSweepError = exception;
        }
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
            // Ownership could not be re-proven, so nothing is touched. The bounded reveal
            // confirmation that follows reports the truth either way.
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
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Managed ETABS window suppression requires Windows.");
        }

        var policy = ManagedEtabsWindowPolicy.Default;
        return new ManagedEtabsWindowGuard(
            ownedProcess,
            new Win32TopLevelWindows(),
            policy,
            new Win32OwnedWindowSurfaceMonitor(policy.BackstopSweepInterval));
    }
}

/// <summary>
/// The user32 window-station boundary. Walks the desktop's top-level Z-order chain rather
/// than taking an enumeration callback, so no unmanaged function pointer and no
/// <c>AllowUnsafeBlocks</c> is needed — the same reasoning that keeps
/// <c>WindowsFileIdentity</c> on classic <c>DllImport</c>.
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

    public void Hide(nint handle)
    {
        // DIAGNOSTIC BUILD ONLY - branch diagnostic/alpha-22-passive-windows-actuation.
        // NOT FOR RELEASE. ShowWindow(SW_HIDE) is now issued from NOWHERE: the event
        // callback already does not actuate (inherited from the previous diagnostic) and
        // this removes the backstop and confirmation actuation as well.
        //
        // Everything else is deliberately intact - the hook still registers and filters
        // to the owned pid, the pump still runs, SweepOnce still identifies the exact
        // owned visible HWNDs, and ConfirmSuppressed still observes real Windows state.
        // ConfirmSuppressed is EXPECTED to fail honestly here, because nothing is ever
        // hidden; the causal question this build answers is decided earlier, at
        // cOAPI.ApplicationStart.
        //
        // SwHide is still referenced so the constant does not become unused and the
        // Release analyzer pile stays at zero.
        _ = handle;
        _ = SwHide;
    }

    public void Show(nint handle)
    {
        // DIAGNOSTIC BUILD ONLY - branch diagnostic/alpha-22-csi-unhide-no-show.
        // NOT FOR RELEASE. With Hide() already passive on the base commit, this removes
        // the LAST out-of-process window actuation: ShowWindow is now issued in NEITHER
        // direction, from nowhere in the process.
        //
        // That is the whole experiment. The production reveal restores our own hidden
        // HWNDs because #20 found cOAPI.Visible() stuck true, which makes the CSI policy
        // read "already visible" and issue no Unhide at all - so that restore, not CSI,
        // is what actually reaches the screen today. If raw cOAPI.Unhide() can put the
        // window back on its own, this actuator is unnecessary and the Windows layer can
        // become purely observational.
        //
        // SwShow is still referenced so the constant does not become unused and the
        // Release analyzer pile stays at zero.
        _ = handle;
        _ = SwShow;
    }

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

/// <summary>
/// How a monitor teardown ended. Teardown asks the pump thread to stop and gives it a
/// bounded wait; the two answers are materially different and are never conflated.
/// </summary>
internal enum OwnedWindowMonitorTeardown
{
    /// <summary>Teardown has not been attempted.</summary>
    NotTornDown,

    /// <summary>The pump exited within the bound, or there was never a pump to wait for.</summary>
    Resolved,

    /// <summary>
    /// The pump had NOT exited when the bound expired — almost always because the
    /// <c>SetWinEventHook</c> call it is inside has not returned yet. Retirement has taken
    /// effect regardless, so that thread can no longer pump, observe, or hand a hook to the
    /// monitor; it is reported rather than presented as a clean teardown.
    /// </summary>
    PumpStillAlive
}

/// <summary>
/// A process-scoped <c>SetWinEventHook</c> subscription plus a backstop tick, on one
/// dedicated message-pumping thread.
///
/// <para>The hook is installed with <c>idProcess</c> set to the proven-owned pid, so the
/// operating system itself filters delivery to that one process — this is not a
/// desktop-wide watch that discards other processes' events afterwards, and no window
/// outside the owned process is ever observed through it.</para>
///
/// <para><c>WINEVENT_OUTOFCONTEXT</c> means the callback is delivered to THIS thread's
/// message queue rather than injected into ETABS, so nothing of ours runs inside the
/// guarded process. That is also why the thread must pump messages: the callback fires
/// from inside the message-retrieval call.</para>
///
/// <para><b>Activation is a handshake, and it is proven.</b> <see cref="Start"/> returns
/// only once the pump has reported back that <c>SetWinEventHook</c> ran AND returned a
/// non-zero hook. Anything else — the acknowledgement never arriving, or arriving with a
/// zero hook — tears the pump down and throws. There is deliberately no backstop-only
/// fallback: sampling alone is what #20 measured the ~234 ms and ~462 ms flickers through,
/// so a session that cannot subscribe must fail loudly rather than quietly degrade to the
/// mechanism that was already rejected.</para>
///
/// <para><b>Teardown is a custody transfer, not a deadline.</b> The one thing teardown
/// cannot do is cancel a <c>SetWinEventHook</c> call that is already in flight, so a pump
/// can outlive any join bound it is given. Three rules make that survivable, and none of
/// them depends on the pump being quick:</para>
///
/// <list type="number">
/// <item><description><b>Retirement is a latch, taken before the wait.</b> Once it is set
/// the monitor will not accept a hook, the callback refuses to run, and the message loop is
/// never entered. A pump that wakes up afterwards is already disarmed.</description></item>
/// <item><description><b>The installing thread is the only remover.</b> Whatever
/// <c>SetWinEventHook</c> returns, and however late, that same thread unhooks it before it
/// exits. Teardown never removes a hook itself — which is also what user32 asks for, since
/// <c>UnhookWinEvent</c> belongs on the thread that hooked.</description></item>
/// <item><description><b>The stop handle is reference counted.</b> Signalling and disposal
/// are separate concerns: teardown signals, and whichever of {teardown, pump} leaves last
/// disposes. Nothing a live pump can still reach is closed underneath it.</description></item>
/// </list>
///
/// <para>What teardown does NOT do is claim a success it cannot see. A pump still alive at
/// the bound is reported as <see cref="OwnedWindowMonitorTeardown.PumpStillAlive"/> and
/// named in the activation failure, because "that thread is disarmed" and "that thread is
/// gone" are different facts and the release gate is entitled to both.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32OwnedWindowSurfaceMonitor : IOwnedWindowSurfaceMonitor
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 0x0001;
    private const uint WaitObject0 = 0;

    /// <summary>
    /// How long activation waits for the pump's acknowledgement. Generous, because it only
    /// covers a thread start and one user32 call — and because exceeding it is a failure,
    /// not a fallback, so it must not fire on a merely busy machine.
    /// </summary>
    public static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long teardown waits for the pump to exit before reporting it as still alive.
    /// This is a REPORTING bound, not a safety bound: correctness does not improve by
    /// lengthening it, because retirement has already disarmed that thread and it removes
    /// its own hook whenever its install returns. The bound only decides whether teardown
    /// gets to say "gone" or has to say "disarmed, still running".
    /// </summary>
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _backstopInterval;
    private readonly TimeSpan _installTimeout;
    private readonly TimeSpan _joinTimeout;
    private readonly Func<int, WinEventProc, nint> _installHook;
    private readonly Action<nint> _removeHook;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();

    /// <summary>
    /// Live references on <see cref="_stop"/>: one held by the monitor, and one more by a
    /// pump from before it can run until after its last use of the handle. It is disposed
    /// by whichever releases last, which is the whole reason a pump that outlives teardown
    /// cannot come back to a closed — or worse, recycled — kernel handle.
    /// </summary>
    private int _stopUsers = 1;

    // The delegate must outlive the hook: the OS holds a raw function pointer to it.
    private WinEventProc? _proc;
    private Thread? _thread;
    private nint _hook;
    private int _retired;
    private OwnedWindowMonitorTeardown _teardown = OwnedWindowMonitorTeardown.NotTornDown;
    private Exception? _installError;
    private Exception? _teardownError;

    public Win32OwnedWindowSurfaceMonitor(TimeSpan backstopInterval)
        : this(backstopInterval, InstallHook, RemoveHook, DefaultInstallTimeout)
    {
    }

    /// <summary>
    /// The same monitor with the user32 hook calls and the acknowledgement deadline behind
    /// parameters, so the activation contract — which is pure control flow around two
    /// P/Invokes — is exercisable without waiting on the real window station to misbehave.
    /// The public constructor above binds the real calls, and that is what production uses.
    /// </summary>
    internal Win32OwnedWindowSurfaceMonitor(
        TimeSpan backstopInterval,
        Func<int, WinEventProc, nint> installHook,
        Action<nint> removeHook,
        TimeSpan installTimeout)
        : this(backstopInterval, installHook, removeHook, installTimeout, DefaultJoinTimeout)
    {
    }

    /// <summary>
    /// And with the teardown join bound behind a parameter too, so the state a pump enters
    /// when it outlives that bound is reachable in a test without anyone sleeping for
    /// seconds to get there. Production always takes <see cref="DefaultJoinTimeout"/>.
    /// </summary>
    internal Win32OwnedWindowSurfaceMonitor(
        TimeSpan backstopInterval,
        Func<int, WinEventProc, nint> installHook,
        Action<nint> removeHook,
        TimeSpan installTimeout,
        TimeSpan joinTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backstopInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(installTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(joinTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(installHook);
        ArgumentNullException.ThrowIfNull(removeHook);

        _backstopInterval = backstopInterval;
        _installTimeout = installTimeout;
        _joinTimeout = joinTimeout;
        _installHook = installHook;
        _removeHook = removeHook;
    }

    /// <inheritdoc />
    public bool Subscribed => Volatile.Read(ref _hook) != nint.Zero;

    /// <summary>
    /// Whether this monitor has been retired. Never false again once true, and the single
    /// fact every disarming rule below reads.
    /// </summary>
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>How the teardown ended — for the failure text, and for teardown assertions.</summary>
    internal OwnedWindowMonitorTeardown Teardown
    {
        get
        {
            lock (_gate)
            {
                return _teardown;
            }
        }
    }

    /// <summary>A failure while removing a hook, if any. Recorded, never thrown.</summary>
    internal Exception? TeardownError => _teardownError;

    /// <summary>
    /// Whether the stop handle has been closed. This is the one resource a still-live pump
    /// can reach, so it is observable rather than assumed: a teardown that closed it while
    /// its pump was still running is exactly the defect this monitor was repaired for.
    /// </summary>
    internal bool StopSignalClosed => _stop.SafeWaitHandle.IsClosed;

    /// <summary>Whether the pump thread is still alive, for teardown assertions.</summary>
    internal bool PumpAlive
    {
        get
        {
            lock (_gate)
            {
                return _thread?.IsAlive ?? false;
            }
        }
    }

    /// <inheritdoc />
    public void Start(int processId, Action onSurfaced, Action onBackstopTick)
    {
        ArgumentNullException.ThrowIfNull(onSurfaced);
        ArgumentNullException.ThrowIfNull(onBackstopTick);

        // Deliberately never disposed. The pump sets it on its way past the install call,
        // which may be long after this method has given up waiting — disposing it here
        // would be the same defect as closing the stop handle underneath a live pump, and
        // the runtime reclaims its handle either way.
        var installed = new ManualResetEventSlim(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired != 0, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException(
                    "This owned-window monitor has already been started.");
            }

            var pump = new Thread(() => Pump(processId, onSurfaced, onBackstopTick, installed))
            {
                IsBackground = true,
                Name = "etabs-window-guard"
            };
            _thread = pump;

            // The pump's reference is taken BEFORE it can run, so there is no instant in
            // which a pump exists uncounted and teardown could close the handle under it.
            _ = Interlocked.Increment(ref _stopUsers);
            try
            {
                pump.Start();
            }
            catch
            {
                _ = Interlocked.Decrement(ref _stopUsers);
                _thread = null;
                throw;
            }
        }

        // Deliberately OUTSIDE the gate. The pump publishes its result and signals; waiting
        // for that acknowledgement while holding the lock the pump needs would guarantee the
        // wait times out and let activation "succeed" before any hook existed — the exact
        // race the subscription was introduced to remove.
        var acknowledged = installed.Wait(_installTimeout);
        if (acknowledged && Subscribed)
        {
            return;
        }

        FailActivation(acknowledged);
    }

    public void Dispose() => _ = Retire();

    /// <summary>
    /// Activation could not be proven, so the monitor is retired and the caller is told —
    /// including, when it is true, that the pump had not exited yet. That last part is the
    /// point: an activation that throws while a thread of ours is still running is a
    /// different fact from one that throws over a fully joined teardown, and this text is
    /// what the caller carries into the launch cleanup envelope.
    /// </summary>
    private void FailActivation(bool acknowledged)
    {
        var teardown = Retire();

        var reason = acknowledged
            ? "SetWinEventHook returned no hook for the owned ETABS process, so window " +
                "events cannot be observed. Background UI suppression would degrade to " +
                "sampling, which is not sufficient."
            : "The owned-window subscription did not report installation within " +
                $"{_installTimeout.TotalSeconds:0.###}s.";

        if (teardown == OwnedWindowMonitorTeardown.PumpStillAlive)
        {
            reason +=
                " Teardown is UNRESOLVED: the subscription thread had still not exited " +
                $"{_joinTimeout.TotalSeconds:0.###}s after being told to stop, so this " +
                "activation is reported as failed WITHOUT a completed cleanup. That thread " +
                "is already retired — it cannot pump, cannot deliver a callback, and it " +
                "removes whatever hook its install returns before it exits.";
        }

        throw new InvalidOperationException(reason, _installError);
    }

    /// <summary>
    /// The single teardown path, shared by disposal and by failed activation, and idempotent
    /// because reveal-then-shutdown reaches it twice.
    ///
    /// <para>Retirement is latched first and under the gate, so it is in force BEFORE the
    /// wait rather than after it. Everything that could still act — claiming a hook, running
    /// the callback, entering the message loop — reads that latch, which is why the bounded
    /// join below is only ever a question about reporting, never about safety.</para>
    /// </summary>
    private OwnedWindowMonitorTeardown Retire()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_retired != 0)
            {
                return _teardown;
            }

            Volatile.Write(ref _retired, 1);
            thread = _thread;
        }

        // Signalling is not disposal. Set() only wakes a pump that is in the message loop;
        // the handle stays open until this reference is released AND the pump has released
        // its own, so a pump still inside its install call cannot return to a closed handle.
        _stop.Set();

        var resolved = thread is null || thread.Join(_joinTimeout);
        var teardown = resolved
            ? OwnedWindowMonitorTeardown.Resolved
            : OwnedWindowMonitorTeardown.PumpStillAlive;
        lock (_gate)
        {
            _teardown = teardown;
        }

        ReleaseStopHandle();
        return teardown;
    }

    /// <summary>Drops one reference on the stop handle, disposing it on the last one.</summary>
    private void ReleaseStopHandle()
    {
        if (Interlocked.Decrement(ref _stopUsers) == 0)
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Hands a freshly installed hook to the monitor, but only while the monitor is still
    /// live. A refusal means this pump is late: the hook stays its own to remove, and it
    /// must not arm anything with it.
    /// </summary>
    private bool TryClaimHook(nint hook)
    {
        lock (_gate)
        {
            if (_retired != 0)
            {
                return false;
            }

            Volatile.Write(ref _hook, hook);
            return true;
        }
    }

    private void ReleaseClaimedHook()
    {
        lock (_gate)
        {
            Volatile.Write(ref _hook, nint.Zero);
        }
    }

    private void Pump(
        int processId,
        Action onSurfaced,
        Action onBackstopTick,
        ManualResetEventSlim installed)
    {
        var hook = nint.Zero;
        try
        {
            // Held in a field for the hook's lifetime; a collected delegate is a hard crash.
            var proc = BuildCallback(onSurfaced);
            _proc = proc;

            // Retirement can beat this thread to its first instruction — the activation
            // deadline is a wall clock and this thread is at the scheduler's mercy.
            // Installing then would put a hook on the window station only to take it off
            // again, against a pid that is already no longer provably ours.
            if (!IsRetired)
            {
                try
                {
                    hook = _installHook(processId, proc);
                }
                catch (Exception exception)
                {
                    _installError = exception;
                }
            }

            // Custody, decided exactly once and under the gate.
            var claimed = hook != nint.Zero && TryClaimHook(hook);

            // Acknowledged only once custody is settled, success or not: Start must never
            // wait out its deadline for an answer that already exists, and must never see a
            // Subscribed that is about to be true rather than true.
            installed.Set();

            if (!claimed)
            {
                // Either nothing installed, or this pump is late and owns its own hook.
                // Both mean the same thing here: nothing is subscribed on the monitor's
                // behalf, so there is nothing to pump — and the backstop must NOT run on
                // its own. Sampling-only suppression is the mechanism #20 measured the
                // flickers through, and a failed activation is about to be reported.
                return;
            }

            RunMessageLoop(onBackstopTick);
            ReleaseClaimedHook();
        }
        finally
        {
            // The installing thread is the only remover, on every path out of this method.
            // user32 wants the unhook from the thread that hooked, and putting it here
            // rather than on the caller's thread during teardown is also what makes a late
            // install impossible to orphan: this runs whenever the install returns, however
            // long after teardown gave up waiting for it.
            if (hook != nint.Zero)
            {
                RemoveHookSafely(hook);
            }

            // Unrooted only after the hook is gone: the OS held a raw pointer to it.
            _proc = null;
            ReleaseStopHandle();
        }
    }

    /// <summary>
    /// The callback the operating system is given. It reads the retirement latch itself
    /// rather than trusting the pump to be the only gate: out-of-context delivery can only
    /// reach us from inside this thread's message retrieval, which retirement stops — but
    /// the pid this hook was scoped to also stops being provably ours at that same instant,
    /// so the callback refuses on its own account too.
    /// </summary>
    private WinEventProc BuildCallback(Action onSurfaced) =>
        (_, eventType, _, idObject, idChild, _, _) =>
        {
            if (IsRetired)
            {
                return;
            }

            if (idObject == ObjIdWindow
                && idChild == ChildIdSelf
                && (eventType == EventObjectCreate || eventType == EventObjectShow))
            {
                // DIAGNOSTIC BUILD ONLY - branch diagnostic/alpha-22-no-event-actuation.
                // NOT FOR RELEASE. The subscription still installs, still filters to the
                // owned pid, and still receives this event; only the ACTUATION is cut, so
                // no ShowWindow(SW_HIDE) is issued from the event edge. That is what
                // separates "hook registration" from "immediate edge-triggered hide during
                // ETABS window creation" as the crash actuator. The backstop sweep, the
                // activation handshake and the retirement/custody rules are untouched.
                _ = onSurfaced;
            }
        };

    private void RunMessageLoop(Action onBackstopTick)
    {
        var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle() };
        var backstopMs = (uint)Math.Max(1, (long)_backstopInterval.TotalMilliseconds);
        while (true)
        {
            var wait = MsgWaitForMultipleObjects(1, handles, false, backstopMs, QsAllInput);
            if (wait == WaitObject0)
            {
                return;
            }

            // Draining the queue is what actually invokes the WinEvent callback above.
            while (PeekMessageW(out var message, nint.Zero, 0, 0, PmRemove))
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessageW(ref message);
            }

            onBackstopTick();
        }
    }

    private void RemoveHookSafely(nint hook)
    {
        try
        {
            _removeHook(hook);
        }
        catch (Exception exception)
        {
            // Recorded rather than thrown. This runs on the pump thread while it is on its
            // way out, where an escaping exception would take the whole daemon down and
            // still would not have removed the hook.
            _teardownError = exception;
        }
    }

    private static nint InstallHook(int processId, WinEventProc proc) => SetWinEventHook(
        EventObjectCreate,
        EventObjectShow,
        nint.Zero,
        proc,
        unchecked((uint)processId),
        0,
        WinEventOutOfContext);

    private static void RemoveHook(nint hook) => _ = UnhookWinEvent(hook);

    internal delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint MsgWaitForMultipleObjects(
        uint nCount,
        nint[] pHandles,
        [MarshalAs(UnmanagedType.Bool)] bool fWaitAll,
        uint dwMilliseconds,
        uint dwWakeMask);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message,
        nint hWnd,
        uint filterMin,
        uint filterMax,
        uint removeMsg);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint DispatchMessageW(ref NativeMessage message);
}
