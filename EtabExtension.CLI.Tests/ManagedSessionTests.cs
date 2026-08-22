using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ManagedSessionTests
{
    private static readonly ManagedProcessIdentity Identity = new(
        42,
        new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero),
        @"C:\ETABS\ETABS.exe");

    [Fact]
    public void Record_store_round_trips_and_clears()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "record.json");
        try
        {
            var store = new JsonSessionRecordStore(path);
            var record = Record(Guid.NewGuid());
            store.Write(record);
            Assert.Equal(record, store.Read());
            store.Clear();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Identity_match_requires_pid_start_time_and_executable_path()
    {
        var record = Record(Guid.NewGuid());
        Assert.True(OrphanSessionCleaner.IdentityMatches(record, Identity));
        Assert.False(OrphanSessionCleaner.IdentityMatches(record, Identity with { Pid = 43 }));
        Assert.False(OrphanSessionCleaner.IdentityMatches(
            record,
            Identity with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddMilliseconds(1) }));
        Assert.False(OrphanSessionCleaner.IdentityMatches(
            record,
            Identity with { ExecutablePath = @"C:\Other\ETABS.exe" }));
    }

    [Fact]
    public void SessionWritesRecoveryRecordBeforeExactOnceInitializationAndReusesReadySession()
    {
        var events = new List<string>();
        var launchId = Guid.NewGuid();
        var managed = new FakeManaged(
            Identity,
            launchId,
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [],
            hasExited: false);
        var launcher = new FakeLauncher(managed, events);
        var store = new MemoryStore(events);
        var processes = new FakeProcesses { Live = Identity };
        var session = new EtabsSession(launcher, processes, store, Consented());

        var first = session.GetOrStartOwned();
        var second = session.GetOrStartOwned();

        Assert.Same(managed, first);
        Assert.Same(first, second);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, managed.InitializeCount);
        Assert.True(session.IsStarted);
        Assert.NotNull(store.Record);
        Assert.Equal(
            [
                "launch",
                "ownership-proven",
                "record-write",
                "initialize",
                "wrap-existing",
                // Re-proven suppressed after InitializeNewModel — the call that puts the
                // blank (Untitled) model behind the window — and still before the session
                // is handed to any command. The proof is the exact-owned Windows census,
                // not cOAPI.Visible(), which #20 showed never clears.
                "confirm-windows-suppressed"
            ],
            events);
    }

    /// <summary>
    /// The background half of the CLI #22 contract. A session created because some
    /// background command needed COM must be PROVEN suppressed by the time anything can use
    /// it, and proven exactly once: re-proving on every request would put a Windows census
    /// on the command path, and re-suppressing is how a session the user asked to see would
    /// get yanked off the screen.
    /// </summary>
    [Fact]
    public void ACreatedSessionIsProvenSuppressedOnceBeforeAnyCommandCanUseIt()
    {
        var fixture = VisibilityFixture.Create();

        fixture.Session.GetOrStartOwned();
        fixture.Session.GetOrStartOwned();

        Assert.Equal(1, fixture.Managed.SuppressionConfirmations);
        Assert.Equal(0, fixture.Managed.RevealCalls);
        Assert.Equal(0, fixture.Managed.RevealConfirmations);
    }

    /// <summary>
    /// The explicit half. Nothing else in the daemon can make ETABS visible, so this is
    /// the single transition the "Open in ETABS" intent travels through.
    /// </summary>
    [Fact]
    public void AnExplicitUserRequestMakesTheManagedSessionVisible()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();

        var revealed = fixture.Session.RevealForExplicitUserRequest();

        Assert.True(revealed.Success);
        Assert.Null(revealed.Error);
        Assert.Equal(1, fixture.Managed.RevealCalls);
        Assert.True(fixture.Managed.IsVisible);
    }

    /// <summary>
    /// The reveal order, every step of which is load bearing. The requested model is
    /// already confirmed open by the time this method is reached; suppression is retired
    /// FIRST (which is also what puts our own suppressed windows back), the CSI hint is
    /// given its chance, and Windows has the last word. Retiring after the transition would
    /// leave the guard free to take the engineer's window straight back down.
    /// </summary>
    [Fact]
    public void AnExplicitRevealRetiresSuppressionThenAsksCsiThenConfirmsFromWindows()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        fixture.Events.Clear();

        fixture.Session.RevealForExplicitUserRequest();

        Assert.Equal(
            ["window-guard-release", "csi-unhide", "confirm-windows-revealed", "enter-user-visible"],
            fixture.Events);
        Assert.Equal(1, fixture.Managed.WindowGuardReleaseCalls);
    }

    /// <summary>
    /// The trap the ruling called out explicitly. With <c>cOAPI.Visible()</c> stuck true,
    /// the CSI policy reads "already visible" and issues no <c>Unhide</c> at all — so a
    /// reveal that accepted CSI's word would report success with nothing on screen.
    /// </summary>
    [Fact]
    public void ARevealCannotSucceedFromCsiAloneWhenNoOwnedWindowIsVisible()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Managed.IsVisible = true;            // CSI already thinks it is visible
        fixture.Managed.WindowsRevealConfirmed = false;
        fixture.Session.GetOrStartOwned();

        var revealed = fixture.Session.RevealForExplicitUserRequest();

        // CSI was asked, agreed, and changed nothing — and that is not a reveal.
        Assert.False(revealed.Success);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.RevealNotConfirmed,
            revealed.Error!,
            StringComparison.Ordinal);
        Assert.Contains("csiConfirmed=True", revealed.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the mirror: CSI refusing the transition does not fail a reveal that Windows can
    /// see. The restore of our own suppressed handles is what reaches the screen.
    /// </summary>
    [Fact]
    public void ARevealSucceedsOnWindowsEvidenceEvenWhenCsiRefuses()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Managed.RevealSucceeds = false;
        fixture.Session.GetOrStartOwned();

        var revealed = fixture.Session.RevealForExplicitUserRequest();

        Assert.True(revealed.Success);
        Assert.Equal(1, fixture.Managed.RevealConfirmations);
    }

    /// <summary>
    /// A reveal that fails still leaves the guard retired. Re-arming after a failed reveal
    /// would be the one path back to hiding a window the user asked for.
    /// </summary>
    [Fact]
    public void AFailedRevealStillLeavesTheWindowGuardRetired()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Managed.WindowsRevealConfirmed = false;
        fixture.Session.GetOrStartOwned();

        var revealed = fixture.Session.RevealForExplicitUserRequest();

        Assert.False(revealed.Success);
        Assert.Equal(1, fixture.Managed.WindowGuardReleaseCalls);
    }

    /// <summary>
    /// The reuse rule, and the reason the hide lives at creation rather than at the head
    /// of every background command: a background command running against a session the
    /// user explicitly opened must leave it on screen.
    /// </summary>
    [Fact]
    public void ABackgroundCommandReusingARevealedSessionLeavesItVisible()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        fixture.Session.RevealForExplicitUserRequest();

        // Stands in for snapshot-export / analyze-and-extract asking the shared session
        // for its application.
        fixture.Session.GetOrStartOwned();
        fixture.Session.GetOrStartOwned();

        Assert.True(fixture.Managed.IsVisible);
        Assert.Equal(1, fixture.Managed.RevealCalls);

        // Suppression was proven once, at creation, and never re-proven or re-armed: the
        // guard is released exactly once, by the reveal, and no amount of later background
        // reuse touches it again.
        Assert.Equal(1, fixture.Managed.SuppressionConfirmations);
        Assert.Equal(1, fixture.Managed.WindowGuardReleaseCalls);
        Assert.Equal(0, fixture.Managed.WindowGuardDisposeCalls);
    }

    /// <summary>
    /// The policy the #20 certification forced, now resting on the right oracle. An
    /// unproven WINDOWS state ends the session and cleans up the exact owned process;
    /// warning and continuing is what the earlier RC did and what #20 measured a visible
    /// window through.
    /// </summary>
    [Fact]
    public void AnUnprovenWindowsStateFailsSessionCreationAndCleansUp()
    {
        var fixture = VisibilityFixture.Create(waitResults: [true]);
        fixture.Managed.WindowsSuppressionConfirmed = false;

        var error = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());
        var repeated = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.HiddenStateNotEstablished, error.Code);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("0x2A4", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=True", error.Message, StringComparison.Ordinal);
        Assert.Equal(error.Message, repeated.Message);
        Assert.False(fixture.Session.IsStarted);

        // The exact owned process is exited, not abandoned with a window on screen.
        Assert.Equal(1, fixture.Managed.SuppressionConfirmations);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal(1, fixture.Managed.WindowGuardDisposeCalls);
    }

    /// <summary>
    /// The exact #20 failure, inverted into the behaviour that was asked for. CSI can go on
    /// reporting <c>Visible=true</c> forever; if the exact-owned Windows census says nothing
    /// of ours is on screen, background readiness SUCCEEDS. Gating on CSI here is what
    /// returned <c>snapshot-export success=false</c> against a session that was hidden.
    /// </summary>
    [Fact]
    public void BackgroundReadinessSucceedsWhileCsiKeepsReportingVisible()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Managed.HideSucceeds = false;        // cOAPI.Visible() never clears
        fixture.Managed.WindowsSuppressionConfirmed = true;

        var owned = fixture.Session.GetOrStartOwned();

        Assert.Same(fixture.Managed, owned);
        Assert.True(fixture.Session.IsStarted);
        Assert.Contains(
            "suppression confirmed",
            fixture.Diagnostics.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The false-success line the #20 run caught: "✓ ETABS started hidden (PID …)" printed
    /// immediately after two "could not be confirmed hidden" warnings. It is now
    /// unreachable — the same failure path that tears the session down is the only exit
    /// from an unproven Windows state, so no run can report a hidden state nothing observed.
    /// </summary>
    [Fact]
    public void NoStartedHiddenSuccessLineIsEmittedWhenTheWindowsStateIsUnproven()
    {
        var fixture = VisibilityFixture.Create(waitResults: [true]);
        fixture.Managed.WindowsSuppressionConfirmed = false;

        Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        var written = fixture.Diagnostics.ToString();
        Assert.DoesNotContain("started hidden", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suppression confirmed", written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other side of it: a confirmed hide still reports the success it proved.</summary>
    [Fact]
    public void AConfirmedHideReportsTheStartedHiddenSuccessLine()
    {
        var fixture = VisibilityFixture.Create();

        fixture.Session.GetOrStartOwned();

        Assert.Contains(
            "started hidden",
            fixture.Diagnostics.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionInitializationNonzeroUsesStrongCleanupAndDoesNotRelaunch()
    {
        var events = new List<string>();
        var launchId = Guid.NewGuid();
        var managed = new FakeManaged(
            Identity,
            launchId,
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [true],
            hasExited: false,
            initializeReturnCode: 5);
        var launcher = new FakeLauncher(managed, events);
        var store = new MemoryStore(events);
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            store,
            Consented());

        var first = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());
        var second = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());

        Assert.Equal("ETABS_MODEL_INITIALIZATION_FAILED", first.Code);
        Assert.Equal(first.Message, second.Message);
        Assert.Contains("cSapModel.InitializeNewModel", first.Message, StringComparison.Ordinal);
        Assert.Contains("returnCode=5", first.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=True", first.Message, StringComparison.Ordinal);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, managed.InitializeCount);
        Assert.Equal(1, managed.ExitCount);
        Assert.Equal(0, managed.KillCount);
        Assert.Equal(0, managed.WrapperDisposeCount);
        Assert.Equal(1, managed.ProcessHandleReleaseCount);
        Assert.Null(store.Record);
        Assert.Equal(
            [
                "launch",
                "ownership-proven",
                "record-write",
                "initialize",
                // Shutdown retires the window guard first, on every route in.
                "window-guard-dispose",
                "application-exit",
                "wait-10",
                "record-clear",
                "process-handle-release",
                "release-api-references"
            ],
            events);
    }

    [Fact]
    public void SessionInitializationExceptionRetainsRecordWhenProcessExitIsUnconfirmed()
    {
        var events = new List<string>();
        var launchId = Guid.NewGuid();
        var managed = new FakeManaged(
            Identity,
            launchId,
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [false, false],
            hasExited: false,
            initializeException: new TestException("not ready\r\n", unchecked((int)0x80004005)));
        var launcher = new FakeLauncher(managed, events);
        var store = new MemoryStore(events);
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            store,
            Consented());

        var error = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());
        session.Dispose();
        var repeated = session.Shutdown();

        Assert.Equal("ETABS_MODEL_INITIALIZATION_FAILED", error.Code);
        Assert.Contains("operation=cSapModel.InitializeNewModel", error.Message, StringComparison.Ordinal);
        Assert.Contains("hresult=0x80004005", error.Message, StringComparison.Ordinal);
        Assert.Contains("state=ProcessExitUnconfirmed", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=False", error.Message, StringComparison.Ordinal);
        Assert.Contains("recordRetained=True", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, managed.InitializeCount);
        Assert.Equal(1, managed.ExitCount);
        Assert.Equal(1, managed.KillCount);
        Assert.Equal(0, managed.WrapperDisposeCount);
        Assert.Equal(0, managed.ProcessHandleReleaseCount);
        Assert.Equal(Identity.Pid, session.ProcessId);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, repeated.Data.State);
        Assert.NotNull(store.Record);
    }

    // A launch that could not prove the process it started is gone leaves no owned handle
    // and no recovery record. Without caching that terminal state the next request would
    // relaunch on top of it, and shutdown would answer "confirmed" about a process nobody
    // resolved.
    [Fact]
    public void UnresolvedLaunchCleanupBlocksRelaunchAndKeepsShutdownTruthful()
    {
        var events = new List<string>();
        var managed = new FakeManaged(
            Identity,
            Guid.NewGuid(),
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [],
            hasExited: false);
        var launcher = new FakeLauncher(managed, events)
        {
            Failure = new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApplicationStartFailed,
                "start failed")
            {
                Cleanup = new ManagedEtabsShutdownResult(
                    false,
                    ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                    "ownership of the surviving ETABS process could not be proven",
                    new ManagedEtabsShutdownData(
                        ManagedEtabsShutdownState.IdentityMismatch,
                        ProcessExitConfirmed: false,
                        Forced: false,
                        RecordRetained: false,
                        ApplicationExitReturnCode: null,
                        OwnedPid: null))
            }
        };
        var store = new MemoryStore(events);
        var session = new EtabsSession(launcher, new FakeProcesses(), store, Consented());

        var first = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());
        var second = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());
        var terminal = session.Shutdown();

        Assert.Same(first, second);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.False(session.IsStarted);

        Assert.False(terminal.Success);
        Assert.False(terminal.Data.ProcessExitConfirmed);
        Assert.Equal(ManagedEtabsShutdownState.IdentityMismatch, terminal.Data.State);
        Assert.Equal(["launch"], events);
    }

    [Fact]
    public void ResolvedLaunchCleanupLeavesTheSessionFreeToTryAgain()
    {
        var events = new List<string>();
        var managed = new FakeManaged(
            Identity,
            Guid.NewGuid(),
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [],
            hasExited: false);
        var launcher = new FakeLauncher(managed, events)
        {
            Failure = new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApplicationStartFailed,
                "start failed")
            {
                Cleanup = new ManagedEtabsShutdownResult(
                    true,
                    null,
                    null,
                    new ManagedEtabsShutdownData(
                        ManagedEtabsShutdownState.Succeeded,
                        ProcessExitConfirmed: true,
                        Forced: true,
                        RecordRetained: false,
                        ApplicationExitReturnCode: null,
                        OwnedPid: Identity.Pid))
            }
        };
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            new MemoryStore(events),
            Consented());

        Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());
        launcher.Failure = null;
        var recovered = session.GetOrStartOwned();

        Assert.Same(managed, recovered);
        Assert.Equal(2, launcher.LaunchCount);
        Assert.True(session.IsStarted);
    }

    [Fact]
    public void SessionDisposeConvergesThroughShutdownOnceAndIsIdempotent()
    {
        var events = new List<string>();
        var launchId = Guid.NewGuid();
        var managed = new FakeManaged(
            Identity,
            launchId,
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [true],
            hasExited: false);
        var store = new MemoryStore(events);
        var session = new EtabsSession(
            new FakeLauncher(managed, events),
            new FakeProcesses { Live = Identity },
            store,
            Consented());
        session.GetOrStartOwned();

        session.Dispose();
        session.Dispose();
        var terminal = session.Shutdown();

        Assert.True(terminal.Success);
        Assert.True(terminal.Data.ProcessExitConfirmed);
        Assert.Equal(1, managed.ExitCount);
        Assert.Equal(0, managed.WrapperDisposeCount);
        Assert.Equal(1, managed.ProcessHandleReleaseCount);
        Assert.Equal(1, events.Count(item => item == "application-exit"));
        Assert.Equal(1, events.Count(item => item == "process-handle-release"));
        Assert.Null(store.Record);
    }

    [Fact]
    public void SessionRecordWriteFailureWithMatchingPartialRecordCleansGracefullyAndPreservesOriginalError()
    {
        var fixture = RecordWriteFailureFixture.Create(
            waitResults: [true],
            persistAttemptedRecordBeforeThrow: true);

        var error = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, error.Code);
        Assert.Same(fixture.WriteException, error.InnerException);
        Assert.Contains("state=Succeeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=True", error.Message, StringComparison.Ordinal);
        Assert.Contains("forced=False", error.Message, StringComparison.Ordinal);
        Assert.Contains("recordRetained=False", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal(0, fixture.Managed.InitializeCount);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void SessionRecordWriteFailureWithoutRecordForcesExactOwnedHandleAndConfirmsExit()
    {
        var fixture = RecordWriteFailureFixture.Create(waitResults: [false, true]);

        var error = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, error.Code);
        Assert.Same(fixture.WriteException, error.InnerException);
        Assert.Contains("state=Succeeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=True", error.Message, StringComparison.Ordinal);
        Assert.Contains("forced=True", error.Message, StringComparison.Ordinal);
        Assert.Contains("recordRetained=False", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal(
            [ManagedEtabsShutdownMachine.GracefulExitTimeout, ManagedEtabsShutdownMachine.ForcedExitTimeout],
            fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void SessionRecordWriteFailureWithUnconfirmedExitRetainsMatchingPartialRecord()
    {
        var fixture = RecordWriteFailureFixture.Create(
            waitResults: [false, false],
            persistAttemptedRecordBeforeThrow: true);

        var error = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, error.Code);
        Assert.Same(fixture.WriteException, error.InnerException);
        Assert.Contains("state=ProcessExitUnconfirmed", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=False", error.Message, StringComparison.Ordinal);
        Assert.Contains("forced=True", error.Message, StringComparison.Ordinal);
        Assert.Contains("recordRetained=True", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(0, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal(Identity.Pid, fixture.Session.ProcessId);
        Assert.NotNull(fixture.Store.Record);
    }

    [Fact]
    public void SessionRecordWriteFailureCleansOwnedHandleButPreservesNonmatchingPreexistingRecord()
    {
        var preexisting = Record(Guid.NewGuid()) with
        {
            ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddHours(-1)
        };
        var fixture = RecordWriteFailureFixture.Create(
            waitResults: [true],
            preexistingRecord: preexisting);

        var error = Assert.Throws<EtabsLaunchException>(() => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.RecoveryRecordWriteFailed, error.Code);
        Assert.Same(fixture.WriteException, error.InnerException);
        Assert.Contains("state=Succeeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("processExitConfirmed=True", error.Message, StringComparison.Ordinal);
        Assert.Contains("recordRetained=True", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal(preexisting, fixture.Store.Record);
        Assert.DoesNotContain("record-clear", fixture.Events);
    }

    [Fact]
    public void ShutdownExitZeroAndGracefulProcessExitSucceedsAndClearsRecord()
    {
        var fixture = ShutdownFixture.Create(waitResults: [true]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.True(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.Succeeded, result.Data.State);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.Forced);
        Assert.False(result.Data.RecordRetained);
        Assert.Equal(0, result.Data.ApplicationExitReturnCode);
        Assert.Equal(42, result.Data.OwnedPid);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal([ManagedEtabsShutdownMachine.GracefulExitTimeout], fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);
        Assert.Equal(
            [
                "window-guard-dispose",
                "application-exit",
                "wait-10",
                "record-clear",
                "process-handle-release",
                "release-api-references"
            ],
            fixture.Events);
    }

    [Fact]
    public void ShutdownNonzeroExitRemainsFailureAfterForcedConfirmedCleanup()
    {
        var fixture = ShutdownFixture.Create(exitReturnCode: 7, waitResults: [false, true]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.ApplicationExitFailed, result.ErrorCode);
        Assert.Equal(ManagedEtabsShutdownState.ApplicationExitFailed, result.Data.State);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.Forced);
        Assert.False(result.Data.RecordRetained);
        Assert.Equal(7, result.Data.ApplicationExitReturnCode);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Contains("forced=true", result.Error, StringComparison.Ordinal);
        Assert.Equal(
            [ManagedEtabsShutdownMachine.GracefulExitTimeout, ManagedEtabsShutdownMachine.ForcedExitTimeout],
            fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void ShutdownExitExceptionRemainsBoundedFailureAfterConfirmedCleanup()
    {
        var fixture = ShutdownFixture.Create(
            exitException: new TestException(
                new string('m', 600) + "\r\n",
                unchecked((int)0x80004005)),
            waitResults: [true]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.ApplicationExitFailed, result.ErrorCode);
        Assert.Equal(ManagedEtabsShutdownState.ApplicationExitFailed, result.Data.State);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.RecordRetained);
        Assert.Null(result.Data.ApplicationExitReturnCode);
        Assert.Contains("0x80004005", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Error!, char.IsControl);
        Assert.True(result.Error!.Length <= 2048);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void ShutdownExitZeroButLiveProcessForcesExactHandleAndSucceeds()
    {
        var fixture = ShutdownFixture.Create(waitResults: [false, true]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.True(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.Succeeded, result.Data.State);
        Assert.True(result.Data.Forced);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void ShutdownSecondWaitTimeoutRetainsRecoveryRecord()
    {
        var fixture = ShutdownFixture.Create(waitResults: [false, false]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed, result.ErrorCode);
        Assert.Equal(ManagedEtabsShutdownState.ProcessExitUnconfirmed, result.Data.State);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.Forced);
        Assert.True(result.Data.RecordRetained);
        Assert.NotNull(fixture.Store.Record);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(0, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Equal("wait-10", fixture.Events[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShutdownIdentityOrLaunchIdMismatchNeverTargetsProcess(bool launchIdMismatch)
    {
        var fixture = ShutdownFixture.Create();
        fixture.Store.Record = launchIdMismatch
            ? fixture.Store.Record! with { ManagedLaunchRecordId = Guid.NewGuid() }
            : fixture.Store.Record! with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddSeconds(1) };

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.Equal(ManagedEtabsShutdownState.IdentityMismatch, result.Data.State);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.RecordRetained);
        Assert.Equal(0, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(0, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.NotNull(fixture.Store.Record);

        // The mismatch refuses to touch the PROCESS, but the guard is this session's own
        // resource and is retired on every shutdown route — including the ones that
        // terminate without exiting anything.
        Assert.Equal(["window-guard-dispose"], fixture.Events);
    }

    [Fact]
    public void SessionIdentityMismatchRetainsInMemoryOwnedIdentityAndCachedResult()
    {
        var events = new List<string>();
        var launchId = Guid.NewGuid();
        var managed = new FakeManaged(
            Identity,
            launchId,
            events,
            exitReturnCode: 0,
            exitException: null,
            waitResults: [],
            hasExited: false);
        var store = new MemoryStore(events);
        var session = new EtabsSession(
            new FakeLauncher(managed, events),
            new FakeProcesses { Live = Identity },
            store,
            Consented());
        session.GetOrStartOwned();
        store.Record = store.Record! with { ManagedLaunchRecordId = Guid.NewGuid() };

        var first = session.Shutdown();
        session.Dispose();
        var repeated = session.Shutdown();

        Assert.Same(first, repeated);
        Assert.Equal(ManagedEtabsShutdownState.IdentityMismatch, first.Data.State);
        Assert.Equal(Identity.Pid, session.ProcessId);
        Assert.Equal(0, managed.ExitCount);
        Assert.Equal(0, managed.KillCount);
        Assert.Equal(0, managed.WrapperDisposeCount);
        Assert.Equal(0, managed.ProcessHandleReleaseCount);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void ShutdownAlreadyExitedProcessSkipsComAndKillThenClearsRecord()
    {
        var fixture = ShutdownFixture.Create(hasExited: true);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.Forced);
        Assert.Equal(0, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(1, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);

        // Exit was already confirmed, so passive COM cleanup still runs — an early exit is
        // not a reason to keep holding references.
        Assert.Equal(1, fixture.Managed.ApiReferenceReleaseCount);
        Assert.Equal(
            ["window-guard-dispose", "record-clear", "process-handle-release", "release-api-references"],
            fixture.Events);
    }

    [Fact]
    public void ShutdownAlreadyExitedHandleWithIdentityMismatchRetainsRecord()
    {
        var fixture = ShutdownFixture.Create(hasExited: true);
        fixture.Store.Record = fixture.Store.Record! with
        {
            ManagedLaunchRecordId = Guid.NewGuid()
        };

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.False(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.RecordRetained);
        Assert.Equal(0, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Equal(0, fixture.Managed.WrapperDisposeCount);
        Assert.Equal(0, fixture.Managed.ProcessHandleReleaseCount);
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.NotNull(fixture.Store.Record);
        Assert.Equal(["window-guard-dispose"], fixture.Events);

    }

    [Fact]
    public void ManagedWrapperContractIsNotDisposable()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IManagedEtabsApplication)));
        Assert.Null(typeof(IManagedEtabsApplication).GetMethod("Dispose"));
    }

    [Fact]
    public void OrphanCleanupUsesOneExactHandleOperationAndClearsAfterConfirmedExit()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            ExactTerminationResult = new(
                ExactProcessTerminationState.ConfirmedGone,
                Identity)
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.Forced);
        Assert.Equal(1, processes.TerminateExactCalls);
        Assert.Equal(Identity, processes.TerminateExactExpected);
        Assert.Equal(TimeSpan.FromSeconds(10), processes.TerminateExactTimeout);
        Assert.Null(store.Record);
    }

    [Fact]
    public void OrphanCleanupPidReuseIdentityMismatchNeverKillsAndRetainsRecord()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            ExactTerminationResult = new(
                ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                Identity with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddSeconds(1) })
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.Equal(1, processes.TerminateExactCalls);
        Assert.False(processes.ExactHandleKillAttempted);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void OrphanCleanupUnreadableIdentityNeverKillsAndRetainsRecord()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            ExactTerminationResult = new(
                ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                ObservedIdentity: null)
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.False(processes.ExactHandleKillAttempted);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void OrphanCleanupExactHandleExitUnconfirmedRetainsRecord()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            ExactTerminationResult = new(
                ExactProcessTerminationState.ExitUnconfirmed,
                Identity),
            ExactHandleKillAttempted = true
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed, result.ErrorCode);
        Assert.True(processes.ExactHandleKillAttempted);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void OrphanCleanupTrueNotFoundClearsRecordWithoutKill()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            ExactTerminationResult = new(
                ExactProcessTerminationState.NotFound,
                ObservedIdentity: null)
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.Forced);
        Assert.False(processes.ExactHandleKillAttempted);
        Assert.Null(store.Record);
    }

    private static ManagedEtabsSessionRecord Record(Guid launchId) => new(
        1,
        Identity.Pid,
        Identity.ProcessStartTimeUtc,
        Identity.ExecutablePath,
        launchId,
        DateTimeOffset.UtcNow);

    /// <summary>A ready session over a fake managed application, for visibility tests.</summary>
    private sealed class VisibilityFixture(
        EtabsSession session,
        FakeManaged managed,
        List<string> events,
        StringWriter diagnostics)
    {
        public EtabsSession Session { get; } = session;
        public FakeManaged Managed { get; } = managed;
        public List<string> Events { get; } = events;

        /// <summary>What the session actually printed, so success lines are assertable.</summary>
        public StringWriter Diagnostics { get; } = diagnostics;

        public static VisibilityFixture Create(IEnumerable<bool>? waitResults = null)
        {
            var events = new List<string>();
            var launchId = Guid.NewGuid();
            var managed = new FakeManaged(
                Identity,
                launchId,
                events,
                exitReturnCode: 0,
                exitException: null,
                waitResults: waitResults ?? [],
                hasExited: false);
            var store = new MemoryStore(events);
            var diagnostics = new StringWriter();
            var session = new EtabsSession(
                new FakeLauncher(managed, events),
                new FakeProcesses { Live = Identity },
                store,
                new ManagedEtabsShutdownMachine(store),
                Consented(),
                diagnostics);
            return new(session, managed, events, diagnostics);
        }
    }

    private sealed class ShutdownFixture
    {
        private ShutdownFixture(
            MemoryStore store,
            FakeManaged managed,
            ManagedEtabsShutdownMachine machine,
            List<string> events)
        {
            Store = store;
            Managed = managed;
            Machine = machine;
            Events = events;
        }

        public MemoryStore Store { get; }
        public FakeManaged Managed { get; }
        public ManagedEtabsShutdownMachine Machine { get; }
        public List<string> Events { get; }

        public static ShutdownFixture Create(
            int exitReturnCode = 0,
            Exception? exitException = null,
            IEnumerable<bool>? waitResults = null,
            bool hasExited = false)
        {
            var events = new List<string>();
            var launchId = Guid.NewGuid();
            var store = new MemoryStore(events) { Record = Record(launchId) };
            var managed = new FakeManaged(
                Identity,
                launchId,
                events,
                exitReturnCode,
                exitException,
                waitResults ?? [],
                hasExited);
            return new(store, managed, new ManagedEtabsShutdownMachine(store), events);
        }
    }

    private sealed class RecordWriteFailureFixture
    {
        private RecordWriteFailureFixture(
            EtabsSession session,
            MemoryStore store,
            FakeManaged managed,
            IOException writeException,
            List<string> events)
        {
            Session = session;
            Store = store;
            Managed = managed;
            WriteException = writeException;
            Events = events;
        }

        public EtabsSession Session { get; }
        public MemoryStore Store { get; }
        public FakeManaged Managed { get; }
        public IOException WriteException { get; }
        public List<string> Events { get; }

        public static RecordWriteFailureFixture Create(
            IEnumerable<bool> waitResults,
            bool persistAttemptedRecordBeforeThrow = false,
            ManagedEtabsSessionRecord? preexistingRecord = null)
        {
            var events = new List<string>();
            var launchId = Guid.NewGuid();
            var writeException = new IOException("durable record unavailable");
            var store = new MemoryStore(events)
            {
                Record = preexistingRecord,
                WriteException = writeException,
                PersistAttemptedRecordBeforeThrow = persistAttemptedRecordBeforeThrow
            };
            var managed = new FakeManaged(
                Identity,
                launchId,
                events,
                exitReturnCode: 0,
                exitException: null,
                waitResults,
                hasExited: false);
            var session = new EtabsSession(
                new FakeLauncher(managed, events),
                new FakeProcesses { Live = Identity },
                store,
                Consented());
            return new(session, store, managed, writeException, events);
        }
    }

    private sealed class MemoryStore(List<string>? events = null) : ISessionRecordStore
    {
        public string FilePath => @"C:\memory\managed-etabs-session.json";
        public ManagedEtabsSessionRecord? Record { get; set; }
        public Exception? WriteException { get; set; }
        public bool PersistAttemptedRecordBeforeThrow { get; set; }
        public ManagedEtabsSessionRecord? Read() => Record;
        public void Write(ManagedEtabsSessionRecord record)
        {
            events?.Add("record-write");
            if (WriteException is not null)
            {
                if (PersistAttemptedRecordBeforeThrow)
                {
                    Record = record;
                }
                throw WriteException;
            }
            Record = record;
        }
        public void Clear()
        {
            events?.Add("record-clear");
            Record = null;
        }
    }

    private sealed class FakeProcesses : IProcessInspector
    {
        public ManagedProcessIdentity? Live { get; set; }
        public ExactProcessTerminationResult ExactTerminationResult { get; set; } = new(
            ExactProcessTerminationState.NotFound,
            ObservedIdentity: null);
        public int TerminateExactCalls { get; private set; }
        public ManagedProcessIdentity? TerminateExactExpected { get; private set; }
        public TimeSpan? TerminateExactTimeout { get; private set; }
        public bool ExactHandleKillAttempted { get; set; }
        public EtabsProcessObservation ObserveEtabs() =>
            new(Live is null ? [] : [Live], 0);
        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;
        public ManagedProcessIdentity? Find(int pid) => Live?.Pid == pid ? Live : null;
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

    private sealed class FakeLauncher(
        FakeManaged managed,
        List<string> events) : IManagedEtabsLauncher
    {
        public int LaunchCount { get; private set; }
        public EtabsLaunchException? Failure { get; set; }

        public IManagedEtabsApplication Launch()
        {
            LaunchCount++;
            events.Add("launch");
            if (Failure is not null)
            {
                throw Failure;
            }

            events.Add("ownership-proven");
            return managed;
        }
    }

    // ── CLI #25: a cold start needs declared consent ─────────────────────────

    /// <summary>
    /// The gate, in its most important form: with no declared intent, NO PROCESS IS
    /// CREATED.
    ///
    /// <para>This is the whole reason the field exists. CLI #22 established over seven
    /// supervised runs that ETABS puts itself on screen for 8.76–13.42 s during startup and
    /// that neither the API nor Windows can prevent it. The CLI cannot manufacture consent
    /// after the fact — by the time a window is up, asking is pointless — so the refusal has
    /// to happen before <c>cHelper.CreateObject</c>. Asserting the launcher was never
    /// invoked is what makes this a real gate rather than a late apology.</para>
    /// </summary>
    [Fact]
    public void AColdStartWithoutDeclaredConsentIsRefusedBeforeAnyProcessIsCreated()
    {
        var events = new List<string>();
        var managed = Managed(events);
        var launcher = new FakeLauncher(managed, events);
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            new MemoryStore(events),
            new ManagedEtabsStartIntentScope());

        var error = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.VisibleStartConsentMissing, error.Code);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Empty(events);
    }

    /// <summary>
    /// An unrecognised token is not consent either. A newer desktop must not be able to
    /// cold-start an older sidecar by sending an intent this build has never heard of —
    /// failing closed on the unknown is the point of parsing rather than truthiness.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("visible-start-consented-v2")]
    [InlineData("VISIBLE-START-CONSENTED")]
    public void AnUnrecognisedIntentIsRefusedExactlyLikeAMissingOne(string wireValue)
    {
        var events = new List<string>();
        var launcher = new FakeLauncher(Managed(events), events);
        var scope = new ManagedEtabsStartIntentScope();
        _ = scope.Publish(ManagedEtabsStartIntents.Parse(wireValue));
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            new MemoryStore(events),
            scope);

        var error = Assert.Throws<EtabsLaunchException>(() => session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.VisibleStartConsentMissing, error.Code);
        Assert.Equal(0, launcher.LaunchCount);
    }

    /// <summary>The declared value does start a session, so the gate is not simply refusing everything.</summary>
    [Fact]
    public void AColdStartWithDeclaredConsentProceeds()
    {
        var events = new List<string>();
        var managed = Managed(events);
        var launcher = new FakeLauncher(managed, events);
        var scope = new ManagedEtabsStartIntentScope();
        _ = scope.Publish(
            ManagedEtabsStartIntents.Parse(ManagedEtabsStartIntents.VisibleByConsent));
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            new MemoryStore(events),
            scope);

        Assert.Same(managed, session.GetOrStartOwned());
        Assert.Equal(1, launcher.LaunchCount);
    }

    /// <summary>
    /// Consent is scoped to ONE request. A session that was consented for cannot leave its
    /// consent lying around for the next request — otherwise the second background command
    /// of a daemon's life could cold-start ETABS on the strength of a prompt the engineer
    /// answered minutes ago for something else.
    /// </summary>
    [Fact]
    public void ConsentIsClearedWhenTheRequestScopeEnds()
    {
        var scope = new ManagedEtabsStartIntentScope();

        using (scope.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, scope.Current);
        }

        Assert.Equal(ManagedEtabsStartIntent.Unspecified, scope.Current);
    }

    /// <summary>
    /// But an EXISTING session serves later background work without asking again. It is
    /// process creation the engineer agreed to; reusing a session that is already hidden
    /// puts nothing new on screen, and re-prompting for it would train people to click
    /// through the prompt that actually matters.
    /// </summary>
    [Fact]
    public void AnExistingHiddenSessionServesLaterWorkWithoutFurtherConsent()
    {
        var events = new List<string>();
        var managed = Managed(events);
        var launcher = new FakeLauncher(managed, events);
        var scope = new ManagedEtabsStartIntentScope();
        var session = new EtabsSession(
            launcher,
            new FakeProcesses { Live = Identity },
            new MemoryStore(events),
            scope);

        using (scope.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            _ = session.GetOrStartOwned();
        }

        // A later request declaring nothing at all.
        Assert.Equal(ManagedEtabsStartIntent.Unspecified, scope.Current);
        Assert.Same(managed, session.GetOrStartOwned());
        Assert.Equal(1, launcher.LaunchCount);
    }

    /// <summary>
    /// And a session the engineer has been SHOWN is reused as-is. Nothing on the command
    /// path may quietly hide it again — the reveal was explicit, and taking the window away
    /// because a background export happened to run next would be the inverse of the defect
    /// this whole contract exists to fix.
    /// </summary>
    [Fact]
    public void AUserVisibleSessionIsReusedWithoutBeingHiddenAgain()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        Assert.True(fixture.Session.RevealForExplicitUserRequest().Success);
        Assert.Equal(ManagedEtabsVisibilityState.UserVisible, fixture.Managed.VisibilityState);

        var hidesBefore = fixture.Managed.HiddenCalls;
        fixture.Events.Clear();

        // Background work reuses the session.
        _ = fixture.Session.GetOrStartOwned();

        Assert.Equal(hidesBefore, fixture.Managed.HiddenCalls);
        Assert.Equal(ManagedEtabsVisibilityState.UserVisible, fixture.Managed.VisibilityState);
        Assert.DoesNotContain("csi-hide", fixture.Events);
        Assert.DoesNotContain("enter-background-hidden", fixture.Events);
    }

    // ── CLI #24: readiness is temporal, not a final census ───────────────────

    /// <summary>
    /// THE #24 regression at session level. The census says hidden; the accumulated
    /// evidence says the engineer already saw ETABS. Readiness must fail.
    ///
    /// <para>This is the exact shape a prior candidate shipped: "✓ ETABS started hidden"
    /// logged truthfully, seconds after a full-screen ETABS window had been in front of the
    /// engineer for 8.76 s, because the gate asked "is it hidden now?". Deleting the
    /// exposure check turns this green again, which is what makes it load bearing.</para>
    /// </summary>
    [Fact]
    public void AHiddenCensusCannotClearAnExposureThatAlreadyHappened()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Managed.WindowsSuppressionConfirmed = true;
        fixture.Managed.Exposure = new ManagedEtabsExposureEvidence(
            Observed: true,
            Observations: 4,
            First: new ManagedEtabsExposureObservation((nint)0x2A4, new WindowBounds(-8, -8, 1928, 1040), 120),
            Last: new ManagedEtabsExposureObservation((nint)0x2A4, new WindowBounds(-8, -8, 1928, 1040), 8760));

        var error = Assert.Throws<EtabsLaunchException>(
            () => fixture.Session.GetOrStartOwned());

        Assert.Equal(EtabsLaunchErrorCodes.HiddenStateNotEstablished, error.Code);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.UnconsentedExposure,
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("0x2A4", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a clean session still starts. The sticky check must not be a blanket refusal.
    /// </summary>
    [Fact]
    public void ASessionWithNoRecordedExposureBecomesReady()
    {
        var fixture = VisibilityFixture.Create();

        _ = fixture.Session.GetOrStartOwned();

        Assert.False(fixture.Managed.Exposure.Observed);
        Assert.Contains(
            "unconsentedExposure=false",
            fixture.Diagnostics.ToString(),
            StringComparison.Ordinal);
    }

    // ── The reveal is CSI-driven and Windows-certified ───────────────────────

    /// <summary>
    /// A reveal whose CSI call could not be ISSUED fails, and does not go on to ask Windows
    /// to certify a transition nobody requested. Diagnostic #3 measured this class of
    /// failure for real.
    /// </summary>
    [Fact]
    public void ARevealWhoseCsiCallCannotBeIssuedFailsWithoutConsultingWindows()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        fixture.Managed.RevealIssuable = false;
        fixture.Events.Clear();

        var result = fixture.Session.RevealForExplicitUserRequest();

        Assert.False(result.Success);
        Assert.Contains("cOAPI.Unhide", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("confirm-windows-revealed", fixture.Events);
        Assert.NotEqual(
            ManagedEtabsVisibilityState.UserVisible,
            fixture.Managed.VisibilityState);
    }

    /// <summary>
    /// A reveal Windows will not certify fails even though CSI accepted it — and the
    /// session does NOT claim the user can see ETABS. "Open in ETABS" that shows nothing
    /// has not done what was asked.
    /// </summary>
    [Fact]
    public void ARevealWindowsWillNotCertifyFailsAndDoesNotBecomeUserVisible()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        fixture.Managed.WindowsRevealConfirmed = false;

        var result = fixture.Session.RevealForExplicitUserRequest();

        Assert.False(result.Success);
        Assert.NotEqual(
            ManagedEtabsVisibilityState.UserVisible,
            fixture.Managed.VisibilityState);
    }

    /// <summary>
    /// A non-zero CSI return does NOT fail a reveal Windows confirms. Cardex documents
    /// exactly that return for "already visible", and the census is the authority.
    /// </summary>
    [Fact]
    public void ANonZeroUnhideReturnDoesNotFailARevealWindowsConfirms()
    {
        var fixture = VisibilityFixture.Create();
        fixture.Session.GetOrStartOwned();
        fixture.Managed.RevealSucceeds = false;   // issued, but returns non-zero

        var result = fixture.Session.RevealForExplicitUserRequest();

        Assert.True(result.Success);
        Assert.Equal(
            ManagedEtabsVisibilityState.UserVisible,
            fixture.Managed.VisibilityState);
    }

    /// <summary>A managed application fixture with nothing unusual about it.</summary>
    private static FakeManaged Managed(List<string> events) => new(
        Identity,
        Guid.NewGuid(),
        events,
        exitReturnCode: 0,
        exitException: null,
        waitResults: [],
        hasExited: false);

    /// <summary>
    /// A request that declared visible-start consent — the state every one of these
    /// fixtures assumes, because they exercise launch and shutdown behaviour rather than
    /// the consent gate. The refusal path has its own tests, which deliberately do NOT
    /// use this.
    /// </summary>
    private static IManagedEtabsStartIntentScope Consented()
    {
        var scope = new ManagedEtabsStartIntentScope();
        _ = scope.Publish(ManagedEtabsStartIntent.VisibleByConsent);
        return scope;
    }

    private sealed class FakeManaged : IManagedEtabsApplication
    {
        private readonly List<string> _events;
        private readonly int _exitReturnCode;
        private readonly Exception? _exitException;
        private readonly int _initializeReturnCode;
        private readonly Exception? _initializeException;
        private readonly Queue<bool> _waitResults;

        public FakeManaged(
            ManagedProcessIdentity identity,
            Guid launchId,
            List<string> events,
            int exitReturnCode,
            Exception? exitException,
            IEnumerable<bool> waitResults,
            bool hasExited,
            int initializeReturnCode = 0,
            Exception? initializeException = null)
        {
            Identity = identity;
            ManagedLaunchRecordId = launchId;
            _events = events;
            _exitReturnCode = exitReturnCode;
            _exitException = exitException;
            _initializeReturnCode = initializeReturnCode;
            _initializeException = initializeException;
            _waitResults = new(waitResults);
            HasExited = hasExited;
        }

        public ETABSApplication Application =>
            throw new InvalidOperationException("Fake must not expose COM");
        public ManagedProcessIdentity Identity { get; }
        public Guid ManagedLaunchRecordId { get; }
        public bool HasExited { get; private set; }
        public int ExitCount { get; private set; }
        public int KillCount { get; private set; }
        public int WrapperDisposeCount { get; private set; }
        public int ProcessHandleReleaseCount { get; private set; }
        public int InitializeCount { get; private set; }
        public List<TimeSpan> WaitTimeouts { get; } = [];

        public int ReadinessCount { get; private set; }
        public int ApiReferenceReleaseCount { get; private set; }

        public void CompleteApiReadiness()
        {
            _events.Add("wrap-existing");
            ReadinessCount++;
        }

        public bool IsVisible { get; set; } = true;
        public int HiddenCalls { get; private set; }
        public int RevealCalls { get; private set; }
        public bool HideSucceeds { get; set; } = true;
        public bool RevealSucceeds { get; set; } = true;

        /// <summary>Whether the CSI call itself can be made at all. False models a throw.</summary>
        public bool HideIssuable { get; set; } = true;

        public bool RevealIssuable { get; set; } = true;

        public ManagedEtabsVisibilityOutcome ApplyCsiHideForBackgroundWork()
        {
            _events.Add("csi-hide");
            HiddenCalls++;
            if (!HideIssuable)
            {
                return new(
                    ManagedEtabsVisibilityIntent.Hidden,
                    Issued: false,
                    Confirmed: false,
                    ReturnCode: 0,
                    CsiVisibleAfter: null,
                    "ETABS_COM_OPERATION_FAILED; operation=cOAPI.Hide");
            }

            IsVisible = false;
            return new(
                ManagedEtabsVisibilityIntent.Hidden,
                Issued: true,
                Confirmed: HideSucceeds,
                ReturnCode: HideSucceeds ? 0 : 1,
                CsiVisibleAfter: IsVisible,
                HideSucceeds ? null : "ETABS_VISIBILITY_NOT_CONFIRMED; operation=cOAPI.Hide");
        }

        public ManagedEtabsVisibilityOutcome ApplyCsiUnhideForExplicitUserAction()
        {
            _events.Add("csi-unhide");
            RevealCalls++;
            if (!RevealIssuable)
            {
                return new(
                    ManagedEtabsVisibilityIntent.Visible,
                    Issued: false,
                    Confirmed: false,
                    ReturnCode: 0,
                    CsiVisibleAfter: null,
                    "ETABS_COM_OPERATION_FAILED; operation=cOAPI.Unhide");
            }

            IsVisible = true;
            return new(
                ManagedEtabsVisibilityIntent.Visible,
                Issued: true,
                Confirmed: RevealSucceeds,
                ReturnCode: RevealSucceeds ? 0 : 1,
                CsiVisibleAfter: IsVisible,
                RevealSucceeds ? null : "ETABS_VISIBILITY_NOT_CONFIRMED; operation=cOAPI.Unhide");
        }

        public ManagedEtabsVisibilityState VisibilityState { get; private set; } =
            ManagedEtabsVisibilityState.StartingVisibleByConsent;

        /// <summary>CLI #24 evidence the test can plant, to prove readiness reads it.</summary>
        public ManagedEtabsExposureEvidence Exposure { get; set; } =
            ManagedEtabsExposureEvidence.None;

        public int EnterBackgroundHiddenCalls { get; private set; }

        public int EnterUserVisibleCalls { get; private set; }

        public void EnterBackgroundHidden()
        {
            _events.Add("enter-background-hidden");
            EnterBackgroundHiddenCalls++;
            VisibilityState = ManagedEtabsVisibilityState.BackgroundHidden;
        }

        public void EnterUserVisible()
        {
            _events.Add("enter-user-visible");
            EnterUserVisibleCalls++;
            VisibilityState = ManagedEtabsVisibilityState.UserVisible;
        }

        public int WindowGuardReleaseCalls { get; private set; }
        public int WindowGuardDisposeCalls { get; private set; }
        public int SuppressionConfirmations { get; private set; }
        public int RevealConfirmations { get; private set; }

        /// <summary>Whether the exact-owned Windows census agrees that nothing is on screen.</summary>
        public bool WindowsSuppressionConfirmed { get; set; } = true;

        /// <summary>Whether an owned top-level window is Windows-visible after a reveal.</summary>
        public bool WindowsRevealConfirmed { get; set; } = true;

        public ManagedEtabsWindowConfirmation ConfirmWindowsSuppressed()
        {
            _events.Add("confirm-windows-suppressed");
            SuppressionConfirmations++;
            return WindowsSuppressionConfirmed
                ? new(true, 1, TimeSpan.Zero, [], null)
                : new(
                    false,
                    3,
                    TimeSpan.FromSeconds(5),
                    [(nint)0x2A4],
                    $"{ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed}; " +
                    $"ownedPid={Identity.Pid}; handles=[0x2A4]");
        }

        public ManagedEtabsWindowConfirmation ConfirmWindowsRevealed()
        {
            _events.Add("confirm-windows-revealed");
            RevealConfirmations++;
            return WindowsRevealConfirmed
                ? new(true, 1, TimeSpan.Zero, [(nint)0x2A4], null)
                : new(
                    false,
                    3,
                    TimeSpan.FromSeconds(5),
                    [],
                    $"{ManagedEtabsWindowErrorCodes.RevealNotConfirmed}; " +
                    $"ownedPid={Identity.Pid}");
        }

        public void ReleaseWindowGuardForExplicitUserAction()
        {
            _events.Add("window-guard-release");
            WindowGuardReleaseCalls++;
        }

        public void DisposeWindowGuard()
        {
            _events.Add("window-guard-dispose");
            WindowGuardDisposeCalls++;
        }

        public void ReleaseApiReferences()
        {
            _events.Add("release-api-references");
            ApiReferenceReleaseCount++;
        }

        public int InitializeNewModel()
        {
            _events.Add("initialize");
            InitializeCount++;
            if (_initializeException is not null)
            {
                throw _initializeException;
            }
            return _initializeReturnCode;
        }

        public int ExitWithoutSaving()
        {
            _events.Add("application-exit");
            ExitCount++;
            if (_exitException is not null)
            {
                throw _exitException;
            }
            return _exitReturnCode;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            _events.Add($"wait-{timeout.TotalSeconds:0}");
            WaitTimeouts.Add(timeout);
            var result = _waitResults.Count > 0 && _waitResults.Dequeue();
            if (result)
            {
                HasExited = true;
            }
            return result;
        }

        public void Kill()
        {
            _events.Add("kill");
            KillCount++;
        }

        public void Dispose()
        {
            _events.Add("wrapper-dispose");
            WrapperDisposeCount++;
        }

        public void ReleaseOwnedProcessHandle()
        {
            _events.Add("process-handle-release");
            ProcessHandleReleaseCount++;
        }
    }

    private sealed class TestException : Exception
    {
        public TestException(string message, int hresult) : base(message)
        {
            HResult = hresult;
        }
    }
}
