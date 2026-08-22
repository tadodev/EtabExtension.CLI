// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The CSI visibility contract, exercised without ETABS or COM.
///
/// <para><b>The contract inverted after CLI #22.</b> The previous policy read
/// <c>cOAPI.Visible()</c> first and skipped the transition when the flag already matched
/// the intent — defensible while the flag was believed, because Cardex documents
/// <c>Hide</c>/<c>Unhide</c> returning an error when already in the requested state. Live
/// certification destroyed that premise: #20 measured the flag stuck true for 94 reads
/// across 10.014 s while the windows were in fact hidden, and Diagnostic #4 saw it wrong
/// in the other direction. A read-first policy therefore declines to act at exactly the
/// moment acting matters.</para>
///
/// <para>So both transitions are now issued UNCONDITIONALLY, the return code is telemetry
/// rather than a verdict, and only a THROW counts as a failure — because a throw means the
/// call never happened. The exact-owned Windows census decides what actually took
/// effect.</para>
/// </summary>
public sealed class ManagedEtabsVisibilityTests
{
    // ── The transition is unconditional ──────────────────────────────────────

    /// <summary>
    /// The single most important property of this policy, and the one whose absence shipped
    /// a rejected candidate: the hide is issued even when CSI insists the application is
    /// already hidden.
    ///
    /// <para>This is not hypothetical. #20's supervised run had <c>Visible()</c> returning
    /// true for 10 s after a successful hide — the flag simply does not track. A policy
    /// that consults it first issues no hide on precisely the builds where the hide is
    /// needed. Restoring the read-first guard fails here.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHideIsIssuedWhateverTheVisibleFlagClaims(bool flagSaysVisible)
    {
        var api = new FakeVisibilityApi(flagSaysVisible);

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.Equal(1, api.HideCalls);
        Assert.Equal(0, api.UnhideCalls);
        Assert.True(outcome.Issued);
        Assert.Equal(ManagedEtabsVisibilityIntent.Hidden, outcome.Intent);
    }

    /// <summary>
    /// The same, in the other direction. The stuck flag is why the old reveal had to put
    /// windows back with <c>ShowWindow</c> at all: the CSI policy read "already visible"
    /// and issued nothing. Diagnostic #4 proved an unconditional <c>Unhide</c> is
    /// sufficient on its own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheUnhideIsIssuedWhateverTheVisibleFlagClaims(bool flagSaysVisible)
    {
        var api = new FakeVisibilityApi(flagSaysVisible);

        var outcome = ManagedEtabsVisibility.ApplyVisible(api);

        Assert.Equal(1, api.UnhideCalls);
        Assert.Equal(0, api.HideCalls);
        Assert.True(outcome.Issued);
        Assert.Equal(ManagedEtabsVisibilityIntent.Visible, outcome.Intent);
    }

    /// <summary>
    /// Exactly once. A retry loop here would be the old convergence policy creeping back in
    /// through a different door, and Cardex is clear that a second call against a state
    /// ETABS believes it is already in returns an error.
    /// </summary>
    [Fact]
    public void TheTransitionIsIssuedExactlyOnceAndNeverRetried()
    {
        var api = new FakeVisibilityApi(visible: true) { TransitionReturnCode = 7 };

        _ = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.Equal(1, api.HideCalls);
        Assert.Single(api.Events.Where(e => string.Equals(e, "hide", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The flag is read for the record, not for a decision — and never before the call, so
    /// it cannot influence one. One read, because there is nothing to poll.
    /// </summary>
    [Fact]
    public void VisibleIsReadOnceAfterTheCallAndNeverBeforeIt()
    {
        var api = new FakeVisibilityApi(visible: true);

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.Equal(["hide", "visible"], api.Events);
        Assert.NotNull(outcome.CsiVisibleAfter);
    }

    // ── Return codes are telemetry, throws are failures ──────────────────────

    /// <summary>
    /// A non-zero return means ETABS considered the request and declined it — most often
    /// because it believed it was already in that state. The call DID happen, so the census
    /// still has something to certify and this is not a transition failure.
    /// </summary>
    [Fact]
    public void ANonZeroReturnIsRecordedButIsStillAnIssuedTransition()
    {
        var api = new FakeVisibilityApi(visible: true) { TransitionReturnCode = 42 };

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.True(outcome.Issued);
        Assert.False(outcome.Confirmed);
        Assert.Equal(42, outcome.ReturnCode);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains("returnCode=42", outcome.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            ManagedEtabsVisibility.HideOperation,
            outcome.Diagnostic,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A throw is categorically different: nothing was asked of ETABS. Diagnostic #3
    /// measured exactly this — <c>cOAPI.Hide()</c> throwing a <c>NullReferenceException</c>
    /// ~12 ms in when called before <c>ApplicationStart</c> returns, because the API object
    /// exists before its WinForms control has a window handle. The caller must fail, not
    /// wait for a census to confirm a transition that was never requested.
    /// </summary>
    [Fact]
    public void AThrowingTransitionIsReportedAsNotIssuedAndNeverEscapes()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            TransitionException = new InvalidOperationException(
                "Invoke or BeginInvoke cannot be called on a control until the window " +
                "handle has been created.")
        };

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.False(outcome.Issued);
        Assert.False(outcome.Confirmed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            ManagedEtabsVisibility.HideOperation,
            outcome.Diagnostic,
            StringComparison.Ordinal);

        // The flag is not consulted after a call that did not happen.
        Assert.Null(outcome.CsiVisibleAfter);
    }

    /// <summary>
    /// The telemetry read failing does not fail the transition. The call was made; only the
    /// commentary is missing.
    /// </summary>
    [Fact]
    public void AThrowingVisibleReadDoesNotFailAnIssuedTransition()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            ReadException = new InvalidOperationException("COM read failed")
        };

        var outcome = ManagedEtabsVisibility.ApplyVisible(api);

        Assert.True(outcome.Issued);
        Assert.True(outcome.Confirmed);
        Assert.Null(outcome.CsiVisibleAfter);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            ManagedEtabsVisibility.ReadOperation,
            outcome.Diagnostic,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The nominal case is quiet. A diagnostic on every successful hide would bury the ones
    /// that matter in a daemon log.
    /// </summary>
    [Fact]
    public void AnAcceptedTransitionThatCsiAgreesWithSaysNothing()
    {
        var api = new FakeVisibilityApi(visible: true) { TransitionLandsInFlag = true };

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.True(outcome.Issued);
        Assert.True(outcome.Confirmed);
        Assert.False(outcome.CsiVisibleAfter);
        Assert.Null(outcome.Diagnostic);
    }

    /// <summary>
    /// And the #20 shape — accepted, but the flag keeps lying — is recorded rather than
    /// hidden, because that is the build we actually ship against.
    /// </summary>
    [Fact]
    public void AnAcceptedHideWhoseFlagKeepsLyingIsRecordedNotFailed()
    {
        var api = new FakeVisibilityApi(visible: true);

        var outcome = ManagedEtabsVisibility.ApplyHidden(api);

        Assert.True(outcome.Issued);
        Assert.True(outcome.Confirmed);
        Assert.True(outcome.CsiVisibleAfter);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            outcome.Diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("census decides", outcome.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApiArgumentIsRequired()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => ManagedEtabsVisibility.ApplyHidden(null!));
        _ = Assert.Throws<ArgumentNullException>(
            () => ManagedEtabsVisibility.ApplyVisible(null!));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    internal sealed class VirtualClock : IManagedEtabsClock
    {
        /// <summary>
        /// Virtual MONOTONIC time. Deliberately not a date: the production clock exposes a
        /// timestamp and an elapsed-since precisely so that no deadline can be computed from
        /// a wall clock, and a fake that offered a "now" would let one back in.
        /// </summary>
        public TimeSpan Elapsed { get; private set; }

        public List<TimeSpan> Waits { get; } = [];

        public long Timestamp => Elapsed.Ticks;

        public TimeSpan ElapsedSince(long timestamp) =>
            TimeSpan.FromTicks(Elapsed.Ticks - timestamp);

        public void Wait(TimeSpan interval)
        {
            Waits.Add(interval);
            Elapsed += interval;
        }
    }

    /// <summary>
    /// The three CSI calls under test control, with the ETABS 23.3 behaviour that matters
    /// as the DEFAULT: the flag does not move when the transition is accepted. A fake that
    /// helpfully updated it would quietly hide the very defect this policy exists for.
    /// </summary>
    internal sealed class FakeVisibilityApi(bool visible) : IEtabsVisibilityApi
    {
        public List<string> Events { get; } = [];

        public int HideCalls { get; private set; }

        public int UnhideCalls { get; private set; }

        public int TransitionReturnCode { get; init; }

        /// <summary>Opt in to a build where CSI's flag actually tracks its own transition.</summary>
        public bool TransitionLandsInFlag { get; init; }

        public Exception? ReadException { get; init; }

        public Exception? TransitionException { get; init; }

        public bool IsVisible { get; private set; } = visible;

        public bool Visible()
        {
            Events.Add("visible");
            return ReadException is null ? IsVisible : throw ReadException;
        }

        public int Hide()
        {
            Events.Add("hide");
            HideCalls++;
            if (TransitionException is not null)
            {
                throw TransitionException;
            }

            if (TransitionLandsInFlag)
            {
                IsVisible = false;
            }

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

            if (TransitionLandsInFlag)
            {
                IsVisible = true;
            }

            return TransitionReturnCode;
        }
    }
}
