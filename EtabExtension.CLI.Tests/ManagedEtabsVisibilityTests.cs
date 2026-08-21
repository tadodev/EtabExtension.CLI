// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The CSI visibility contract, exercised without ETABS or COM.
///
/// <para>Cardex (ETABS 23.3) is explicit that <c>cOAPI.Hide</c> and <c>cOAPI.Unhide</c>
/// return an error when the application is already in the requested state, so a policy
/// that called them unconditionally would manufacture failures. It is equally explicit
/// that <c>cOAPI.ApplicationStart</c> takes no visibility argument, so nothing here may
/// assume a starting state — every transition is read first and confirmed after.</para>
/// </summary>
public sealed class ManagedEtabsVisibilityTests
{
    [Fact]
    public void HidingAVisibleApplicationIssuesHideAndConfirmsTheNewState()
    {
        var api = new FakeVisibilityApi(visible: true);

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.Equal(ManagedEtabsVisibilityIntent.Hidden, outcome.Intent);
        Assert.True(outcome.Confirmed);
        Assert.True(outcome.Changed);
        Assert.Null(outcome.Diagnostic);
        Assert.Equal(1, api.HideCalls);
        Assert.Equal(0, api.UnhideCalls);
        // Read, transition, read again: the second read is what turns "the call was
        // accepted" into "the application is hidden".
        Assert.Equal(["visible", "hide", "visible"], api.Events);
    }

    /// <summary>
    /// Cardex, <c>cOAPI.Hide</c>: "If the application is already hidden, calling this
    /// function returns an error." Calling anyway would report a failure for the state
    /// we wanted.
    /// </summary>
    [Fact]
    public void HidingAnAlreadyHiddenApplicationCallsNothingAndStillSucceeds()
    {
        var api = new FakeVisibilityApi(visible: false);

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.True(outcome.Confirmed);
        Assert.False(outcome.Changed);
        Assert.Equal(0, api.HideCalls);
        Assert.Equal(["visible"], api.Events);
    }

    [Fact]
    public void RevealingAHiddenApplicationIssuesUnhideAndConfirmsTheNewState()
    {
        var api = new FakeVisibilityApi(visible: false);

        var outcome = ManagedEtabsVisibility.EnsureVisible(api, Converging());

        Assert.Equal(ManagedEtabsVisibilityIntent.Visible, outcome.Intent);
        Assert.True(outcome.Confirmed);
        Assert.True(outcome.Changed);
        Assert.Equal(1, api.UnhideCalls);
        Assert.Equal(0, api.HideCalls);
        Assert.Equal(["visible", "unhide", "visible"], api.Events);
    }

    /// <summary>
    /// Cardex, <c>cOAPI.Unhide</c>: "If the application is already visible (not hidden)
    /// calling this function returns an error."
    /// </summary>
    [Fact]
    public void RevealingAnAlreadyVisibleApplicationCallsNothingAndStillSucceeds()
    {
        var api = new FakeVisibilityApi(visible: true);

        var outcome = ManagedEtabsVisibility.EnsureVisible(api, Converging());

        Assert.True(outcome.Confirmed);
        Assert.False(outcome.Changed);
        Assert.Equal(0, api.UnhideCalls);
        Assert.Equal(["visible"], api.Events);
    }

    [Theory]
    [InlineData(true, "cOAPI.Hide")]
    [InlineData(false, "cOAPI.Unhide")]
    public void ANonZeroTransitionReturnIsReportedAgainstTheNamedCsiCall(
        bool startVisible,
        string expectedOperation)
    {
        var api = new FakeVisibilityApi(startVisible) { TransitionReturnCode = 7 };

        var outcome = startVisible
            ? ManagedEtabsVisibility.EnsureHidden(api, Converging())
            : ManagedEtabsVisibility.EnsureVisible(api, Converging());

        Assert.False(outcome.Confirmed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(EtabsApiErrorCodes.ApiCallFailed, outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains($"operation={expectedOperation}", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("returnCode=7", outcome.Diagnostic, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same failure class <c>cFile.OpenFile</c> shipped with: a zero return that the
    /// state contradicts. Reporting success here would be the whole defect back again,
    /// because a background run would believe it was hidden while a window sat on screen.
    /// </summary>
    [Fact]
    public void AZeroReturnThatTheStateContradictsIsNotConfirmed()
    {
        var api = new FakeVisibilityApi(visible: true) { IgnoreTransition = true };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.False(outcome.Confirmed);
        Assert.True(outcome.Changed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            outcome.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("requested=hidden", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("observed=visible", outcome.Diagnostic, StringComparison.Ordinal);
    }

    /// <summary>
    /// The #20 measurement, stated as the primitive's contract: <c>Hide()</c> returns
    /// success and <c>Visible()</c> keeps saying visible for a while. The candidate called
    /// that a failure on the first read. It is a deferred transition, and the only correct
    /// answer is to keep observing to a bound.
    /// </summary>
    [Fact]
    public void ASuccessfulHideThatTakesEffectLaterEventuallyConfirms()
    {
        var clock = new VirtualClock();
        var api = new FakeVisibilityApi(visible: true) { VisibleReadsBeforeHideLands = 20 };

        var outcome = ManagedEtabsVisibility.EnsureHidden(
            api,
            new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), clock));

        Assert.True(outcome.Confirmed);
        Assert.True(outcome.Changed);
        Assert.Null(outcome.Diagnostic);
        // Twenty reads still said visible, one poll interval apart, and the twenty-first
        // agreed. The candidate declared failure after the first of those.
        Assert.Equal(22, outcome.Reads);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), outcome.Waited);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), clock.Elapsed);
    }

    /// <summary>
    /// And the transition is issued ONCE. Cardex documents <c>Hide</c> erroring when the
    /// application is already hidden, so re-issuing it while a first call is still landing
    /// would manufacture exactly the failure the convergence exists to avoid.
    /// </summary>
    [Fact]
    public void ConvergencePollsTheStateAndNeverReIssuesTheTransition()
    {
        var api = new FakeVisibilityApi(visible: true) { VisibleReadsBeforeHideLands = 30 };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.True(outcome.Confirmed);
        Assert.Equal(1, api.HideCalls);
        Assert.Equal(0, api.UnhideCalls);
        Assert.Equal(1, api.Events.Count(step => step == "hide"));
        Assert.Equal(outcome.Reads, api.Events.Count(step => step == "visible"));
    }

    /// <summary>
    /// The bound is a bound. A hide that never lands is still a refusal, and the diagnostic
    /// carries the measurements — reads and waited — the supervised gate cannot otherwise
    /// reconstruct.
    /// </summary>
    [Fact]
    public void AHideThatNeverLandsFailsAtTheDeadlineWithItsMeasurements()
    {
        var clock = new VirtualClock();
        var api = new FakeVisibilityApi(visible: true) { IgnoreTransition = true };

        var outcome = ManagedEtabsVisibility.EnsureHidden(
            api,
            new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), clock));

        Assert.False(outcome.Confirmed);
        Assert.Equal(1, api.HideCalls);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Elapsed);
        Assert.Contains(
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            outcome.Diagnostic!,
            StringComparison.Ordinal);
        Assert.Contains($"reads={outcome.Reads}", outcome.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("waitedMs=10000", outcome.Diagnostic!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-zero return does not get the convergence budget. Cardex's documented cause of
    /// a non-zero <c>Hide</c> is "already hidden", so it is re-read once — the observation
    /// decides — and a disagreeing observation reports the return code rather than waiting
    /// out a call ETABS refused.
    /// </summary>
    [Fact]
    public void ARejectedTransitionIsReadOnceAndNotWaitedOut()
    {
        var clock = new VirtualClock();
        var api = new FakeVisibilityApi(visible: true) { TransitionReturnCode = 7 };

        var outcome = ManagedEtabsVisibility.EnsureHidden(
            api,
            new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), clock));

        Assert.False(outcome.Confirmed);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.Empty(clock.Waits);
        Assert.Equal(2, outcome.Reads);
    }

    /// <summary>
    /// The same read, when the state agrees. A non-zero return whose documented meaning is
    /// "already in the requested state" must not be reported as a failure to reach it.
    /// </summary>
    [Fact]
    public void ARejectedTransitionThatTheStateAgreesWithIsConfirmed()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            TransitionReturnCode = 7,
            HidesDespiteNonZeroReturn = true
        };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.True(outcome.Confirmed);
        Assert.Null(outcome.Diagnostic);
        Assert.Equal(1, api.HideCalls);
    }

    [Fact]
    public void AThrowingVisibilityReadIsBoundedAndNeverThrowsOutOfThePolicy()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            ReadException = new InvalidOperationException("COM went away")
        };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.False(outcome.Confirmed);
        Assert.False(outcome.Changed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            EtabsApiErrorCodes.ComOperationFailed,
            outcome.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains(
            $"operation={ManagedEtabsVisibility.ReadOperation}",
            outcome.Diagnostic,
            StringComparison.Ordinal);
        Assert.True(outcome.Diagnostic!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    [Fact]
    public void AThrowingTransitionIsBoundedAndNeverThrowsOutOfThePolicy()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            TransitionException = new InvalidOperationException("RPC_E_DISCONNECTED")
        };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api, Converging());

        Assert.False(outcome.Confirmed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            $"operation={ManagedEtabsVisibility.HideOperation}",
            outcome.Diagnostic,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The production convergence policy on virtual time: the same deadline and interval
    /// the daemon ships with, and no real sleeping.
    /// </summary>
    private static ManagedEtabsVisibilityPolicy Converging() => new(
        ManagedEtabsVisibilityPolicy.Default.ConvergenceDeadline,
        ManagedEtabsVisibilityPolicy.Default.PollInterval,
        new VirtualClock());

    internal sealed class VirtualClock : IManagedEtabsClock
    {
        private readonly DateTimeOffset _origin = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        public VirtualClock() => UtcNow = _origin;

        public DateTimeOffset UtcNow { get; private set; }

        public List<TimeSpan> Waits { get; } = [];

        public TimeSpan Elapsed => UtcNow - _origin;

        public void Wait(TimeSpan interval)
        {
            Waits.Add(interval);
            UtcNow = UtcNow.Add(interval);
        }
    }

    internal sealed class FakeVisibilityApi(bool visible) : IEtabsVisibilityApi
    {
        public List<string> Events { get; } = [];
        public int HideCalls { get; private set; }
        public int UnhideCalls { get; private set; }
        public int TransitionReturnCode { get; init; }
        public bool IgnoreTransition { get; init; }

        /// <summary>
        /// Reads that keep reporting the OLD state after an accepted transition — the #20
        /// measurement, where Hide() returned success and Visible() disagreed for seconds.
        /// </summary>
        public int VisibleReadsBeforeHideLands { get; init; }

        /// <summary>A refused call that nevertheless left the state where it was wanted.</summary>
        public bool HidesDespiteNonZeroReturn { get; init; }

        private int _pendingDeferredReads;
        public Exception? ReadException { get; init; }
        public Exception? TransitionException { get; init; }
        public bool IsVisible { get; private set; } = visible;

        public bool Visible()
        {
            Events.Add("visible");
            if (ReadException is not null)
            {
                throw ReadException;
            }
            if (_pendingDeferredReads > 0)
            {
                _pendingDeferredReads--;
                if (_pendingDeferredReads == 0)
                {
                    IsVisible = false;
                }
                return true;
            }

            return IsVisible;
        }

        public int Hide()
        {
            Events.Add("hide");
            HideCalls++;
            if (TransitionException is not null)
            {
                throw TransitionException;
            }
            if (TransitionReturnCode != 0)
            {
                if (HidesDespiteNonZeroReturn)
                {
                    IsVisible = false;
                }
                return TransitionReturnCode;
            }

            if (IgnoreTransition)
            {
                return 0;
            }
            if (VisibleReadsBeforeHideLands > 0)
            {
                _pendingDeferredReads = VisibleReadsBeforeHideLands;
                return 0;
            }

            IsVisible = false;
            return TransitionReturnCode;
        }

        public int Unhide()
        {
            Events.Add("unhide");
            UnhideCalls++;
            if (TransitionException is not null)
            {
                throw TransitionException;
            }
            if (TransitionReturnCode == 0 && !IgnoreTransition)
            {
                IsVisible = true;
            }
            return TransitionReturnCode;
        }
    }
}
