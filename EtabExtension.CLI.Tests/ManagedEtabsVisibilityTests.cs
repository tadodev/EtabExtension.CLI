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

        var outcome = ManagedEtabsVisibility.EnsureHidden(api);

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

        var outcome = ManagedEtabsVisibility.EnsureHidden(api);

        Assert.True(outcome.Confirmed);
        Assert.False(outcome.Changed);
        Assert.Equal(0, api.HideCalls);
        Assert.Equal(["visible"], api.Events);
    }

    [Fact]
    public void RevealingAHiddenApplicationIssuesUnhideAndConfirmsTheNewState()
    {
        var api = new FakeVisibilityApi(visible: false);

        var outcome = ManagedEtabsVisibility.EnsureVisible(api);

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

        var outcome = ManagedEtabsVisibility.EnsureVisible(api);

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
            ? ManagedEtabsVisibility.EnsureHidden(api)
            : ManagedEtabsVisibility.EnsureVisible(api);

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

        var outcome = ManagedEtabsVisibility.EnsureHidden(api);

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

    [Fact]
    public void AThrowingVisibilityReadIsBoundedAndNeverThrowsOutOfThePolicy()
    {
        var api = new FakeVisibilityApi(visible: true)
        {
            ReadException = new InvalidOperationException("COM went away")
        };

        var outcome = ManagedEtabsVisibility.EnsureHidden(api);

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

        var outcome = ManagedEtabsVisibility.EnsureHidden(api);

        Assert.False(outcome.Confirmed);
        Assert.NotNull(outcome.Diagnostic);
        Assert.Contains(
            $"operation={ManagedEtabsVisibility.HideOperation}",
            outcome.Diagnostic,
            StringComparison.Ordinal);
    }

    internal sealed class FakeVisibilityApi(bool visible) : IEtabsVisibilityApi
    {
        public List<string> Events { get; } = [];
        public int HideCalls { get; private set; }
        public int UnhideCalls { get; private set; }
        public int TransitionReturnCode { get; init; }
        public bool IgnoreTransition { get; init; }
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
            if (TransitionException is not null) throw TransitionException;
            if (TransitionReturnCode == 0 && !IgnoreTransition) IsVisible = false;
            return TransitionReturnCode;
        }

        public int Unhide()
        {
            Events.Add("unhide");
            UnhideCalls++;
            if (TransitionException is not null) throw TransitionException;
            if (TransitionReturnCode == 0 && !IgnoreTransition) IsVisible = true;
            return TransitionReturnCode;
        }
    }
}
