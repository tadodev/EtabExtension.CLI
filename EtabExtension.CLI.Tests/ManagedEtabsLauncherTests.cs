using System.Runtime.Versioning;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

// The managed startup contract, exercised end to end without ETABS or COM.
//
// Sequence under test (Cardex ETABS 23.3): preflight census -> cHelper.CreateObject
// (which starts the program) -> exact OS census -> authoritative owned handle -> Windows
// window guard armed over that exact process -> cOAPI.ApplicationStart() == 0 ->
// cOAPI.Hide confirmed -> cOAPI.SapModel present. Initialization and the EtabSharp wrap
// happen afterwards, in EtabsSession, so the record is written between ownership and
// initialization.
//
// Ownership moved ahead of ApplicationStart for one reason: the supervised #20 live
// certification measured a real ETABS window on screen for 5.19 s INSIDE that call, and a
// window cannot be suppressed before the process that owns it has been proven to be ours.
public sealed class ManagedEtabsLauncherTests
{
    private static readonly ManagedProcessIdentity Identity = new(
        4242,
        new DateTimeOffset(2026, 8, 13, 5, 12, 12, TimeSpan.Zero),
        @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe");

    [Fact]
    public void LaunchCreatesStartsCensusesAndTakesOneAuthoritativeHandle()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses { AfterStart = [Identity] };
        var launcher = Build(api, processes, out var events);

        var managed = launcher.Launch();

        Assert.Equal(Identity, managed.Identity);
        Assert.NotEqual(Guid.Empty, managed.ManagedLaunchRecordId);
        Assert.Equal(
            [
                "census-preflight",
                "create-object",
                // Ownership as early as CreateObject allows, because the window guard may
                // only ever be armed over a process this launcher has proven it owns.
                "census-ownership",
                "open-exact",
                "window-guard-activate",
                // And only then the blocking start, with the observer already watching.
                "application-start",
                // CSI mutates. The hide is issued UNCONDITIONALLY - note there is no
                // "visible" read before it. A read-first policy skipped the hide exactly
                // when the flag was lying, which is the #20 measurement: stuck true for 94
                // reads across 10.014 s while the windows were in fact hidden.
                "hide",
                // One telemetry read afterwards, recorded and obeyed by nothing.
                "visible",
                // THIS is the gate: the exact-owned Windows census must report no
                // materially visible owned top-level window before startup continues.
                "window-guard-confirm-suppressed",
                // Confirming it for the first time is what CLOSES the startup-consent
                // interval. Exposure after this point is unconsented and sticky.
                "window-guard-enter-background-hidden",
                "sap-model",
                "version-probe",
                "oapi-version"
            ],
            events);
        Assert.Equal(Identity.ExecutablePath, api.CreatedFromPath);
        Assert.Equal(1, api.StartCount);

        // Not API-ready yet: initialization and the wrap are the session's job.
        Assert.Equal(0, api.InitializeCount);
        Assert.Equal(0, api.WrapCount);
        Assert.Equal(0, processes.TerminateExactCalls);
    }

    [Fact]
    public void LaunchWrapsTheSameObjectItStartedRatherThanReattaching()
    {
        var api = new FakeRawApi();
        var launcher = Build(api, new FakeProcesses { AfterStart = [Identity] }, out _);

        var managed = launcher.Launch();
        managed.CompleteApiReadiness();

        Assert.Equal(1, api.WrapCount);
        Assert.Same(api, api.WrappedInstance);
        Assert.Equal(23, api.WrappedMajorVersion);
        Assert.Equal("23.3.0", api.WrappedFullVersion);
        Assert.Equal(2.16, api.WrappedApiVersion);
    }

    [Fact]
    public void CreateObjectReturningNothingFailsTypedAndStartsNothingElse()
    {
        var processes = new FakeProcesses { AfterStart = [] };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new NullReturningApiFactory(),
            new FakeVersionProbe(),
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApiObjectCreationFailed, error.Code);
        Assert.Contains("cHelper.CreateObject", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, processes.TerminateExactCalls);
    }

    // CreateObject starts the program, so a failure at that call can leave a survivor. The
    // creation call must sit inside the cleanup envelope, not before it.
    [Fact]
    public void CreateObjectReturningNothingStillResolvesAProcessItStarted()
    {
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new NullReturningApiFactory(),
            new FakeVersionProbe(),
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApiObjectCreationFailed, error.Code);
        Assert.Equal(1, processes.TerminateExactCalls);
        Assert.Equal(Identity, processes.TerminateExactExpected);
        Assert.NotNull(error.Cleanup);
        Assert.True(error.Cleanup!.Success);
        Assert.True(error.Cleanup.Data.ProcessExitConfirmed);
    }

    [Fact]
    public void CreateObjectThrowingWithAnAmbiguousSurvivorReportsUnresolvedCleanup()
    {
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            AfterStartUnidentified = 1
        };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new ThrowingApiFactory(new TestException("create threw", 0)),
            new FakeVersionProbe(),
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        // Nothing is terminated, and the failure carries machine-visible terminal facts.
        Assert.Equal(0, processes.TerminateExactCalls);
        Assert.NotNull(error.Cleanup);
        Assert.False(error.Cleanup!.Success);
        Assert.False(error.Cleanup.Data.ProcessExitConfirmed);
        Assert.Equal(ManagedEtabsShutdownState.IdentityMismatch, error.Cleanup.Data.State);
        Assert.Contains("processExitConfirmed=False", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateObjectFailureWithNoSurvivorIsResolvedClean()
    {
        var processes = new FakeProcesses { AfterStart = [] };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new NullReturningApiFactory(),
            new FakeVersionProbe(),
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(0, processes.TerminateExactCalls);
        Assert.True(error.Cleanup!.Success);
        Assert.True(error.Cleanup.Data.ProcessExitConfirmed);
    }

    [Fact]
    public void UnconfirmedTerminationOfTheStartedProcessIsReportedUnresolved()
    {
        var api = new FakeRawApi { StartReturnCode = 3 };
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            // Ownership is proven before the start now, so an unresolved cleanup is
            // expressed through the authoritative handle rather than a census fallback.
            OpenExactResult = new FakeOwnedProcess(Identity) { RefusesToExit = true }
        };
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApplicationStartFailed, error.Code);
        Assert.False(error.Cleanup!.Success);
        Assert.Equal(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            error.Cleanup.Data.State);
        Assert.Equal(Identity.Pid, error.Cleanup.Data.OwnedPid);
    }

    [Fact]
    public void ResolvedCleanupCarriesNoTerminalFailureToTheSession()
    {
        var api = new FakeRawApi { StartReturnCode = 3 };
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.True(error.Cleanup!.Success);
        Assert.True(error.Cleanup.Data.ProcessExitConfirmed);
        Assert.True(error.Cleanup.Data.Forced);
    }

    [Fact]
    public void CreateObjectThrowingFailsTypedWithBoundedDiagnostics()
    {
        var launcher = new ManagedEtabsLauncher(
            new FakeProcesses { AfterStart = [] },
            new FakeResolver(Identity.ExecutablePath),
            new ThrowingApiFactory(new TestException("com blew up\r\n", unchecked((int)0x80004005))),
            new FakeVersionProbe(),
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApiObjectCreationFailed, error.Code);
        Assert.Contains("operation=cHelper.CreateObject", error.Message, StringComparison.Ordinal);
        Assert.Contains("hresult=0x80004005", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(error.Message, char.IsControl);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void ApplicationStartNonzeroExitsAndTerminatesTheProvenSurvivor(int returnCode)
    {
        var api = new FakeRawApi { StartReturnCode = returnCode };
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = Build(api, processes, out var events);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApplicationStartFailed, error.Code);
        Assert.Contains("operation=cOAPI.ApplicationStart", error.Message, StringComparison.Ordinal);
        Assert.Contains($"returnCode={returnCode}", error.Message, StringComparison.Ordinal);

        // Raw exit first, then exact-identity termination of the process it started.
        // Ownership is now proven BEFORE the start, so the authoritative handle — not a
        // re-derived census — is what stops the process this attempt started.
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(0, processes.TerminateExactCalls);
        Assert.Equal(1, processes.OpenedHandle!.KillCount);
        Assert.True(processes.OpenedHandle.Disposed);
        Assert.Equal(0, api.WrapCount);
        Assert.Contains("open-exact", events);

        // And the suppression is retired before anything is asked to exit, so a failed
        // start cannot put a window on screen on its way out.
        Assert.True(Assert.Single(processes.Guards.Activated).Disposed);
        Assert.False(processes.Guards.Activated[0].ReleasedForUser);
        Assert.False(processes.Guards.Activated[0].IsActive);
    }

    [Fact]
    public void ApplicationStartThrowingStillResolvesTheStartedProcess()
    {
        var api = new FakeRawApi { StartException = new TestException("start threw", 0) };
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApplicationStartFailed, error.Code);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, processes.OpenedHandle!.KillCount);
        Assert.True(processes.Guards.Activated[0].Disposed);
    }

    [Fact]
    public void MissingSapModelFailsTypedAndStopsTheProcessItAlreadyOwned()
    {
        var api = new FakeRawApi { HasSapModelValue = false };
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = Build(api, processes, out var events);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ApiModelUnavailable, error.Code);
        Assert.Contains("open-exact", events);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, processes.OpenedHandle!.KillCount);
        Assert.True(processes.Guards.Activated[0].Disposed);
    }

    [Fact]
    public void ExternalInstanceBeforeLaunchFailsClosedWithoutCreatingAnything()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses
        {
            Preflight = [Identity with { Pid = 99 }],
            AfterStart = [Identity]
        };
        var launcher = Build(api, processes, out var events);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(["census-preflight"], events);
        Assert.Equal(0, api.StartCount);
        Assert.Equal(0, processes.TerminateExactCalls);

        // Nothing was created, so nothing was owned, so nothing was guarded.
        Assert.Empty(processes.Guards.Activated);
    }

    [Fact]
    public void NoProcessAfterStartFailsClosedAndTerminatesNothing() =>
        AssertCensusFailsClosed([], unidentified: 0);

    [Fact]
    public void SeveralProcessesAfterStartFailClosedAndTerminateNothing() =>
        AssertCensusFailsClosed([Identity, Identity with { Pid = 77 }], unidentified: 0);

    [Fact]
    public void UnreadableProcessAfterStartFailsClosedAndTerminatesNothing() =>
        AssertCensusFailsClosed([Identity], unidentified: 1);

    private static void AssertCensusFailsClosed(
        ManagedProcessIdentity[] identified,
        int unidentified)
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses
        {
            AfterStart = identified,
            AfterStartUnidentified = unidentified
        };
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(1, api.ExitCount);

        // Exactly one identified survivor may be terminated by exact identity; anything
        // else is unproven and must be left alone.
        var provable = identified.Length == 1 && unidentified == 0;
        Assert.Equal(provable ? 1 : 0, processes.TerminateExactCalls);
        Assert.Equal(0, api.WrapCount);
    }

    [Fact]
    public void UnmatchableLiveIdentityFailsTypedAndDoesNotClaimOwnership()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            OpenExactResult = null,
            ExactTerminationResult = new(ExactProcessTerminationState.ConfirmedGone, Identity)
        };
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ProcessIdentityFailed, error.Code);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, processes.TerminateExactCalls);

        // No authoritative handle means no guard: suppression is never armed on a process
        // whose identity this launcher could not confirm.
        Assert.Empty(processes.Guards.Activated);
        Assert.Equal(0, api.StartCount);
    }

    [Fact]
    public void OwnedHandleFailureStopsTheProcessItAlreadyProved()
    {
        var api = new FakeRawApi();
        var owned = new FakeOwnedProcess(Identity);
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            OpenExactResult = owned
        };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new FakeApiFactory(api, []),
            new ThrowingVersionProbe(),
            TextWriter.Null,
            processes.Guards,
            new FakeClock());

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ProcessIdentityFailed, error.Code);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, owned.KillCount);
        Assert.True(owned.Disposed);

        // The handle was authoritative, so the exact-identity terminate path is not needed.
        Assert.Equal(0, processes.TerminateExactCalls);
    }

    /// <summary>
    /// The subscription's teardown could not be joined, and the launch failure the release
    /// gate reads says so.
    ///
    /// <para>Nothing about <c>SetWinEventHook</c> can be cancelled once it is in flight, so
    /// a pump can still be inside it when activation gives up. The monitor disarms that
    /// thread and lets it remove its own hook, but it does NOT get to call the cleanup
    /// complete — and this is the wiring that carries the difference out: through the guard
    /// constructor, through the launcher's typed <c>WindowSuppressionUnavailable</c> wrap,
    /// through the diagnostic formatter's 512-character message bound, and into the
    /// exception the cleanup envelope rethrows.</para>
    ///
    /// <para>Everything below the factory is the production monitor. Only the two user32
    /// hook calls are the test's, which is the only way to hold an install open on demand.</para>
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AnUnjoinableWindowSubscriptionTeardownIsNamedInTheLaunchFailure()
    {
        var api = new FakeRawApi();
        var owned = new FakeOwnedProcess(Identity);
        var guards = new LateSubscriptionGuardFactory();
        var processes = new FakeProcesses
        {
            AfterStart = [Identity],
            OpenExactResult = owned
        };
        var launcher = new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new FakeApiFactory(api, []),
            new FakeVersionProbe(),
            TextWriter.Null,
            guards,
            new FakeClock());

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.WindowSuppressionUnavailable, error.Code);
        Assert.Contains("UNRESOLVED", error.Message, StringComparison.Ordinal);

        // The launch was not derailed by the thread it could not join: the process this
        // attempt started is still stopped, and ApplicationStart was never reached.
        Assert.Equal(1, owned.KillCount);
        Assert.True(owned.Disposed);
        Assert.Equal(0, api.StartCount);

        // And when the held install finally returns, that pump takes its own hook off.
        guards.Release.Set();
        Assert.True(
            guards.Removed.Wait(TimeSpan.FromSeconds(30)),
            "the late hook was never removed.");
    }

    /// <summary>
    /// The measured RC1 defect, stated as a test: the window became visible 8.5 s into a
    /// background snapshot-export and showed a blank <c>(Untitled)</c> model at 14.8 s,
    /// while the requested EDB only opened at 16.9 s. Hiding must therefore happen on the
    /// start path — before SapModel, before the census, and long before any model open.
    /// </summary>
    [Fact]
    public void LaunchHidesTheStartedApplicationBeforeAnythingElseTouchesIt()
    {
        var api = new FakeRawApi();
        var launcher = Build(api, new FakeProcesses { AfterStart = [Identity] }, out var events);

        launcher.Launch();

        Assert.Equal(1, api.HideCalls);
        Assert.False(api.IsVisible);
        var hide = events.IndexOf("hide");
        Assert.True(hide > events.IndexOf("application-start"), "Hide must follow ApplicationStart.");
        Assert.True(hide < events.IndexOf("sap-model"), "Hide must precede everything after the start.");
    }

    /// <summary>
    /// The ruling #20 forced, stated as a test. <c>cOAPI.Visible()</c> stayed true for 94
    /// reads across 10.014 s on the certified candidate while the exact-owned windows were
    /// suppressed the whole time — so the candidate refused a session that was, in fact,
    /// hidden, and <c>snapshot-export</c> came back <c>success=false</c>.
    ///
    /// <para>CSI never clearing must now cost the launch nothing, provided Windows confirms
    /// suppression. Both rows are CSI disagreeing: one refusing the call, one accepting it
    /// and never changing the flag.</para>
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ACsiHideThatNeverAgreesDoesNotFailALaunchWindowsConfirms(
        bool nonZeroReturn,
        bool ignoreHide)
    {
        var api = new FakeRawApi
        {
            HideReturnCode = nonZeroReturn ? 1 : 0,
            IgnoreHide = ignoreHide
        };
        var processes = new FakeProcesses { AfterStart = [Identity] };
        var diagnostics = new StringWriter();
        var launcher = Build(api, processes, out _, diagnostics: diagnostics);

        var managed = launcher.Launch();

        Assert.Equal(Identity, managed.Identity);

        // Exactly one Hide, and its disagreement is recorded rather than acted on.
        Assert.Equal(1, api.HideCalls);
        Assert.Contains(
            "Windows state is the authority",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(1, processes.Guards.Activated[0].SuppressionConfirmations);

        // And the consent interval closed on the strength of the WINDOWS confirmation,
        // not on anything CSI said about itself.
        Assert.Equal(1, processes.Guards.Activated[0].EnterBackgroundHiddenCalls);
    }

    /// <summary>
    /// The gate that replaced it. An owned top-level window Windows still reports visible
    /// fails the launch, names the offending handle, and stops the exact owned process.
    /// </summary>
    [Fact]
    public void AnOwnedWindowStillOnScreenFailsTheLaunchAndStopsTheOwnedProcess()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses { AfterStart = [Identity] };
        processes.Guards.StillVisible = [0x2A4];
        var launcher = Build(api, processes, out _);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.HiddenStateNotEstablished, error.Code);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("stage=after cOAPI.ApplicationStart", error.Message, StringComparison.Ordinal);

        // The exact owned process is stopped, and suppression is retired, not leaked.
        Assert.Equal(1, processes.OpenedHandle!.KillCount);
        Assert.True(processes.Guards.Activated[0].Disposed);
        Assert.NotNull(error.Cleanup);
        Assert.True(error.Cleanup!.Success);

        // Nothing downstream of the gate ran.
        Assert.Equal(0, api.WrapCount);
    }

    /// <summary>
    /// The CSI hide now costs NO waiting at all, on a build whose Visible() flag never
    /// moves. There is nothing to converge on: the flag is not an oracle in either
    /// direction, so the policy issues the call, records the answer and hands the question
    /// to the Windows census.
    ///
    /// <para>The old policy spent a bounded budget re-reading that flag. Against ETABS 23.3
    /// that budget was always spent in full and always changed nothing — this asserts the
    /// clock is not advanced by the CSI step at all.</para>
    /// </summary>
    [Fact]
    public void TheCsiHideCostsNoConvergenceWaitingAtAll()
    {
        var api = new FakeRawApi { IgnoreHide = true };
        var processes = new FakeProcesses { AfterStart = [Identity] };
        var clock = new FakeClock();

        _ = Build(api, processes, out _, clock).Launch();

        Assert.Equal(1, api.HideCalls);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.Empty(clock.Waits);
    }

    /// <summary>
    /// The launch says, on stderr, that background UI suppression was CONFIRMED and what
    /// that rests on. This is the line the supervised gate reads, and it must describe the
    /// Windows census rather than what CSI was asked for.
    /// </summary>
    [Fact]
    public void LaunchReportsThatWindowsConfirmedTheSuppression()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses { AfterStart = [Identity] };
        var diagnostics = new StringWriter();

        _ = Build(api, processes, out _, diagnostics: diagnostics).Launch();

        var text = diagnostics.ToString();
        Assert.Contains(
            "background UI suppression confirmed after cOAPI.ApplicationStart",
            text,
            StringComparison.Ordinal);
        Assert.Contains("observations=", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window guard is armed over the authoritative handle itself, never over a pid,
    /// and it is armed before the blocking start call the #20 run measured a window
    /// through. Deleting the activation from <c>Launch</c> fails here.
    /// </summary>
    [Fact]
    public void TheWindowGuardIsArmedOverTheProvenHandleBeforeApplicationStart()
    {
        var api = new FakeRawApi();
        var owned = new FakeOwnedProcess(Identity);
        var processes = new FakeProcesses { AfterStart = [Identity], OpenExactResult = owned };
        var launcher = Build(api, processes, out var events);

        launcher.Launch();

        var guard = Assert.Single(processes.Guards.Activated);
        Assert.Same(owned, guard.Owned);
        Assert.Equal(Identity, guard.Identity);
        Assert.True(
            events.IndexOf("window-guard-activate") < events.IndexOf("application-start"),
            "Suppression must be armed before the blocking ApplicationStart call.");
        Assert.True(
            events.IndexOf("open-exact") < events.IndexOf("window-guard-activate"),
            "Suppression may only be armed once ownership is authoritative.");

        // Still guarding when the application is handed over: startup, initialization,
        // API readiness and every background command run under it.
        Assert.False(guard.Disposed);
        Assert.False(guard.ReleasedForUser);
    }

    /// <summary>
    /// The census moved ahead of <c>ApplicationStart</c>, so it can now meet a process that
    /// is seconds old and not yet willing to report its main module. That is a transient,
    /// and it is waited out — never resolved optimistically, and never at the cost of the
    /// fail-closed rule below.
    /// </summary>
    [Fact]
    public void AProcessThatCannotYetBeIdentifiedIsWaitedOutRatherThanFailedOrGuessed()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses
        {
            AfterStartObservations =
            [
                new([], 1),
                new([], 1),
                new([Identity], 0)
            ]
        };
        var clock = new FakeClock();
        var launcher = Build(api, processes, out _, clock);

        var managed = launcher.Launch();

        Assert.Equal(Identity, managed.Identity);
        Assert.Equal(
            [
                ManagedEtabsLauncher.OwnershipCensusPollInterval,
                ManagedEtabsLauncher.OwnershipCensusPollInterval
            ],
            clock.Waits);
    }

    /// <summary>
    /// And the deadline still fails closed on an ambiguity that never resolves: nothing is
    /// guarded, nothing is wrapped, and no process is claimed.
    /// </summary>
    [Fact]
    public void AnOwnershipCensusThatNeverResolvesFailsClosedAtTheDeadline()
    {
        var api = new FakeRawApi();
        var processes = new FakeProcesses { AfterStart = [Identity], AfterStartUnidentified = 1 };
        var clock = new FakeClock();
        var launcher = Build(api, processes, out _, clock);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(ManagedEtabsLauncher.OwnershipCensusDeadline, clock.Elapsed);
        Assert.Empty(processes.Guards.Activated);
        Assert.Equal(0, api.StartCount);
    }

    /// <summary>
    /// The daemon resolves this launcher out of the container, and the repair added two
    /// more public constructors to it. Microsoft.Extensions.DependencyInjection picks a
    /// constructor by what it can resolve, so an overload set it cannot decide between is a
    /// startup failure that no unit test touching the class directly would ever see.
    ///
    /// <para>Resolution is side-effect free by construction here: nothing on the path
    /// beyond the constructors runs, so no ETABS is discovered, started or looked for.</para>
    /// </summary>
    [Fact]
    public void TheServeContainerCanStillConstructTheLauncher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        services.AddServeFeature();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ManagedEtabsLauncher>(
            scope.ServiceProvider.GetRequiredService<IManagedEtabsLauncher>());
    }

    [Fact]
    public void UnreadableApiVersionDoesNotFailAProvenLifecycle()
    {
        var api = new FakeRawApi { OapiVersionException = new TestException("no version", 0) };
        var launcher = Build(api, new FakeProcesses { AfterStart = [Identity] }, out _);

        var managed = launcher.Launch();
        managed.CompleteApiReadiness();

        Assert.Equal(0d, api.WrappedApiVersion);
        Assert.Equal(23, api.WrappedMajorVersion);
    }

    // The old managed path is gone, not merely unused: these types no longer exist, so
    // Process.Start, ConnectToProcess, ROT fallback and the attach-retry clock cannot be
    // reintroduced by accident.
    [Theory]
    [InlineData("IEtabsProcessStarter")]
    [InlineData("WindowsEtabsProcessStarter")]
    [InlineData("IManagedEtabsConnector")]
    [InlineData("EtabSharpManagedEtabsConnector")]
    [InlineData("IEtabsLaunchClock")]
    [InlineData("SystemEtabsLaunchClock")]
    public void RemovedManagedStartupTypesAreAbsentFromTheAssembly(string typeName)
    {
        var assembly = typeof(IManagedEtabsApplication).Assembly;

        Assert.Null(assembly.GetType(
            $"EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.{typeName}"));
    }

    // The raw boundary is lifecycle plus the three cOAPI visibility calls, and nothing
    // else — no attach, no ROT lookup, no out-of-band process start.
    //
    // Visibility was deliberately excluded here once, on the reasoning that "an early
    // Hide is not expressible from managed startup". CLI #22 is the bill for that: the
    // one-shot Mode B commands hid the instance they created, the daemon that replaced
    // them could not, and a background Commit left a blank (Untitled) ETABS window on the
    // engineer's screen for 8.4 seconds. Hiding at startup IS the requirement, so the
    // capability belongs on the boundary that owns startup.
    [Fact]
    public void RawApiBoundaryExposesLifecycleAndVisibilityAndNothingElse()
    {
        // Walks the real interface graph rather than a hand-written pair, so a future third
        // base interface cannot smuggle members past a test whose entire claim is "nothing
        // else". The version this replaced had the same blind spot.
        var members = typeof(IEtabsRawApi).GetInterfaces()
            .Append(typeof(IEtabsRawApi))
            .SelectMany(contract => contract.GetMembers())
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Application",
                "ApplicationExit",
                "ApplicationStart",
                "CompleteApiReadiness",
                "GetOapiVersionNumber",
                "HasSapModel",
                "Hide",
                "InitializeNewModel",
                "ReleaseApiReferences",
                "Unhide",
                "Visible"
            ],
            members);
    }

    private static ManagedEtabsLauncher Build(
        FakeRawApi api,
        FakeProcesses processes,
        out List<string> events,
        FakeClock? clock = null,
        TextWriter? diagnostics = null)
    {
        var recorded = new List<string>();
        events = recorded;
        api.Events = recorded;
        processes.Events = recorded;
        processes.Guards.Events = recorded;
        return new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new FakeApiFactory(api, recorded),
            new FakeVersionProbe(recorded),
            diagnostics ?? TextWriter.Null,
            processes.Guards,
            clock ?? new FakeClock());
    }

    /// <summary>Virtual time, so every bounded wait in the launcher runs at full speed.</summary>
    private sealed class FakeClock : IManagedEtabsClock
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

    private sealed class FakeWindowGuardFactory : IManagedEtabsWindowGuardFactory
    {
        public List<string> Events { get; set; } = [];

        public List<FakeWindowGuard> Activated { get; } = [];

        /// <summary>Windows the guard will keep reporting visible on every session it arms.</summary>
        public nint[] StillVisible { get; set; } = [];

        public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess)
        {
            Events.Add("window-guard-activate");
            var guard = new FakeWindowGuard(ownedProcess, Events) { StillVisible = StillVisible };
            Activated.Add(guard);
            return guard;
        }
    }

    private sealed class FakeWindowGuard(IOwnedEtabsProcess owned, List<string> events)
        : IManagedEtabsWindowGuard
    {
        public IOwnedEtabsProcess Owned { get; } = owned;
        public ManagedProcessIdentity Identity => Owned.Identity;
        public bool IsActive => !Disposed && !ReleasedForUser;
        public bool Disposed { get; private set; }
        public bool ReleasedForUser { get; private set; }
        public int SuppressionConfirmations { get; private set; }

        /// <summary>Windows the census keeps reporting visible, i.e. suppression failing.</summary>
        public nint[] StillVisible { get; set; } = [];

        public ManagedEtabsVisibilityState State { get; private set; } =
            ManagedEtabsVisibilityState.StartingVisibleByConsent;

        public ManagedEtabsExposureEvidence Exposure { get; set; } =
            ManagedEtabsExposureEvidence.None;

        public int EnterBackgroundHiddenCalls { get; private set; }

        public int EnterUserVisibleCalls { get; private set; }

        public void EnterBackgroundHidden()
        {
            events.Add("window-guard-enter-background-hidden");
            EnterBackgroundHiddenCalls++;
            State = ManagedEtabsVisibilityState.BackgroundHidden;
        }

        public void EnterUserVisible()
        {
            events.Add("window-guard-enter-user-visible");
            EnterUserVisibleCalls++;
            State = ManagedEtabsVisibilityState.UserVisible;
        }

        public ManagedEtabsWindowConfirmation ConfirmSuppressed()
        {
            events.Add("window-guard-confirm-suppressed");
            SuppressionConfirmations++;
            return StillVisible.Length == 0
                ? new(true, 1, TimeSpan.Zero, [], null)
                : new(
                    false,
                    3,
                    TimeSpan.FromSeconds(5),
                    StillVisible,
                    $"{ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed}; " +
                    $"ownedPid={Identity.Pid}; visibleOwnedWindows={StillVisible.Length}");
        }

        public ManagedEtabsWindowConfirmation ConfirmRevealed()
        {
            events.Add("window-guard-confirm-revealed");
            return new(true, 1, TimeSpan.Zero, [(nint)1], null);
        }

        public void ReleaseForExplicitUserAction() => ReleasedForUser = true;

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The PRODUCTION window monitor, over a <c>SetWinEventHook</c> the test holds open past
    /// both of the monitor's deadlines. Only the two user32 calls are faked — there is no
    /// other way to keep an install in flight on demand — so the failure the launcher
    /// reports is the real one, formatted by the real formatter.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class LateSubscriptionGuardFactory : IManagedEtabsWindowGuardFactory
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public ManualResetEventSlim Removed { get; } = new(false);

        public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess) =>
            new ManagedEtabsWindowGuard(
                ownedProcess,
                new NoWindows(),
                ManagedEtabsWindowPolicy.Default,
                new Win32OwnedWindowSurfaceMonitor(
                    TimeSpan.FromMilliseconds(5),
                    HoldTheInstallOpen,
                    _ => Removed.Set(),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(50)),
                new StatedDesktop());

        private nint HoldTheInstallOpen(
            int processId,
            Win32OwnedWindowSurfaceMonitor.WinEventProc proc)
        {
            _ = Release.Wait(TimeSpan.FromSeconds(30));
            return 0x5150;
        }

        /// <summary>An empty window station: this launch never gets far enough to observe.</summary>
        private sealed class NoWindows : ITopLevelWindows
        {
            public IReadOnlyList<TopLevelWindow> Enumerate() => [];
        }

        /// <summary>A stated desktop, so nothing here reads the real screen.</summary>
        private sealed class StatedDesktop : IVirtualDesktop
        {
            public WindowBounds Bounds => new(0, 0, 1920, 1080);
        }
    }

    private sealed class FakeResolver(string path) : IEtabsExecutableResolver
    {
        public string Resolve() => path;
    }

    private sealed class FakeApiFactory(FakeRawApi api, List<string> events) : IEtabsRawApiFactory
    {
        public IEtabsRawApi CreateFromExecutable(string executablePath)
        {
            events.Add("create-object");
            api.CreatedFromPath = executablePath;
            return api;
        }
    }

    private sealed class NullReturningApiFactory : IEtabsRawApiFactory
    {
        public IEtabsRawApi CreateFromExecutable(string executablePath) =>
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiObjectCreationFailed,
                $"cHelper.CreateObject returned no API object for '{executablePath}'.");
    }

    private sealed class ThrowingApiFactory(Exception exception) : IEtabsRawApiFactory
    {
        public IEtabsRawApi CreateFromExecutable(string executablePath) =>
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiObjectCreationFailed,
                Shared.Infrastructure.Etabs.EtabsApiDiagnosticFormatter.Exception(
                    "cHelper.CreateObject",
                    exception),
                exception);
    }

    private sealed class FakeVersionProbe(List<string>? events = null) : IEtabsVersionProbe
    {
        public (int MajorVersion, string FullVersion) Read(string executablePath)
        {
            events?.Add("version-probe");
            return (23, "23.3.0");
        }
    }

    private sealed class ThrowingVersionProbe : IEtabsVersionProbe
    {
        public (int MajorVersion, string FullVersion) Read(string executablePath) =>
            throw new IOException("version unreadable");
    }

    private sealed class FakeRawApi : IEtabsRawApi
    {
        public List<string> Events { get; set; } = [];
        public string? CreatedFromPath { get; set; }
        public int StartReturnCode { get; set; }
        public Exception? StartException { get; set; }
        public bool HasSapModelValue { get; set; } = true;
        public Exception? OapiVersionException { get; set; }
        public int HideReturnCode { get; set; }
        public bool IgnoreHide { get; set; }

        /// <summary>
        /// Reads that still report "visible" after an accepted Hide, reproducing the #20
        /// measurement: the call is taken, the state follows later.
        /// </summary>
        public int VisibleReadsBeforeHideLands { get; set; }

        public bool VisibleAfterStart { get; set; } = true;
        public bool IsVisible { get; private set; }
        public int VisibleCalls { get; private set; }
        public int HideCalls { get; private set; }
        public int UnhideCalls { get; private set; }
        public int StartCount { get; private set; }
        public int ExitCount { get; private set; }
        public int InitializeCount { get; private set; }
        public int WrapCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public object? WrappedInstance { get; private set; }
        public int WrappedMajorVersion { get; private set; }
        public double WrappedApiVersion { get; private set; }
        public string? WrappedFullVersion { get; private set; }

        public int ApplicationStart()
        {
            Events.Add("application-start");
            StartCount++;
            // Cardex documents no visibility argument on ApplicationStart and says nothing
            // about the resulting state. The supervised RC1 run observed a window arriving,
            // which is the default here — but the timeline is equally consistent with the
            // window not existing yet, so that case is expressible too.
            IsVisible = VisibleAfterStart;
            return StartException is null ? StartReturnCode : throw StartException;
        }

        public bool Visible()
        {
            Events.Add("visible");
            VisibleCalls++;
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

        private int _pendingDeferredReads;

        public int Hide()
        {
            Events.Add("hide");
            HideCalls++;
            if (HideReturnCode != 0 || IgnoreHide)
            {
                return HideReturnCode;
            }

            if (VisibleReadsBeforeHideLands > 0)
            {
                // Accepted now, applied later — exactly what the live run observed.
                _pendingDeferredReads = VisibleReadsBeforeHideLands;
                return 0;
            }

            IsVisible = false;
            return HideReturnCode;
        }

        public int Unhide()
        {
            Events.Add("unhide");
            UnhideCalls++;
            IsVisible = true;
            return 0;
        }

        public double GetOapiVersionNumber()
        {
            Events.Add("oapi-version");
            return OapiVersionException is null ? 2.16 : throw OapiVersionException;
        }

        public bool HasSapModel
        {
            get
            {
                Events.Add("sap-model");
                return HasSapModelValue;
            }
        }

        public int InitializeNewModel()
        {
            Events.Add("initialize");
            InitializeCount++;
            return 0;
        }

        public int ApplicationExit(bool fileSave)
        {
            Events.Add($"application-exit-save={fileSave}");
            ExitCount++;
            return 0;
        }

        public void CompleteApiReadiness(int majorVersion, double apiVersion, string fullVersion)
        {
            Events.Add("wrap-existing");
            WrapCount++;
            WrappedInstance = this;
            WrappedMajorVersion = majorVersion;
            WrappedApiVersion = apiVersion;
            WrappedFullVersion = fullVersion;
        }

        public ETABSApplication Application =>
            throw new InvalidOperationException("Fake must not expose COM");

        public void ReleaseApiReferences()
        {
            Events.Add("release-api-references");
            ReleaseCount++;
        }
    }

    private sealed class FakeOwnedProcess(ManagedProcessIdentity identity) : IOwnedEtabsProcess
    {
        public ManagedProcessIdentity Identity { get; } = identity;
        public bool HasExited { get; private set; }
        public int KillCount { get; private set; }
        public bool Disposed { get; private set; }

        /// <summary>A process that will not die, so unresolved cleanup stays expressible.</summary>
        public bool RefusesToExit { get; init; }

        public void Kill()
        {
            KillCount++;
            if (!RefusesToExit)
            {
                HasExited = true;
            }
        }

        public bool WaitForExit(TimeSpan timeout) => HasExited;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcesses : IProcessInspector
    {
        private bool _preflightObserved;
        private int _ownershipObservations;

        public List<string> Events { get; set; } = [];
        public ManagedProcessIdentity[] Preflight { get; set; } = [];
        public ManagedProcessIdentity[] AfterStart { get; set; } = [];
        public int AfterStartUnidentified { get; set; }

        /// <summary>
        /// Successive ownership-census answers, when the test needs the census to settle
        /// rather than resolve on the first look. The last entry repeats.
        /// </summary>
        public EtabsProcessObservation[] AfterStartObservations { get; set; } = [];

        public FakeWindowGuardFactory Guards { get; } = new();
        public FakeOwnedProcess? OpenedHandle { get; private set; }
        public IOwnedEtabsProcess? OpenExactResult { get; set; } = new FakeOwnedProcess(Identity);
        public ExactProcessTerminationResult ExactTerminationResult { get; set; } = new(
            ExactProcessTerminationState.ConfirmedGone,
            Identity);
        public int TerminateExactCalls { get; private set; }
        public ManagedProcessIdentity? TerminateExactExpected { get; private set; }
        public TimeSpan? TerminateExactTimeout { get; private set; }

        public EtabsProcessObservation ObserveEtabs()
        {
            if (!_preflightObserved)
            {
                _preflightObserved = true;
                Events.Add("census-preflight");
                return new(Preflight, 0);
            }

            Events.Add("census-ownership");
            if (AfterStartObservations.Length == 0)
            {
                return new(AfterStart, AfterStartUnidentified);
            }

            var index = Math.Min(_ownershipObservations, AfterStartObservations.Length - 1);
            _ownershipObservations++;
            return AfterStartObservations[index];
        }

        public ManagedProcessIdentity? Find(int pid) =>
            AfterStart.FirstOrDefault(identity => identity.Pid == pid);

        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected)
        {
            Events.Add("open-exact");
            OpenedHandle = OpenExactResult as FakeOwnedProcess;
            return OpenExactResult;
        }

        public ExactProcessTerminationResult TerminateExact(
            ManagedProcessIdentity expected,
            TimeSpan timeout)
        {
            TerminateExactCalls++;
            TerminateExactExpected = expected;
            TerminateExactTimeout = timeout;
            return ExactTerminationResult;
        }
    }

    private sealed class TestException : Exception
    {
        public TestException(string message, int hresult) : base(message) => HResult = hresult;
    }
}
