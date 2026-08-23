// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The Windows visibility layer — after CLI #22, an OBSERVER and a certifier, never an
/// actuator.
///
/// <para>Seven supervised ETABS 23.3 runs settled what this layer may do. Four runs with
/// out-of-process <c>ShowWindow(SW_HIDE)</c> active all killed ETABS with an unhandled
/// <c>NullReferenceException</c> inside its own <c>NativeWindow.Callback</c>; the
/// controlled arm that removed the actuation survived and exported cleanly. Meanwhile the
/// CSI actions were measured working in both directions — the hide landing within ~5 ms,
/// ~16 ms and ~0.5 s of the call across three runs, and the reveal 14 ms into an
/// <c>Unhide</c> with <c>ShowWindow</c> impossible in either direction. So CSI mutates and
/// Windows observes.</para>
///
/// <para>Its entire remaining risk is targeting and truthfulness: reading a window it does
/// not own, or certifying a state that was not so. Both are what this suite is for.</para>
/// </summary>
public sealed class ManagedEtabsWindowGuardTests
{
    private static readonly ManagedProcessIdentity Owned = new(
        4242,
        new DateTimeOffset(2026, 8, 21, 5, 12, 12, TimeSpan.Zero),
        @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe");

    private const int ForeignPid = 4243;

    /// <summary>A 1920x1080 desktop at the origin — the layout the live runs measured on.</summary>
    private static readonly WindowBounds Desktop = new(0, 0, 1920, 1080);

    /// <summary>A full-screen window, as ETABS's main frame was observed: -8,-8,1928,1040.</summary>
    private static readonly WindowBounds FullScreen = new(-8, -8, 1928, 1040);

    /// <summary>
    /// ETABS's own Analysis Monitor, exactly as Diagnostic #4 observed it: IsWindowVisible
    /// true, but parked at x=32767 where no pixel of it can reach the engineer.
    /// </summary>
    private static readonly WindowBounds OffScreen = new(32767, 234, 33407, 703);

    // ── There is no actuator ─────────────────────────────────────────────────

    /// <summary>
    /// The strongest statement this suite can make, and it is a structural one: the
    /// Windows seam has exactly one member and it is a question.
    ///
    /// <para>Re-adding a <c>Hide</c> or <c>Show</c> to <see cref="ITopLevelWindows"/> — the
    /// first move anyone would make to "just put the window back" — fails here before any
    /// behaviour is even exercised.</para>
    /// </summary>
    [Fact]
    public void TheWindowsSeamExposesNoActuatorAtAll()
    {
        var members = typeof(ITopLevelWindows)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .ToArray();

        Assert.Equal(["Enumerate"], members);
    }

    /// <summary>
    /// And the production implementation cannot reach <c>user32!ShowWindow</c> even
    /// indirectly, because it no longer declares it. A P/Invoke left behind "for later" is
    /// an actuator one line away from being reachable again.
    /// </summary>
    [Fact]
    public void TheProductionWindowsImplementationDeclaresNoShowWindowPInvoke()
    {
        var declared = typeof(Win32TopLevelWindows)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("ShowWindow", declared);
        Assert.Contains("IsWindowVisible", declared);
        Assert.Contains("GetWindowRect", declared);
    }

    // ── What counts as being on screen ───────────────────────────────────────

    /// <summary>
    /// The CLI #24 predicate, stated directly. A raw <c>IsWindowVisible</c> is NOT the
    /// violation test: Diagnostic #4 caught ETABS's own Analysis Monitor reporting visible
    /// for 15 ms while sitting entirely beyond the right edge of the desktop. Counting that
    /// would fail healthy sessions on every run.
    /// </summary>
    [Theory]
    // visible, on screen -> material
    [InlineData(true, -8, -8, 1928, 1040, true)]
    // visible, but entirely off the right edge (the real Analysis Monitor rectangle)
    [InlineData(true, 32767, 234, 33407, 703, false)]
    // visible, but degenerate
    [InlineData(true, 100, 100, 100, 400, false)]
    // visible, but minimized where Windows parks them
    [InlineData(true, -32000, -32000, -31840, -31975, false)]
    // hidden, wherever it is
    [InlineData(false, 0, 0, 800, 600, false)]
    // visible, straddling the left edge -> still material, part of it is on screen
    [InlineData(true, -200, 100, 40, 400, true)]
    public void MaterialExposureIsVisibleAndNonEmptyAndOnTheDesktop(
        bool visible,
        int left,
        int top,
        int right,
        int bottom,
        bool expected)
    {
        var window = new TopLevelWindow(
            (nint)0x1,
            Owned.Pid,
            visible,
            new WindowBounds(left, top, right, bottom));

        Assert.Equal(expected, ManagedEtabsWindowExposure.IsMaterial(window, Desktop));
    }

    // ── Targeting ────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole targeting rule: a window of any other process is never counted, in either
    /// direction, however visible it is.
    /// </summary>
    [Fact]
    public void NoWindowOfAnyOtherProcessDecidesEitherConfirmation()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x10, ForeignPid, true, FullScreen),
            new TopLevelWindow((nint)0x11, ForeignPid, true, FullScreen));
        using var guard = Guard(windows, out _, out _);

        // Nothing owned is on screen, so suppression is confirmed despite two foreign
        // windows being wide open.
        Assert.True(guard.ConfirmSuppressed().Confirmed);

        // And a reveal cannot be satisfied by somebody else's window.
        Assert.False(guard.ConfirmRevealed().Confirmed);
    }

    /// <summary>
    /// A pid is only provably ours while the authoritative handle stops Windows recycling
    /// it. Once the process exits the observer stops for good rather than reading a pid
    /// that now belongs to a stranger.
    /// </summary>
    [Fact]
    public void AnExitedOwnedProcessRetiresTheObserverInsteadOfReadingItsPid()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out var owned, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        windows.Surface((nint)0x20, Owned.Pid, FullScreen);

        owned.Exit();
        guard.ObserveOnce();

        Assert.False(guard.IsActive);
        Assert.Equal(ManagedEtabsVisibilityState.Retired, guard.State);
        Assert.False(guard.Exposure.Observed);
    }

    /// <summary>An exited process is reported as gone, not silently confirmed either way.</summary>
    [Fact]
    public void AnExitedOwnedProcessIsReportedGoneRatherThanConfirmed()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out var owned, out _);
        owned.Exit();

        var confirmation = guard.ConfirmSuppressed();

        Assert.False(confirmation.Confirmed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.OwnedProcessGone,
            confirmation.Diagnostic,
            StringComparison.Ordinal);
    }

    // ── Windows-authoritative confirmation ───────────────────────────────────

    /// <summary>
    /// Suppression is confirmed from the owned census alone. CSI is not consulted here and
    /// there is no seam through which it could be — #20 measured <c>cOAPI.Visible()</c>
    /// stuck true for 94 reads across 10.014 s while the windows were in fact hidden.
    /// </summary>
    [Fact]
    public void SuppressionIsConfirmedFromTheOwnedCensusWithNoReferenceToCsi()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x30, Owned.Pid, false, FullScreen));
        using var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmSuppressed();

        Assert.True(confirmation.Confirmed);
        Assert.Empty(confirmation.ObservedWindows);
    }

    /// <summary>
    /// An owned window that stays materially on screen fails the gate at the deadline, and
    /// the diagnostic names the offending handle rather than saying "not confirmed".
    /// </summary>
    [Fact]
    public void AnOwnedWindowThatStaysOnScreenFailsSuppressionAtTheDeadline()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x31, Owned.Pid, true, FullScreen));
        using var guard = Guard(windows, out _, out var clock);

        var confirmation = guard.ConfirmSuppressed();

        Assert.False(confirmation.Confirmed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
            confirmation.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("0x31", confirmation.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
    }

    /// <summary>
    /// A window that goes down a few observations later is still confirmed. A CSI
    /// transition is not synchronous for us, so a single read after the call would prove
    /// nothing either way.
    /// </summary>
    [Fact]
    public void AWindowThatTakesSeveralObservationsToGoDownIsStillConfirmed()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x32, Owned.Pid, true, FullScreen))
        {
            GoesHiddenAfterEnumerations = 3
        };
        using var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmSuppressed();

        Assert.True(confirmation.Confirmed);
        Assert.True(confirmation.Observations >= 3);
    }

    /// <summary>
    /// The off-screen helper does not satisfy a REVEAL either. "Open in ETABS" that leaves
    /// a window parked beyond the desktop has not shown the engineer anything.
    /// </summary>
    [Fact]
    public void AnOffScreenOwnedWindowCannotSatisfyAReveal()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x33, Owned.Pid, true, OffScreen));
        using var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmRevealed();

        Assert.False(confirmation.Confirmed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.RevealNotConfirmed,
            confirmation.Diagnostic,
            StringComparison.Ordinal);
    }

    /// <summary>And a materially on-screen owned window does satisfy it.</summary>
    [Fact]
    public void RevealIsConfirmedOnlyWhenAnOwnedWindowIsMateriallyOnScreen()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x34, Owned.Pid, true, FullScreen));
        using var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmRevealed();

        Assert.True(confirmation.Confirmed);
        Assert.Equal([(nint)0x34], confirmation.ObservedWindows);
    }

    // ── CLI #24: the evidence is temporal and sticky ─────────────────────────

    /// <summary>
    /// THE #24 defect, as a regression. An exposure at t1 followed by a hidden census at
    /// t3 must still fail: a point-in-time question cannot un-happen what the engineer
    /// already saw.
    ///
    /// <para>This is not hypothetical either. A prior candidate logged "ETABS started
    /// hidden" truthfully at 16:13, 8.76 s after a full-screen ETABS window had been in
    /// front of the engineer — because the gate asked "is it hidden now?".</para>
    /// </summary>
    [Fact]
    public void AnExposureAfterBackgroundHiddenIsStickyAndSurvivesALaterHiddenCensus()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);

        // t0: the consent interval closes.
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        Assert.False(guard.Exposure.Observed);

        // t1: an owned window is materially on screen.
        windows.Surface((nint)0x40, Owned.Pid, FullScreen);
        guard.ObserveOnce();
        Assert.True(guard.Exposure.Observed);

        // t2: it goes away again.
        windows.SetVisible((nint)0x40, visible: false);
        guard.ObserveOnce();

        // t3: the census now says hidden — and the evidence still says otherwise.
        Assert.True(guard.ConfirmSuppressed().Confirmed);
        Assert.True(guard.Exposure.Observed);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.UnconsentedExposure,
            guard.Exposure.Describe(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Exposure BEFORE the consent interval closes is not a violation. That is the whole
    /// point of the new contract: the engineer was told ETABS would appear, so it appearing
    /// is expected rather than a breach.
    /// </summary>
    [Fact]
    public void ExposureDuringTheConsentedStartupIsNotRecordedAsAViolation()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x41, Owned.Pid, true, FullScreen));
        using var guard = Guard(windows, out _, out _);

        Assert.Equal(ManagedEtabsVisibilityState.StartingVisibleByConsent, guard.State);
        guard.ObserveOnce();

        Assert.False(guard.Exposure.Observed);
    }

    /// <summary>
    /// The off-screen helper is observed but is NOT a violation — the precision CLI #24
    /// asked for after Diagnostic #4. Making the predicate a raw IsWindowVisible turns this
    /// red, which is the point.
    /// </summary>
    [Fact]
    public void AnOffScreenVisibleHelperNeverCountsAsUnconsentedExposure()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x42, Owned.Pid, OffScreen);
        guard.ObserveOnce();

        Assert.False(guard.Exposure.Observed);
        Assert.True(guard.ConfirmSuppressed().Confirmed);
    }

    /// <summary>The evidence names the offender: which window, where, and when.</summary>
    [Fact]
    public void TheEvidenceRecordsTheFirstAndLastExposureWithBounds()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out var clock);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x43, Owned.Pid, FullScreen);
        guard.ObserveOnce();
        clock.Wait(TimeSpan.FromMilliseconds(250));
        guard.ObserveOnce();

        var evidence = guard.Exposure;
        Assert.True(evidence.Observed);
        Assert.Equal(2, evidence.Observations);
        Assert.Equal((nint)0x43, evidence.First!.Value.Handle);
        Assert.Equal(FullScreen, evidence.First!.Value.Bounds);
        Assert.Equal(0, evidence.First!.Value.SinceProtectedMs);
        Assert.Equal(250, evidence.Last!.Value.SinceProtectedMs);
    }

    /// <summary>
    /// The certification race, stated as a test.
    ///
    /// <para>Evidence accumulates from WinEvent callbacks delivered on the monitor's pump
    /// thread. A window can therefore be materially on screen while the accumulated
    /// evidence still reads clean, because nobody has observed yet. A certification that
    /// only READS - as this one did - would clear a session that is in front of the
    /// engineer at that very instant.</para>
    ///
    /// <para>No <c>ObserveOnce</c> here on purpose: the window surfaces and the
    /// certification is asked immediately, which is the ordering the daemon actually
    /// hits when a command finishes.</para>
    /// </summary>
    [Fact]
    public void CertifyingForcesACensusAndSeesAWindowNoEventHasReportedYet()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x51, Owned.Pid, FullScreen);

        // What a plain read sees: nothing, because no observation has run.
        Assert.False(guard.Exposure.Observed);

        var certified = guard.CertifyExposure();

        Assert.True(certified.Observed);
        Assert.Equal((nint)0x51, certified.First!.Value.Handle);
    }

    /// <summary>
    /// And the opposite half, which a census alone cannot do: a window that surfaced and
    /// was gone again before the certification runs is still a breach. The engineer saw it.
    /// Temporal evidence and a forced census are both required; neither is sufficient.
    /// </summary>
    [Fact]
    public void CertifyingStillReportsAnExposureThatIsAlreadyOffScreenAgain()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out var clock);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x52, Owned.Pid, FullScreen);
        guard.ObserveOnce();
        clock.Wait(TimeSpan.FromMilliseconds(120));
        windows.SetVisible((nint)0x52, visible: false);

        var certified = guard.CertifyExposure();

        Assert.True(certified.Observed);
        Assert.Equal((nint)0x52, certified.First!.Value.Handle);
    }

    /// <summary>
    /// A census that cannot run must not be returned as clean evidence.
    ///
    /// <para>The confirmation loop can answer "not confirmed" because its caller reads a
    /// flag. This method returns EVIDENCE, and no value of that evidence honestly means "I
    /// could not look" - so it propagates instead, and the session above it decides. The
    /// sweep error is still recorded, for the same diagnostics the confirmation loop
    /// feeds.</para>
    /// </summary>
    [Fact]
    public void ACensusThatThrowsIsNotReportedAsCleanEvidence()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        var failure = new InvalidOperationException("EnumWindows failed: 0x5");
        windows.EnumerateException = failure;

        var thrown = Assert.Throws<InvalidOperationException>(() => guard.CertifyExposure());

        Assert.Same(failure, thrown);
        Assert.Same(failure, guard.LastSweepError);
    }

    /// <summary>A session that was never on screen certifies clean, so the gate is not always-on.</summary>
    [Fact]
    public void CertifyingAHiddenSessionReportsNoExposure()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        Assert.False(guard.CertifyExposure().Observed);
    }

    /// <summary>
    /// A foreign window on screen during the protected interval is not our exposure. The
    /// engineer seeing Excel is not an ETABS contract breach.
    /// </summary>
    [Fact]
    public void AForeignWindowOnScreenIsNeverRecordedAsOurExposure()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x44, ForeignPid, FullScreen);
        guard.ObserveOnce();

        Assert.False(guard.Exposure.Observed);
    }

    // ── The state machine ────────────────────────────────────────────────────

    /// <summary>
    /// A session starts in the consented-startup state, because by the time this observer
    /// exists the caller has already declared intent and the process has been created.
    /// </summary>
    [Fact]
    public void AGuardBeginsInTheConsentedStartupState()
    {
        using var guard = Guard(new FakeWindows(), out _, out _);

        Assert.Equal(ManagedEtabsVisibilityState.StartingVisibleByConsent, guard.State);
    }

    /// <summary>
    /// UserVisible is ABSORBING. Later background work reusing the session must never walk
    /// it back into a hidden state — the engineer asked to see ETABS and nothing on the
    /// command path is allowed to take that away.
    /// </summary>
    [Fact]
    public void AUserVisibleSessionCannotBeWalkedBackIntoBackgroundHidden()
    {
        using var guard = Guard(new FakeWindows(), out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        guard.EnterUserVisible();

        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        Assert.Equal(ManagedEtabsVisibilityState.UserVisible, guard.State);
    }

    /// <summary>
    /// And once the session is UserVisible, an on-screen window is exactly what was asked
    /// for — it must not be accumulated as unconsented exposure.
    /// </summary>
    [Fact]
    public void ExposureIsNotRecordedOnceTheUserHasBeenShownEtabs()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        guard.EnterUserVisible();

        windows.Surface((nint)0x50, Owned.Pid, FullScreen);
        guard.ObserveOnce();

        Assert.False(guard.Exposure.Observed);
    }

    // ── Observation, event-driven and backstopped ────────────────────────────

    /// <summary>
    /// The subscription is armed for the exact owned pid before the observer can be used,
    /// so the ApplicationStart interval — the one #20 measured a window through — is
    /// already being watched.
    /// </summary>
    [Fact]
    public void TheSubscriptionIsArmedForTheExactOwnedPidBeforeTheGuardIsUsable()
    {
        using var guard = Guard(new FakeWindows(), out _, out _, out var monitor);

        Assert.Equal(1, monitor.StartCalls);
        Assert.Equal(Owned.Pid, monitor.ProcessId);
        Assert.True(guard.Subscribed);
    }

    /// <summary>A window event prompts an observation, with no backstop tick involved.</summary>
    [Fact]
    public void AWindowSurfacingIsObservedOnTheEventWithNoInterveningBackstopTick()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x60, Owned.Pid, FullScreen);
        monitor.RaiseSurfaced();

        Assert.True(guard.Exposure.Observed);
        Assert.Equal(1, guard.EventPasses);
        Assert.Equal(0, guard.BackstopPasses);
    }

    /// <summary>And the backstop catches anything the event missed.</summary>
    [Fact]
    public void TheBackstopObservesEvenWhenNoEventArrives()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x61, Owned.Pid, FullScreen);
        monitor.RaiseBackstopTick();

        Assert.True(guard.Exposure.Observed);
        Assert.Equal(0, guard.EventPasses);
        Assert.Equal(1, guard.BackstopPasses);
    }

    /// <summary>
    /// Modal and secondary owned top-level windows are in scope. #20's exposure included a
    /// splash that was not the main frame.
    /// </summary>
    [Fact]
    public void SecondaryAndModalOwnedTopLevelWindowsAreObservedToo()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        windows.Surface((nint)0x70, Owned.Pid, new WindowBounds(413, 206, 1507, 825));
        guard.ObserveOnce();

        Assert.True(guard.Exposure.Observed);
        Assert.Equal((nint)0x70, guard.Exposure.First!.Value.Handle);
    }

    /// <summary>
    /// An observation failure is recorded rather than thrown or ignored. A transient
    /// window-station failure must not take the daemon down, and the bounded confirmation
    /// reports it truthfully.
    /// </summary>
    [Fact]
    public void AnObservationFailureIsRecordedAndReportedRatherThanThrownOrIgnored()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        // The window station starts failing only once the session is under observation,
        // which is the case that matters: a transient failure DURING protected background
        // work must be recorded, not swallowed.
        windows.EnumerateException = new InvalidOperationException("boom");
        monitor.RaiseBackstopTick();

        Assert.NotNull(guard.LastSweepError);

        var confirmation = guard.ConfirmSuppressed();
        Assert.False(confirmation.Confirmed);
        Assert.Contains(
            "ITopLevelWindows.Enumerate",
            confirmation.Diagnostic,
            StringComparison.Ordinal);
    }

    // ── The latch ────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposal tears the subscription down deterministically.
    /// </summary>
    [Fact]
    public void DisposingTheObserverDisposesTheSubscription()
    {
        var guard = Guard(new FakeWindows(), out _, out _, out var monitor);

        guard.Dispose();

        Assert.Equal(1, monitor.DisposeCalls);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// But BEGINNING a reveal must NOT. The observer has to survive the CSI call, because
    /// a reveal that fails leaves the session in an unknown on-screen state — and the
    /// previous shape retired the observer first, so a failed reveal produced a still-ready
    /// session with nothing watching it at all.
    /// </summary>
    [Fact]
    public void BeginningARevealKeepsTheSubscriptionAlive()
    {
        var guard = Guard(new FakeWindows(), out _, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        guard.BeginExplicitReveal();

        Assert.Equal(0, monitor.DisposeCalls);
        Assert.True(guard.IsActive);
        Assert.Equal(ManagedEtabsVisibilityState.RevealPending, guard.State);
    }

    /// <summary>
    /// And a CONFIRMED reveal is what finally retires it: the engineer is looking at ETABS
    /// deliberately, so there is nothing left to protect.
    /// </summary>
    [Fact]
    public void AConfirmedRevealRetiresTheObserverAndDisposesTheSubscription()
    {
        var guard = Guard(new FakeWindows(), out _, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        guard.BeginExplicitReveal();

        guard.EnterUserVisible();

        Assert.Equal(ManagedEtabsVisibilityState.UserVisible, guard.State);
        Assert.Equal(1, monitor.DisposeCalls);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// Terminating twice re-arms nothing and disposes once. Reveal-then-shutdown reaches
    /// this path twice in a normal session.
    /// </summary>
    [Fact]
    public void TerminatingTwiceDisposesOnceAndReArmsNothing()
    {
        var guard = Guard(new FakeWindows(), out _, out _, out var monitor);

        guard.BeginExplicitReveal();
        guard.Dispose();

        Assert.Equal(1, monitor.DisposeCalls);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// After the interval is released, nothing accumulates. A reveal the engineer asked for
    /// must not be recorded as the exposure this evidence exists to catch.
    /// </summary>
    [Fact]
    public void NoExposureIsRecordedAfterTheIntervalIsReleased()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        guard.BeginExplicitReveal();

        windows.Surface((nint)0x80, Owned.Pid, FullScreen);
        guard.ObserveOnce();

        Assert.False(guard.Exposure.Observed);
    }

    // ── Contract shape ───────────────────────────────────────────────────────

    /// <summary>
    /// The only way to obtain an observer is with an authoritative owned handle. There is
    /// no overload taking a bare pid, so an unproven or global ETABS process cannot be
    /// observed at all.
    /// </summary>
    [Fact]
    public void TheOnlyWayToArmAnObserverIsWithAnAuthoritativeOwnedHandle()
    {
        var activate = typeof(IManagedEtabsWindowGuardFactory)
            .GetMethod(nameof(IManagedEtabsWindowGuardFactory.Activate))!;

        var parameter = Assert.Single(activate.GetParameters());
        Assert.Equal(typeof(IOwnedEtabsProcess), parameter.ParameterType);
    }

    [Fact]
    public void TheObserverReportsTheIdentityOfTheHandleItWasGiven()
    {
        using var guard = Guard(new FakeWindows(), out var owned, out _);

        Assert.Equal(owned.Identity, guard.Identity);
        Assert.Equal(Owned, guard.Identity);
    }


    // ── Repairs found in exact-head review ───────────────────────────────────

    /// <summary>
    /// DEFECT 2. The complete production reveal sequence, against a REAL guard.
    ///
    /// <para>The session drives retire → CSI Unhide → Windows confirm → EnterUserVisible.
    /// The previous shape made the first step set the same flag the last step checked, so
    /// <c>EnterUserVisible</c> returned immediately and a successful reveal could never
    /// actually reach <c>UserVisible</c>. Every session-level test missed it, because the
    /// fake application simply assigned the state.</para>
    /// </summary>
    [Fact]
    public void TheFullProductionRevealSequenceReachesUserVisibleOnARealGuard()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out _, out _);
        Assert.True(guard.ConfirmSuppressedAndCloseConsentInterval().Confirmed);

        // 1. the observer stops accumulating, but stays alive
        guard.BeginExplicitReveal();

        // 2. CSI puts the window back (modelled: ETABS shows it)
        windows.Surface((nint)0x90, Owned.Pid, FullScreen);

        // 3. Windows certifies
        Assert.True(guard.ConfirmRevealed().Confirmed);

        // 4. and only now is the session UserVisible
        guard.EnterUserVisible();

        Assert.Equal(ManagedEtabsVisibilityState.UserVisible, guard.State);
        Assert.False(guard.Exposure.Observed);
    }

    /// <summary>
    /// DEFECT 4, structurally. The consent interval can ONLY be closed by the census that
    /// justifies it — there is no separate member to call, so the gap in which a WinEvent
    /// could be judged against the state we were about to leave cannot be reintroduced by
    /// re-ordering two calls.
    /// </summary>
    [Fact]
    public void TheConsentIntervalCannotBeClosedSeparatelyFromTheCensusThatJustifiesIt()
    {
        var members = typeof(IManagedEtabsWindowGuard)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .ToArray();

        Assert.DoesNotContain("EnterBackgroundHidden", members);
        Assert.Contains(
            nameof(IManagedEtabsWindowGuard.ConfirmSuppressedAndCloseConsentInterval),
            members);
    }

    /// <summary>
    /// DEFECT 4, behaviourally. The transition is already in force the instant the
    /// confirmation returns — the caller never has to make a second call, so there is no
    /// window between them for an exposure to be discarded in.
    /// </summary>
    [Fact]
    public void TheConsentIntervalIsAlreadyClosedWhenTheConfirmationReturns()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);

        var confirmation = guard.ConfirmSuppressedAndCloseConsentInterval();

        Assert.True(confirmation.Confirmed);
        Assert.Equal(ManagedEtabsVisibilityState.BackgroundHidden, guard.State);

        // An exposure landing immediately afterwards is therefore already accumulating.
        windows.Surface((nint)0x91, Owned.Pid, FullScreen);
        guard.ObserveOnce();
        Assert.True(guard.Exposure.Observed);
    }

    /// <summary>A census that never goes clean does not close the interval either.</summary>
    [Fact]
    public void AFailedConfirmationLeavesTheConsentIntervalOpen()
    {
        var windows = new FakeWindows(
            new TopLevelWindow((nint)0x92, Owned.Pid, true, FullScreen));
        using var guard = Guard(windows, out _, out _);

        Assert.False(guard.ConfirmSuppressedAndCloseConsentInterval().Confirmed);
        Assert.Equal(ManagedEtabsVisibilityState.StartingVisibleByConsent, guard.State);
    }

    /// <summary>
    /// DEFECT 6. An owned process that exits retires the observer from inside an
    /// observation pass. Disposal is owed regardless, and exactly once.
    ///
    /// <para>The previous shape shared one flag between "logically retired" and "monitor
    /// disposed", so the exit swallowed the disposal and the hook and its pump outlived the
    /// session — defeating the deterministic teardown contract the monitor was repaired
    /// for.</para>
    /// </summary>
    [Fact]
    public void AProcessExitStillLeavesTheSubscriptionOwedAndDisposedExactlyOnce()
    {
        var windows = new FakeWindows();
        var guard = Guard(windows, out var owned, out _, out var monitor);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        owned.Exit();
        guard.ObserveOnce();
        Assert.False(guard.IsActive);
        Assert.Equal(0, monitor.DisposeCalls);

        guard.Dispose();
        Assert.Equal(1, monitor.DisposeCalls);

        // And a second disposal is still exactly once.
        guard.Dispose();
        Assert.Equal(1, monitor.DisposeCalls);
    }

    /// <summary>
    /// The #24 bounded metrics: a stretch that ends is folded into the totals, and a run
    /// still open when the evidence is read is counted without being closed by the read.
    /// </summary>
    [Fact]
    public void TheEvidenceMeasuresTotalAndLongestContiguousExposure()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out var clock);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();

        // a 100 ms stretch
        windows.Surface((nint)0xA0, Owned.Pid, FullScreen);
        guard.ObserveOnce();
        clock.Wait(TimeSpan.FromMilliseconds(100));
        guard.ObserveOnce();

        // then it goes away
        windows.SetVisible((nint)0xA0, visible: false);
        clock.Wait(TimeSpan.FromMilliseconds(50));
        guard.ObserveOnce();

        // then a longer 300 ms stretch
        windows.SetVisible((nint)0xA0, visible: true);
        guard.ObserveOnce();
        clock.Wait(TimeSpan.FromMilliseconds(300));
        guard.ObserveOnce();

        var evidence = guard.Exposure;
        Assert.True(evidence.Observed);
        Assert.Equal(400, evidence.ObservedTotalVisibleMs);
        Assert.Equal(300, evidence.ObservedMaxContiguousVisibleMs);
        Assert.Equal(450, evidence.ObservationDurationMs);
    }

    /// <summary>An observation carries the stage it happened in, not only a timestamp.</summary>
    [Fact]
    public void AnExposureRecordsWhichStageItHappenedIn()
    {
        var windows = new FakeWindows();
        using var guard = Guard(windows, out _, out _);
        _ = guard.ConfirmSuppressedAndCloseConsentInterval();
        guard.MarkStage("snapshot-export");

        windows.Surface((nint)0xA1, Owned.Pid, FullScreen);
        guard.ObserveOnce();

        Assert.Equal("snapshot-export", guard.Exposure.First!.Value.Stage);
        Assert.Contains(
            "firstStage=snapshot-export",
            guard.Exposure.Describe(),
            StringComparison.Ordinal);
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
            monitor,
            new FakeDesktop(Desktop)));
    }

    // ── Teardown against an install that has not returned ────────────────────

    /// <summary>
    /// An install still running at the acknowledgement deadline fails activation, and the
    /// hook it eventually obtains is removed by the thread that installed it.
    ///
    /// <para>The install here returns only once retirement has been latched, so it
    /// provably outlives the deadline rather than being raced against a sleep. Teardown
    /// still joins it, so this is the resolved half of the pair: the failure is reported
    /// without an unresolved-cleanup claim, nothing stays subscribed, and — the part that
    /// matters — the backstop never ran on its own.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnInstallStillRunningAtTheAcknowledgementDeadlineFailsAndLeavesNothingArmed()
    {
        var ticks = 0;
        var installer = new LateHookInstaller();
        installer.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            installer.Install,
            installer.Remove,
            TimeSpan.FromMilliseconds(250),
            Generous);
        installer.HoldUntil = () => monitor.IsRetired;

        var error = Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Owned.Pid, () => { }, () => Interlocked.Increment(ref ticks)));

        Assert.True(
            installer.Entered.IsSet,
            "the pump never reached SetWinEventHook, so this proves nothing about a late install.");
        Assert.Contains("did not report installation", error.Message, StringComparison.Ordinal);
        Assert.Equal(OwnedWindowMonitorTeardown.Resolved, monitor.Teardown);
        Assert.DoesNotContain("UNRESOLVED", error.Message, StringComparison.Ordinal);

        Assert.False(monitor.Subscribed);
        Assert.False(monitor.PumpAlive);
        Assert.Equal([installer.Result], installer.Snapshot());
        Assert.Null(monitor.TeardownError);
        Assert.Equal(0, Volatile.Read(ref ticks));
    }

    /// <summary>
    /// And the half the exact-head review found missing: an install that has not returned
    /// when the TEARDOWN JOIN expires either.
    ///
    /// <para>The old shape discarded that join's result, so this state was reported as a
    /// clean failed teardown while a thread of ours was still inside <c>SetWinEventHook</c>.
    /// It must not be. Retirement has taken effect — nothing can be claimed, pumped or
    /// delivered — but "disarmed" and "gone" are different facts, and the release gate is
    /// entitled to the true one. A teardown path that reports success here fails this
    /// test.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnInstallThatOutlivesTheTeardownJoinIsReportedRatherThanClaimedClean()
    {
        var run = StartOverAnInstallThatOutlivesBothDeadlines();
        try
        {
            Assert.True(run.Monitor.PumpAlive, "the pump exited, so nothing outlived the join.");
            Assert.Equal(OwnedWindowMonitorTeardown.PumpStillAlive, run.Monitor.Teardown);
            Assert.Contains("UNRESOLVED", run.Failure.Message, StringComparison.Ordinal);

            // Unresolved is not the same as degraded: nothing is subscribed and the backstop
            // is not running on its own behind the failure.
            Assert.False(run.Monitor.Subscribed);
            Assert.Equal(0, run.Ticks);
        }
        finally
        {
            run.Finish();
        }
    }

    /// <summary>
    /// The blocker, stated as a resource rule: a teardown that could not join its pump must
    /// not close the handle that pump can still reach.
    ///
    /// <para>Disposing it there is not a leak-shaped defect, it is a correctness one — the
    /// late pump would go on to wait on a closed, and eventually recycled, kernel handle.
    /// Signalling and disposal are therefore separate: teardown signals, and the handle is
    /// released by whichever of the two leaves last.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AFailedActivationDoesNotCloseTheStopHandleUnderALivePump()
    {
        var run = StartOverAnInstallThatOutlivesBothDeadlines();
        try
        {
            Assert.True(run.Monitor.PumpAlive, "the pump exited, so there is nothing to protect.");
            Assert.False(
                run.Monitor.StopSignalClosed,
                "teardown closed the stop handle while its own pump was still running.");
        }
        finally
        {
            run.Finish();
        }

        // Held only as long as it is reachable, then released — not leaked either.
        Assert.False(run.Monitor.PumpAlive);
        Assert.True(run.Monitor.StopSignalClosed, "the stop handle was never released.");
    }

    /// <summary>
    /// And when the delayed install finally returns, the pump that issued it takes its own
    /// hook back off and arms nothing on its way out.
    ///
    /// <para>Every survivor the exact-head review named is checked here: the hook is
    /// removed exactly once, no subscription is published, the backstop never pumps on its
    /// own, and the callback the operating system holds a pointer to refuses even when
    /// invoked directly with an event it would otherwise act on. That last one is what
    /// stops a late delivery reaching a pid whose ownership has already ended.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ALateInstallIsRemovedByItsOwnPumpAndArmsNothing()
    {
        var run = StartOverAnInstallThatOutlivesBothDeadlines();
        run.Finish();

        Assert.Equal([run.Installer.Result], run.Installer.Snapshot());
        Assert.Null(run.Monitor.TeardownError);
        Assert.False(run.Monitor.Subscribed);
        Assert.Equal(0, run.Ticks);

        run.Installer.Callback!.Invoke(
            hWinEventHook: run.Installer.Result,
            eventType: EventObjectShow,
            hwnd: 0x99,
            idObject: 0,
            idChild: 0,
            idEventThread: 0,
            dwmsEventTime: 0);

        Assert.Equal(0, run.Surfaced);
    }

    /// <summary>
    /// An unresolved teardown is still terminal. It is not a retryable state, and a later
    /// disposal must not start a second teardown over the pump that is still finishing.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AMonitorStaysTerminalAfterAnUnresolvedTeardown()
    {
        var run = StartOverAnInstallThatOutlivesBothDeadlines();
        try
        {
            Assert.True(run.Monitor.IsRetired);

            run.Monitor.Dispose();
            Assert.Equal(OwnedWindowMonitorTeardown.PumpStillAlive, run.Monitor.Teardown);
            Assert.False(run.Monitor.StopSignalClosed, "a second teardown released the handle again.");

            _ = Assert.Throws<ObjectDisposedException>(
                () => run.Monitor.Start(Owned.Pid, () => { }, () => { }));
        }
        finally
        {
            run.Finish();
        }

        Assert.Equal(1, run.Installer.Installs);
        Assert.Equal([run.Installer.Result], run.Installer.Snapshot());
    }

    /// <summary>
    /// The other direction, so the unresolved report cannot be a constant: a failure with
    /// nothing installed has nothing to outlive anything, and is reported as the joined,
    /// fully released teardown it is.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AZeroHookFailureStillTearsDownAndReportsItResolved()
    {
        var installer = new LateHookInstaller { Result = nint.Zero };
        installer.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            installer.Install,
            installer.Remove,
            Generous,
            Generous);

        var error = Assert.Throws<InvalidOperationException>(
            () => monitor.Start(Owned.Pid, () => { }, () => { }));

        Assert.Contains("SetWinEventHook", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UNRESOLVED", error.Message, StringComparison.Ordinal);
        Assert.Equal(OwnedWindowMonitorTeardown.Resolved, monitor.Teardown);
        Assert.False(monitor.PumpAlive);
        Assert.True(monitor.StopSignalClosed);
        Assert.Empty(installer.Snapshot());
    }

    /// <summary>
    /// And a successful activation is unchanged by all of it: the subscription is published
    /// before <c>Start</c> returns, the real message-pumping loop runs the backstop, the
    /// callback delivers while the monitor is live, and disposal joins the pump, removes
    /// the hook once, releases the handle and disarms the callback.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ASuccessfulActivationSubscribesPumpsAndTearsDownResolved()
    {
        var surfaced = 0;
        var ticks = 0;
        var installer = new LateHookInstaller();
        installer.Release.Set();
        var monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            installer.Install,
            installer.Remove,
            Generous,
            Generous);

        monitor.Start(
            Owned.Pid,
            () => Interlocked.Increment(ref surfaced),
            () => Interlocked.Increment(ref ticks));

        Assert.True(monitor.Subscribed);
        Assert.Equal(Owned.Pid, installer.ProcessId);
        Assert.Equal(OwnedWindowMonitorTeardown.NotTornDown, monitor.Teardown);
        Assert.False(monitor.StopSignalClosed);
        Assert.True(
            SpinUntil(() => Volatile.Read(ref ticks) >= 2, Generous),
            "the real message loop never reached the backstop.");

        // Live, the callback does the thing it exists for.
        installer.Callback!.Invoke(0, EventObjectShow, 0x99, 0, 0, 0, 0);
        Assert.Equal(1, Volatile.Read(ref surfaced));

        monitor.Dispose();

        Assert.Equal(OwnedWindowMonitorTeardown.Resolved, monitor.Teardown);
        Assert.False(monitor.Subscribed);
        Assert.False(monitor.PumpAlive);
        Assert.True(monitor.StopSignalClosed);
        Assert.Equal([installer.Result], installer.Snapshot());

        // Retired, the same callback refuses.
        installer.Callback!.Invoke(0, EventObjectShow, 0x99, 0, 0, 0, 0);
        Assert.Equal(1, Volatile.Read(ref surfaced));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>How long a bounded wait may take before the test itself is the failure.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    /// <summary>The <c>EVENT_OBJECT_SHOW</c> the guard's callback is meant to act on.</summary>
    private const uint EventObjectShow = 0x8002;

    /// <summary>
    /// Drives an activation whose <c>SetWinEventHook</c> call is STILL RUNNING when both
    /// deadlines expire, and hands the pieces back.
    ///
    /// <para>Nothing here is timed against a sleep. The install blocks on a gate this
    /// method holds, so "the install outlives the teardown join" is arranged by
    /// construction; the two deadlines are short because they are the thing being exceeded,
    /// and the run asserts that the pump really did reach the install rather than passing
    /// on a pump that never started.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static LateInstallRun StartOverAnInstallThatOutlivesBothDeadlines()
    {
        var run = new LateInstallRun();
        run.Monitor = new Win32OwnedWindowSurfaceMonitor(
            TimeSpan.FromMilliseconds(5),
            run.Installer.Install,
            run.Installer.Remove,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(50));

        run.Failure = Assert.Throws<InvalidOperationException>(
            () => run.Monitor.Start(Owned.Pid, run.CountSurfaced, run.CountTick));

        Assert.True(
            run.Installer.Entered.IsSet,
            "the pump never reached SetWinEventHook, so this run proves nothing about a " +
            "late install.");
        return run;
    }

    /// <summary>One failed activation over an install the test still holds open.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class LateInstallRun
    {
        private int _surfaced;
        private int _ticks;

        public LateHookInstaller Installer { get; } = new();

        public Win32OwnedWindowSurfaceMonitor Monitor { get; set; } = null!;

        public InvalidOperationException Failure { get; set; } = null!;

        public int Surfaced => Volatile.Read(ref _surfaced);

        public int Ticks => Volatile.Read(ref _ticks);

        public void CountSurfaced() => Interlocked.Increment(ref _surfaced);

        public void CountTick() => Interlocked.Increment(ref _ticks);

        /// <summary>Lets the held install return, and waits for that pump to finish.</summary>
        public void Finish()
        {
            Installer.Release.Set();
            Assert.True(Installer.Removed.Wait(Generous), "the late hook was never removed.");
            Assert.True(SpinUntil(() => !Monitor.PumpAlive, Generous), "the pump never exited.");
        }
    }

    /// <summary>
    /// The two user32 hook calls with the RETURN of the install under test control — the one
    /// thing teardown cannot cancel, and therefore the seam the late-install contract has to
    /// be proven over.
    ///
    /// <para><see cref="Entered"/> reports that the pump is inside <c>SetWinEventHook</c>.
    /// The call then returns only once <see cref="HoldUntil"/> comes true and
    /// <see cref="Release"/> is set, so a test states when the install returns instead of
    /// hoping a sleep lands on the right side of a deadline.</para>
    /// </summary>
    private sealed class LateHookInstaller
    {
        private int _installs;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public ManualResetEventSlim Removed { get; } = new(false);

        public nint Result { get; init; } = 0x5150;

        /// <summary>An extra condition the install waits for before it returns.</summary>
        public Func<bool>? HoldUntil { get; set; }

        public int Installs => Volatile.Read(ref _installs);

        public int? ProcessId { get; private set; }

        public Win32OwnedWindowSurfaceMonitor.WinEventProc? Callback { get; private set; }

        private List<nint> RemovedHooks { get; } = [];

        public nint Install(int processId, Win32OwnedWindowSurfaceMonitor.WinEventProc proc)
        {
            _ = Interlocked.Increment(ref _installs);
            ProcessId = processId;
            Callback = proc;
            Entered.Set();

            if (HoldUntil is not null)
            {
                var spin = new SpinWait();
                var watch = Stopwatch.StartNew();
                while (!HoldUntil() && watch.Elapsed < Generous)
                {
                    spin.SpinOnce();
                }
            }

            _ = Release.Wait(Generous);
            return Result;
        }

        public void Remove(nint hook)
        {
            lock (RemovedHooks)
            {
                RemovedHooks.Add(hook);
            }

            Removed.Set();
        }

        public nint[] Snapshot()
        {
            lock (RemovedHooks)
            {
                return [.. RemovedHooks];
            }
        }
    }

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
        return new ManagedEtabsWindowGuard(
            owned,
            windows,
            Policy(clock),
            monitor,
            new FakeDesktop(Desktop));
    }

    /// <summary>
    /// A stated monitor layout. Injected rather than read from the machine so a test can
    /// never accidentally measure exposure against the developer's real screen — which
    /// would make the off-screen cases pass or fail depending on who ran them.
    /// </summary>
    private sealed class FakeDesktop(WindowBounds bounds) : IVirtualDesktop
    {
        public WindowBounds Bounds { get; } = bounds;
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

    /// <summary>
    /// A window station under test control.
    ///
    /// <para>It has no Hide and no Show, because the production seam has none. Windows
    /// change state here only because the TEST says so — which is the honest model now that
    /// CSI, not this layer, is what moves ETABS on and off the screen.</para>
    /// </summary>
    private sealed class FakeWindows : ITopLevelWindows
    {
        private readonly List<TopLevelWindow> _windows;
        private readonly object _gate = new();
        private int _enumerations;

        public FakeWindows(params TopLevelWindow[] windows) => _windows = [.. windows];

        /// <summary>
        /// After how many censuses the owned windows go hidden on their own. Models a CSI
        /// transition landing a few observations after the call, which is the normal case:
        /// Hide() and Unhide() are not synchronous for us.
        /// </summary>
        public int GoesHiddenAfterEnumerations { get; init; }

        public Exception? EnumerateException { get; set; }

        public int Enumerations => _enumerations;

        public IReadOnlyList<TopLevelWindow> Enumerate()
        {
            if (EnumerateException is not null)
            {
                throw EnumerateException;
            }

            lock (_gate)
            {
                _enumerations++;
                if (GoesHiddenAfterEnumerations > 0 && _enumerations >= GoesHiddenAfterEnumerations)
                {
                    for (var index = 0; index < _windows.Count; index++)
                    {
                        _windows[index] = _windows[index] with { IsVisible = false };
                    }
                }

                return [.. _windows];
            }
        }

        /// <summary>A new top-level window of a process appears on screen.</summary>
        public void Surface(nint handle, int processId, WindowBounds bounds)
        {
            lock (_gate)
            {
                _windows.Add(new(handle, processId, IsVisible: true, bounds));
            }
        }

        /// <summary>ETABS takes a window down, or puts it back, of its own accord.</summary>
        public void SetVisible(nint handle, bool visible)
        {
            lock (_gate)
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
    }
}
