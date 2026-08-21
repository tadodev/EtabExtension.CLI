// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The Windows suppression layer, and — after #20 — the product's visibility AUTHORITY.
///
/// <para>The certification settled two things with evidence. The exact-owned guard works:
/// sustained exposure fell from 5.19 s to two flickers of ~234 ms and ~462 ms. And
/// <c>cOAPI.Visible()</c> is not an oracle: it held true for 94 reads across 10.014 s
/// after a successful <c>Hide()</c>, while those same windows were being suppressed
/// throughout. So background readiness and explicit reveal are both decided here, from the
/// exact-owned HWND census, and CSI is telemetry.</para>
///
/// <para>Its entire risk is targeting, so much of this suite is still about that: a guard
/// that could reach a window it does not own would be a far worse defect than the one it
/// fixes.</para>
/// </summary>
public sealed class ManagedEtabsWindowGuardTests
{
    private static readonly ManagedProcessIdentity Owned = new(
        4242,
        new DateTimeOffset(2026, 8, 21, 5, 12, 12, TimeSpan.Zero),
        @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe");

    private const int ForeignPid = 4243;

    // ── Targeting ────────────────────────────────────────────────────────────

    [Fact]
    public void ASweepHidesEveryVisibleWindowOfTheExactOwnedProcess()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(10, Owned.Pid, IsVisible: true),
            new TopLevelWindow(11, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);

        guard.SweepOnce();

        Assert.Equal([10, 11], windows.Hidden);
        Assert.Equal([(nint)10, 11], guard.Suppressed);
        Assert.True(guard.IsActive);
    }

    /// <summary>
    /// The rule the whole design rests on. A foreign process's window is not hidden, not
    /// shown, and not recorded — even when it is the only visible window on the desktop and
    /// even when it belongs to another ETABS.
    /// </summary>
    [Fact]
    public void NoWindowOfAnyOtherProcessIsEverTouched()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(20, ForeignPid, IsVisible: true),
            new TopLevelWindow(21, Owned.Pid, IsVisible: true),
            new TopLevelWindow(22, ForeignPid, IsVisible: true));
        var guard = Guard(windows, out _, out _);

        guard.SweepOnce();
        guard.ReleaseForExplicitUserAction();

        Assert.Equal([21], windows.Hidden);
        Assert.Equal([21], windows.Shown);
        Assert.DoesNotContain((nint)20, windows.Touched);
        Assert.DoesNotContain((nint)22, windows.Touched);
    }

    /// <summary>
    /// A foreign window is also never the reason a confirmation succeeds or fails. The
    /// census is exact-owned in both directions: another process's visible window must not
    /// block background readiness, and must not stand in for a revealed ETABS.
    /// </summary>
    [Fact]
    public void AForeignVisibleWindowDecidesNeitherConfirmation()
    {
        var windows = new FakeWindows(new TopLevelWindow(25, ForeignPid, IsVisible: true));
        var guard = Guard(windows, out _, out _);

        var suppressed = guard.ConfirmSuppressed();
        var revealed = guard.ConfirmRevealed();

        Assert.True(suppressed.Confirmed);
        Assert.False(revealed.Confirmed);
        Assert.Empty(windows.Touched);
    }

    /// <summary>
    /// A pid is only provably ours while the authoritative handle keeps Windows from
    /// recycling it. Once the process is gone the pid belongs to whoever gets it next, so
    /// the guard stops for good rather than acting on it.
    /// </summary>
    [Fact]
    public void AnExitedOwnedProcessStopsTheGuardInsteadOfActingOnItsPid()
    {
        var windows = new FakeWindows(new TopLevelWindow(30, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out var owned, out _);
        owned.Exit();

        guard.SweepOnce();
        guard.SweepOnce();

        Assert.Empty(windows.Touched);
        Assert.False(guard.IsActive);
    }

    /// <summary>And a dead process is never confirmed either way — it is reported as gone.</summary>
    [Fact]
    public void AnExitedOwnedProcessIsReportedGoneRatherThanConfirmed()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out var owned, out _);
        owned.Exit();

        var suppressed = guard.ConfirmSuppressed();
        var revealed = guard.ConfirmRevealed();

        Assert.False(suppressed.Confirmed);
        Assert.False(revealed.Confirmed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.OwnedProcessGone,
            suppressed.Diagnostic!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Cardex's non-idempotency lesson applied to windows: a window that is already down
    /// is left alone, so the guard cannot churn the desktop while it waits.
    /// </summary>
    [Fact]
    public void AlreadyHiddenWindowsOfTheOwnedProcessAreLeftAlone()
    {
        var windows = new FakeWindows(new TopLevelWindow(40, Owned.Pid, IsVisible: false));
        var guard = Guard(windows, out _, out _);

        guard.SweepOnce();

        Assert.Empty(windows.Touched);
        Assert.Empty(guard.Suppressed);
    }

    // ── Windows-authoritative confirmation ───────────────────────────────────

    /// <summary>
    /// The ruling, stated as a test. On ETABS 23.3 <c>cOAPI.Visible()</c> stays true
    /// forever; this layer never consults it, and confirms suppression from what Windows
    /// actually reports about the owned process's windows.
    /// </summary>
    [Fact]
    public void SuppressionIsConfirmedFromTheOwnedCensusWithNoReferenceToCsi()
    {
        var windows = new FakeWindows(new TopLevelWindow(50, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmSuppressed();

        Assert.True(confirmation.Confirmed);
        Assert.Empty(confirmation.ObservedWindows);
        Assert.Null(confirmation.Diagnostic);
        Assert.Equal([50], windows.Hidden);
    }

    /// <summary>
    /// <c>ShowWindow</c> against another process's window is not synchronous for the
    /// caller, so a single read after a hide proves nothing. A window that takes a few
    /// observations to go down is confirmed, not failed.
    /// </summary>
    [Fact]
    public void AWindowThatTakesSeveralObservationsToGoDownIsStillConfirmed()
    {
        var windows = new FakeWindows(new TopLevelWindow(51, Owned.Pid, IsVisible: true))
        {
            HidesAfterRequests = 4
        };
        var guard = Guard(windows, out _, out var clock);

        var confirmation = guard.ConfirmSuppressed();

        Assert.True(confirmation.Confirmed);
        Assert.Equal(4, confirmation.Observations);
        Assert.Equal(TimeSpan.FromMilliseconds(150), clock.Elapsed);
    }

    /// <summary>
    /// And a window that will not go down fails the gate explicitly, naming the exact
    /// offending handle. This is the background-readiness refusal — it now rests on real
    /// Windows state rather than on a CSI flag that never clears.
    /// </summary>
    [Fact]
    public void AnOwnedWindowThatStaysVisibleFailsSuppressionAtTheDeadline()
    {
        var windows = new FakeWindows(new TopLevelWindow(0x2A4, Owned.Pid, IsVisible: true))
        {
            StaysVisible = true
        };
        var guard = Guard(windows, out _, out var clock);

        var confirmation = guard.ConfirmSuppressed();

        Assert.False(confirmation.Confirmed);
        Assert.Equal([(nint)0x2A4], confirmation.ObservedWindows);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
            confirmation.Diagnostic!,
            StringComparison.Ordinal);
        Assert.Contains("0x2A4", confirmation.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains($"ownedPid={Owned.Pid}", confirmation.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void RevealIsConfirmedOnlyWhenAnOwnedWindowIsActuallyVisible()
    {
        var windows = new FakeWindows(new TopLevelWindow(60, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmRevealed();

        Assert.True(confirmation.Confirmed);
        Assert.Equal([(nint)60], confirmation.ObservedWindows);

        // Confirming a reveal never hides anything — it is pure observation.
        Assert.Empty(windows.Touched);
    }

    [Fact]
    public void RevealFailsWhenNoOwnedWindowEverBecomesVisible()
    {
        var windows = new FakeWindows(new TopLevelWindow(61, Owned.Pid, IsVisible: false));
        var guard = Guard(windows, out _, out var clock);

        var confirmation = guard.ConfirmRevealed();

        Assert.False(confirmation.Confirmed);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.RevealNotConfirmed,
            confirmation.Diagnostic!,
            StringComparison.Ordinal);
    }

    // ── Event-driven suppression ─────────────────────────────────────────────

    /// <summary>
    /// The mechanism, and the reason the #20 flickers are not a tuning problem.
    ///
    /// <para>A sampler can only promise "gone by the next tick". This guard is woken BY the
    /// window surfacing, so the window is taken down with no intervening tick at all — the
    /// residual is scheduler latency, not a sampling period.</para>
    /// </summary>
    [Fact]
    public void AWindowSurfacingIsSuppressedOnTheEventWithNoInterveningBackstopTick()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out _, out _, out var monitor);

        // The backstop has just run; nothing is on screen yet.
        monitor.RaiseBackstopTick();
        Assert.Empty(windows.Hidden);

        // ETABS surfaces its frame midway between ticks.
        windows.Surface(300, Owned.Pid);
        monitor.RaiseSurfaced();

        Assert.Equal([300], windows.Hidden);
        Assert.False(windows.IsVisible(300));
        Assert.Equal(1, guard.EventPasses);
        Assert.Equal(1, guard.BackstopPasses);
    }

    /// <summary>
    /// The same scenario with the event removed — the flicker #20 measured, reproduced
    /// offline. The window is on screen for the whole gap between ticks, and no shorter
    /// period removes that; it only shortens the exposure.
    /// </summary>
    [Fact]
    public void WithoutTheEventTheWindowStaysOnScreenUntilTheNextBackstopTick()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out _, out _, out var monitor);

        monitor.RaiseBackstopTick();
        windows.Surface(301, Owned.Pid);

        // This interval is the flicker: real, visible, and bounded only by the tick period.
        Assert.True(windows.IsVisible(301));
        Assert.Equal(0, guard.EventPasses);

        monitor.RaiseBackstopTick();

        Assert.False(windows.IsVisible(301));
        Assert.Equal([301], windows.Hidden);
    }

    /// <summary>
    /// The subscription is process-scoped, and scoped to the PROVEN-owned pid. It is armed
    /// from the constructor, so the caller's next blocking call — <c>ApplicationStart</c>,
    /// where #20 measured the window — is already covered.
    /// </summary>
    [Fact]
    public void TheSubscriptionIsArmedForTheExactOwnedPidBeforeTheGuardIsUsable()
    {
        var monitor = new FakeMonitor();
        var owned = new FakeOwnedProcess(Owned);

        var guard = new ManagedEtabsWindowGuard(
            owned,
            new FakeWindows(),
            Policy(new VirtualClock()),
            monitor);

        Assert.Equal(1, monitor.StartCalls);
        Assert.Equal(Owned.Pid, monitor.ProcessId);
        Assert.True(guard.Subscribed);
    }

    /// <summary>
    /// A modal or secondary owned top-level window is covered by exactly the same rule as
    /// the main frame — there is no "main window" concept anywhere in the targeting, only
    /// ownership.
    /// </summary>
    [Fact]
    public void SecondaryAndModalOwnedTopLevelWindowsAreCoveredToo()
    {
        var windows = new FakeWindows(new TopLevelWindow(310, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _, out var monitor);
        monitor.RaiseSurfaced();

        // A modal dialog appears later, on top of the already-suppressed frame.
        windows.Surface(311, Owned.Pid);
        monitor.RaiseSurfaced();

        Assert.Equal([310, 311], windows.Hidden);
        Assert.True(guard.ConfirmSuppressed().Confirmed);
    }

    /// <summary>Delivery stops with the guard: no hook or thread outlives the session.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminatingTheGuardDisposesTheSubscription(bool forUser)
    {
        var guard = Guard(new FakeWindows(), out _, out _, out var monitor);

        if (forUser)
        {
            guard.ReleaseForExplicitUserAction();
        }
        else
        {
            guard.Dispose();
        }

        Assert.Equal(1, monitor.DisposeCalls);
        Assert.False(guard.Subscribed);
    }

    // ── The latch ────────────────────────────────────────────────────────────

    /// <summary>
    /// The explicit-reveal half of the latch, and now load bearing rather than defensive:
    /// with <c>cOAPI.Visible()</c> stuck true, CSI reads "already visible" and issues no
    /// <c>Unhide</c>, so putting our own windows back is what reaches the screen.
    /// </summary>
    [Fact]
    public void ReleasingForAnExplicitUserActionRestoresExactlyWhatWasSuppressed()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(70, Owned.Pid, IsVisible: true),
            new TopLevelWindow(71, ForeignPid, IsVisible: true));
        var guard = Guard(windows, out _, out _);
        guard.SweepOnce();

        guard.ReleaseForExplicitUserAction();

        Assert.Equal([70], windows.Shown);
        Assert.False(guard.IsActive);
        Assert.True(guard.ConfirmRevealed().Confirmed);
    }

    /// <summary>
    /// The restore-time ownership recheck. Suppression filters by owning process id, but
    /// what gets SAVED is a raw <c>HWND</c> value — and the open process handle does not
    /// protect that. It keeps Windows from recycling the pid; it says nothing about a handle
    /// value, which Windows may hand to a different window in a different process the moment
    /// ETABS destroys the one we hid.
    /// </summary>
    [Fact]
    public void ASuppressedHandleThatNowBelongsToAnotherProcessIsNeverShown()
    {
        var windows = new FakeWindows(new TopLevelWindow(80, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);
        guard.SweepOnce();
        Assert.Equal([(nint)80], guard.Suppressed);

        windows.Reassign(80, ForeignPid);

        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    [Fact]
    public void ASuppressedHandleThatNoLongerExistsIsNeverShown()
    {
        var windows = new FakeWindows(new TopLevelWindow(81, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);
        guard.SweepOnce();

        windows.Destroy(81);
        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    [Fact]
    public void AnExitedOwnedProcessRestoresNothingOnReveal()
    {
        var windows = new FakeWindows(new TopLevelWindow(82, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out var owned, out _);
        guard.SweepOnce();
        owned.Exit();

        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    /// <summary>Shutdown is not a reveal: a process on its way out must not flash a window.</summary>
    [Fact]
    public void DisposingRestoresNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(90, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);
        guard.SweepOnce();

        guard.Dispose();

        Assert.Empty(windows.Shown);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// The latch. Once suppression has ended it never resumes, whatever calls arrive
    /// afterwards — which is exactly why a background command reusing a session the user
    /// asked to see cannot take the window away again.
    /// </summary>
    [Fact]
    public void SuppressionNeverResumesAfterAnExplicitRelease()
    {
        var windows = new FakeWindows(new TopLevelWindow(100, Owned.Pid, IsVisible: true))
        {
            StaysVisible = true
        };
        var guard = Guard(windows, out _, out _, out var monitor);
        guard.ReleaseForExplicitUserAction();

        guard.SweepOnce();
        monitor.RaiseSurfaced();
        monitor.RaiseBackstopTick();

        Assert.Empty(windows.Hidden);
        Assert.False(guard.IsActive);
    }

    [Fact]
    public void TerminatingTwiceRestoresOnceAndReArmsNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(110, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _, out var monitor);
        guard.SweepOnce();

        guard.ReleaseForExplicitUserAction();
        guard.Dispose();
        guard.ReleaseForExplicitUserAction();
        guard.SweepOnce();

        Assert.Equal([110], windows.Shown);
        Assert.Equal([110], windows.Hidden);
        Assert.Equal(1, monitor.DisposeCalls);
        Assert.False(guard.IsActive);
    }

    [Fact]
    public void DisposingAfterAShutdownStillRestoresNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(120, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _, out _);
        guard.SweepOnce();

        guard.Dispose();
        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    /// <summary>
    /// A window station that misbehaves must not take the daemon down with it, and must not
    /// be reported as a confirmed state either.
    /// </summary>
    [Fact]
    public void ASweepFailureIsRecordedAndReportedRatherThanThrownOrIgnored()
    {
        var windows = new FakeWindows(new TopLevelWindow(130, Owned.Pid, IsVisible: true))
        {
            EnumerateException = new InvalidOperationException("window station went away")
        };
        var guard = Guard(windows, out _, out _, out var monitor);

        monitor.RaiseSurfaced();
        var confirmation = guard.ConfirmSuppressed();

        Assert.IsType<InvalidOperationException>(guard.LastSweepError);
        Assert.False(confirmation.Confirmed);
        Assert.Contains("Enumerate", confirmation.Diagnostic!, StringComparison.Ordinal);
    }

    // ── Contract shape ───────────────────────────────────────────────────────

    /// <summary>
    /// The structural half of "never operates on an unproven or global ETABS pid": there is
    /// no signature through which a bare process id could be guarded. Activation takes the
    /// authoritative handle the launcher opened by exact identity, and nothing else.
    /// </summary>
    [Fact]
    public void TheOnlyWayToArmAGuardIsWithAnAuthoritativeOwnedHandle()
    {
        var methods = typeof(IManagedEtabsWindowGuardFactory).GetMethods();

        var activate = Assert.Single(methods);
        Assert.Equal(nameof(IManagedEtabsWindowGuardFactory.Activate), activate.Name);
        Assert.Equal(
            [typeof(IOwnedEtabsProcess)],
            activate.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.Throws<ArgumentNullException>(
            () => WindowsManagedEtabsWindowGuardFactory.Instance.Activate(null!));
    }

    /// <summary>
    /// And the guard's own surface offers no way back on. Suppression is armed once, by the
    /// launcher, and ended once — there is no re-arm to reach from a command path.
    /// </summary>
    [Fact]
    public void TheGuardContractExposesNoWayToResumeSuppression()
    {
        var members = typeof(IManagedEtabsWindowGuard)
            .GetInterfaces()
            .Append(typeof(IManagedEtabsWindowGuard))
            .SelectMany(contract => contract.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "ConfirmRevealed",
                "ConfirmSuppressed",
                "Dispose",
                "Identity",
                "IsActive",
                "ReleaseForExplicitUserAction"
            ],
            members);
    }

    /// <summary>The guard's identity is the handle's identity — never a value it was told.</summary>
    [Fact]
    public void TheGuardReportsTheIdentityOfTheHandleItWasGiven()
    {
        var guard = Guard(new FakeWindows(), out var owned, out _);

        Assert.Equal(owned.Identity, guard.Identity);
        Assert.Equal(Owned, guard.Identity);
    }

    // ── The real Win32 subscription ──────────────────────────────────────────

    /// <summary>
    /// The production mechanism, exercised for real with the real user32 calls: the hook
    /// installs, the message-pumping thread runs, the backstop ticks, and disposal tears
    /// both down deterministically.
    ///
    /// <para>Subscribed to THIS process, with a callback that only counts. Nothing is
    /// enumerated and no window is touched, so the test host own windows cannot be
    /// affected — the point is that the subscription and its pump are real, not that they
    /// suppress anything here.</para>
    ///
    /// <para>It also times <c>Start</c>. The defect this replaced could only ever return by
    /// burning its full timeout, so an activation that completes in milliseconds is
    /// evidence the handshake really is a handshake and not a discarded wait — the previous
    /// version of this test asserted <c>Subscribed</c> after the fact and would have passed
    /// against that defect.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheWin32SubscriptionInstallsPumpsAndTearsDownDeterministically()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only subscription.");

        var ticks = 0;
        var surfaced = 0;
        var monitor = new Win32OwnedWindowSurfaceMonitor(TimeSpan.FromMilliseconds(10));
        try
        {
            var watch = Stopwatch.StartNew();
            monitor.Start(
                Environment.ProcessId,
                () => Interlocked.Increment(ref surfaced),
                () => Interlocked.Increment(ref ticks));
            watch.Stop();

            Assert.True(monitor.Subscribed, "SetWinEventHook did not install.");
            Assert.True(
                watch.Elapsed < TimeSpan.FromSeconds(1),
                $"activation took {watch.ElapsedMilliseconds} ms — it is waiting out a " +
                "deadline rather than being acknowledged.");
            Assert.True(
                SpinUntil(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(10)),
                $"the pump thread ticked {Volatile.Read(ref ticks)} times");
        }
        finally
        {
            monitor.Dispose();
        }

        Assert.False(monitor.Subscribed);
        var afterDispose = Volatile.Read(ref ticks);
        Assert.False(
            SpinUntil(
                () => Volatile.Read(ref ticks) > afterDispose,
                TimeSpan.FromMilliseconds(300)),
            "the pump kept ticking after disposal");

        // Disposal is idempotent, because reveal-then-shutdown reaches it twice.
        monitor.Dispose();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheWin32SubscriptionRefusesToBeStartedTwice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only subscription.");

        using var monitor = new Win32OwnedWindowSurfaceMonitor(TimeSpan.FromMilliseconds(50));
        monitor.Start(Environment.ProcessId, () => { }, () => { });

        Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Environment.ProcessId, () => { }, () => { }));
    }

    // ── The activation handshake ─────────────────────────────────────────────

    /// <summary>
    /// Activation is a handshake, and this is the half that was missing: <c>Start</c> must
    /// not return until the pump has reported that the hook is installed.
    ///
    /// <para>The defect it replaces waited for that acknowledgement while holding the lock
    /// the pump needed to publish it, so the wait could only ever time out — and its result
    /// was discarded. Activation therefore "succeeded" with no subscription in place, and
    /// <c>ApplicationStart</c> could begin in exactly the unguarded window the hook exists
    /// to cover. Deleting the acknowledgement check fails here.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ActivationDoesNotReturnUntilTheSubscriptionIsInstalled()
    {
        var hook = new ControlledHook();
        using var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(50),
            hook.Install,
            hook.Remove,
            TimeSpan.FromSeconds(10));

        var activation = Task.Run(() => monitor.Start(Owned.Pid, () => { }, () => { }));

        // The install is still in flight, so activation must still be blocked and the
        // monitor must not be claiming a subscription it does not have.
        Assert.False(
            activation.Wait(TimeSpan.FromMilliseconds(250)),
            "Start returned before the subscription was installed.");
        Assert.False(monitor.Subscribed);

        hook.Release.Set();

        Assert.True(activation.Wait(TimeSpan.FromSeconds(10)), "Start never returned.");
        Assert.True(monitor.Subscribed);
        Assert.Equal(Owned.Pid, hook.ProcessId);
    }

    /// <summary>
    /// A zero hook is a failure, not a fallback. Running the backstop timer on its own would
    /// be sampling-only suppression — the mechanism #20 measured the ~234 ms and ~462 ms
    /// flickers through — presented as a working guard.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AZeroHookFailsActivationRatherThanDegradingToSampling()
    {
        var ticks = 0;
        var hook = new ControlledHook { Result = nint.Zero };
        hook.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            hook.Install,
            hook.Remove,
            TimeSpan.FromSeconds(10));

        var error = Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Owned.Pid, () => { }, () => Interlocked.Increment(ref ticks)));

        Assert.Contains("SetWinEventHook", error.Message, StringComparison.Ordinal);
        Assert.False(monitor.Subscribed);
        Assert.False(monitor.PumpAlive);

        // And the backstop never ran, despite an interval short enough to have ticked many
        // times over the assertions above.
        Assert.Equal(0, Volatile.Read(ref ticks));
        Assert.Equal(0, hook.Removes);
    }

    /// <summary>
    /// An acknowledgement that never arrives fails activation too, and the late hook the
    /// pump eventually installs is removed rather than orphaned.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnInstallThatMissesTheDeadlineFailsActivationAndLeavesNothingBehind()
    {
        var hook = new ControlledHook { InstallDelay = TimeSpan.FromMilliseconds(400) };
        hook.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(50),
            hook.Install,
            hook.Remove,
            TimeSpan.FromMilliseconds(50));

        var error = Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Owned.Pid, () => { }, () => { }));

        Assert.Contains("did not report installation", error.Message, StringComparison.Ordinal);
        Assert.False(monitor.Subscribed);
        Assert.False(monitor.PumpAlive);
        Assert.Equal([hook.Result], hook.Removed);
    }

    /// <summary>
    /// Failed activation tears the pump down deterministically: the thread is gone, the
    /// hook is removed, and the monitor cannot be started again or leak a second thread on
    /// a later dispose.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void FailedActivationTearsThePumpDownAndStaysTerminal()
    {
        var hook = new ControlledHook { Result = nint.Zero };
        hook.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            hook.Install,
            hook.Remove,
            TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Owned.Pid, () => { }, () => { }));

        Assert.False(monitor.PumpAlive);
        Assert.Equal(1, hook.Installs);

        // Terminal: disposal is a no-op rather than a second teardown, and a retry is
        // refused rather than starting another pump over a monitor that already failed.
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => monitor.Start(Owned.Pid, () => { }, () => { }));
        Assert.Equal(1, hook.Installs);
    }

    /// <summary>
    /// And the guard propagates that failure rather than constructing over a monitor that
    /// never subscribed — so the launcher cleanup envelope stops the owned process.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AGuardCannotBeBuiltOverAMonitorThatFailedToSubscribe()
    {
        var hook = new ControlledHook { Result = nint.Zero };
        hook.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            hook.Install,
            hook.Remove,
            TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(() => new ManagedEtabsWindowGuard(
            new FakeOwnedProcess(Owned),
            new FakeWindows(),
            Policy(new VirtualClock()),
            monitor));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static ManagedEtabsWindowPolicy Policy(VirtualClock clock) => new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        clock);

    private static ManagedEtabsWindowGuard Guard(
        FakeWindows windows,
        out FakeOwnedProcess owned,
        out VirtualClock clock) => Guard(windows, out owned, out clock, out _);

    private static ManagedEtabsWindowGuard Guard(
        FakeWindows windows,
        out FakeOwnedProcess owned,
        out VirtualClock clock,
        out FakeMonitor monitor)
    {
        owned = new FakeOwnedProcess(Owned);
        clock = new VirtualClock();
        monitor = new FakeMonitor();
        return new ManagedEtabsWindowGuard(owned, windows, Policy(clock), monitor);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return condition();
    }

    /// <summary>
    /// The two user32 hook calls, under the test control. Everything the activation
    /// contract does is control flow around these, so this is the seam that makes the
    /// contract falsifiable without waiting on a real window station to misbehave.
    /// </summary>
    private sealed class ControlledHook
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public nint Result { get; init; } = 0x1234;

        public TimeSpan InstallDelay { get; init; }

        public int Installs { get; private set; }

        public int Removes => Removed.Count;

        public List<nint> Removed { get; } = [];

        public int? ProcessId { get; private set; }

        public nint Install(int processId, Win32OwnedWindowSurfaceMonitor.WinEventProc proc)
        {
            Installs++;
            ProcessId = processId;
            _ = Release.Wait(TimeSpan.FromSeconds(30));
            if (InstallDelay > TimeSpan.Zero)
            {
                Thread.Sleep(InstallDelay);
            }

            return Result;
        }

        public void Remove(nint hook)
        {
            lock (Removed)
            {
                Removed.Add(hook);
            }
        }
    }

    private sealed class VirtualClock : IManagedEtabsClock
    {
        public TimeSpan Elapsed { get; private set; }

        public long Timestamp => Elapsed.Ticks;

        public TimeSpan ElapsedSince(long timestamp) =>
            TimeSpan.FromTicks(Elapsed.Ticks - timestamp);

        public void Wait(TimeSpan interval) => Elapsed += interval;
    }

    /// <summary>Delivery the test drives by hand, so event and backstop are separable.</summary>
    private sealed class FakeMonitor : IOwnedWindowSurfaceMonitor
    {
        private Action? _surfaced;
        private Action? _tick;

        public int StartCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int? ProcessId { get; private set; }
        public bool Subscribed { get; private set; }

        public void Start(int processId, Action onSurfaced, Action onBackstopTick)
        {
            StartCalls++;
            ProcessId = processId;
            _surfaced = onSurfaced;
            _tick = onBackstopTick;
            Subscribed = true;
        }

        public void RaiseSurfaced() => _surfaced?.Invoke();

        public void RaiseBackstopTick() => _tick?.Invoke();

        public void Dispose()
        {
            DisposeCalls++;
            Subscribed = false;
        }
    }

    private sealed class FakeOwnedProcess(ManagedProcessIdentity identity) : IOwnedEtabsProcess
    {
        public ManagedProcessIdentity Identity { get; } = identity;
        public bool HasExited { get; private set; }
        public void Exit() => HasExited = true;
        public void Kill() => HasExited = true;
        public bool WaitForExit(TimeSpan timeout) => HasExited;
        public void Dispose()
        {
        }
    }

    private sealed class FakeWindows : ITopLevelWindows
    {
        private readonly List<TopLevelWindow> _windows;
        private readonly Dictionary<nint, int> _hideRequests = [];
        private readonly object _gate = new();

        public FakeWindows(params TopLevelWindow[] windows) => _windows = [.. windows];

        /// <summary>A window that refuses to go down, so a failed confirmation is expressible.</summary>
        public bool StaysVisible { get; init; }

        /// <summary>
        /// How many hide requests a window absorbs before it actually goes down. ShowWindow
        /// against another process is not synchronous, so this is the normal case, not an
        /// exotic one.
        /// </summary>
        public int HidesAfterRequests { get; init; } = 1;

        public Exception? EnumerateException { get; init; }

        public List<nint> Hidden { get; } = [];

        public List<nint> Shown { get; } = [];

        public IEnumerable<nint> Touched => Hidden.Concat(Shown);

        public IReadOnlyList<TopLevelWindow> Enumerate()
        {
            if (EnumerateException is not null)
            {
                throw EnumerateException;
            }

            lock (_gate)
            {
                return [.. _windows];
            }
        }

        public void Hide(nint handle)
        {
            lock (_gate)
            {
                Hidden.Add(handle);
                if (StaysVisible)
                {
                    return;
                }

                _hideRequests[handle] = _hideRequests.GetValueOrDefault(handle) + 1;
                if (_hideRequests[handle] >= HidesAfterRequests)
                {
                    Replace(handle, visible: false);
                }
            }
        }

        public void Show(nint handle)
        {
            lock (_gate)
            {
                Shown.Add(handle);
                Replace(handle, visible: true);
            }
        }

        public bool IsVisible(nint handle)
        {
            lock (_gate)
            {
                return _windows.Any(window => window.Handle == handle && window.IsVisible);
            }
        }

        /// <summary>A new top-level window of a process appears on screen.</summary>
        public void Surface(nint handle, int processId)
        {
            lock (_gate)
            {
                _windows.Add(new(handle, processId, IsVisible: true));
            }
        }

        /// <summary>Hands a live handle value to a different process, as Windows may.</summary>
        public void Reassign(nint handle, int processId)
        {
            lock (_gate)
            {
                for (var index = 0; index < _windows.Count; index++)
                {
                    if (_windows[index].Handle == handle)
                    {
                        _windows[index] = _windows[index] with { ProcessId = processId };
                    }
                }
            }
        }

        /// <summary>Destroys a window, so its handle stops appearing in the census.</summary>
        public void Destroy(nint handle)
        {
            lock (_gate)
            {
                _ = _windows.RemoveAll(window => window.Handle == handle);
            }
        }

        private void Replace(nint handle, bool visible)
        {
            for (var index = 0; index < _windows.Count; index++)
            {
                if (_windows[index].Handle == handle)
                {
                    _windows[index] = _windows[index] with { IsVisible = visible };
                }
            }
        }
    }
}
