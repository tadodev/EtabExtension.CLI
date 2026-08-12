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
        var session = new EtabsSession(launcher, processes, store);

        var first = session.GetOrStartOwned();
        var second = session.GetOrStartOwned();

        Assert.Same(managed, first);
        Assert.Same(first, second);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, managed.InitializeCount);
        Assert.True(session.IsStarted);
        Assert.NotNull(store.Record);
        Assert.Equal(
            ["launch", "ownership-proven", "record-write", "initialize"],
            events);
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
            store);

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
                "application-exit",
                "wait-10",
                "record-clear",
                "process-handle-release"
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
            store);

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
            store);
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
        Assert.Equal(["application-exit", "wait-10", "record-clear", "process-handle-release"], fixture.Events);
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
        Assert.Empty(fixture.Events);
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
            store);
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
        Assert.Equal(["record-clear", "process-handle-release"], fixture.Events);
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
        Assert.Empty(fixture.Events);

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
                store);
            return new(session, store, managed, writeException, events);
        }
    }

    private sealed class MemoryStore(List<string>? events = null) : ISessionRecordStore
    {
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

        public IManagedEtabsApplication Launch()
        {
            LaunchCount++;
            events.Add("launch");
            events.Add("ownership-proven");
            return managed;
        }
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
