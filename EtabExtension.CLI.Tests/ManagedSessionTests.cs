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
    public void Shutdown_exit_zero_and_graceful_process_exit_succeeds_and_clears_record()
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
        Assert.Equal(["application-exit", "wait-10", "record-clear", "dispose"], fixture.Events);
    }

    [Fact]
    public void Shutdown_nonzero_exit_remains_failure_after_forced_confirmed_cleanup()
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
        Assert.Equal(
            [ManagedEtabsShutdownMachine.GracefulExitTimeout, ManagedEtabsShutdownMachine.ForcedExitTimeout],
            fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void Shutdown_exit_exception_remains_bounded_failure_after_confirmed_cleanup()
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
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void Shutdown_exit_zero_but_live_process_forces_exact_handle_and_succeeds()
    {
        var fixture = ShutdownFixture.Create(waitResults: [false, true]);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.True(result.Success);
        Assert.Equal(ManagedEtabsShutdownState.Succeeded, result.Data.State);
        Assert.True(result.Data.Forced);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.Equal(1, fixture.Managed.KillCount);
        Assert.Null(fixture.Store.Record);
    }

    [Fact]
    public void Shutdown_second_wait_timeout_retains_recovery_record()
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
        Assert.Equal("dispose", fixture.Events[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shutdown_identity_or_launch_id_mismatch_never_targets_process(bool launchIdMismatch)
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
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.NotNull(fixture.Store.Record);
        Assert.Equal(["dispose"], fixture.Events);
    }

    [Fact]
    public void Shutdown_already_exited_process_skips_com_and_kill_then_clears_record()
    {
        var fixture = ShutdownFixture.Create(hasExited: true);

        var result = fixture.Machine.Shutdown(fixture.Managed);

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.Forced);
        Assert.Equal(0, fixture.Managed.ExitCount);
        Assert.Equal(0, fixture.Managed.KillCount);
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.Null(fixture.Store.Record);
        Assert.Equal(["record-clear", "dispose"], fixture.Events);
    }

    [Fact]
    public void Shutdown_already_exited_handle_with_identity_mismatch_retains_record()
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
        Assert.Empty(fixture.Managed.WaitTimeouts);
        Assert.NotNull(fixture.Store.Record);
        Assert.Equal(["dispose"], fixture.Events);
    }

    [Fact]
    public void Orphan_cleanup_terminates_only_full_identity_and_clears_after_confirmed_exit()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses { Live = Identity, WaitResult = true };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.True(result.Data.Forced);
        Assert.Equal(42, processes.TerminatedPid);
        Assert.Equal(TimeSpan.FromSeconds(10), processes.WaitTimeout);
        Assert.Null(store.Record);
    }

    [Fact]
    public void Orphan_cleanup_identity_mismatch_never_terminates_and_retains_record()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses
        {
            Live = Identity with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddSeconds(1) }
        };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, result.ErrorCode);
        Assert.Null(processes.TerminatedPid);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void Orphan_cleanup_unconfirmed_exit_retains_record()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses { Live = Identity, WaitResult = false };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.False(result.Success);
        Assert.Equal(ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed, result.ErrorCode);
        Assert.Equal(42, processes.TerminatedPid);
        Assert.NotNull(store.Record);
    }

    [Fact]
    public void Orphan_cleanup_already_gone_clears_record_without_termination()
    {
        var store = new MemoryStore { Record = Record(Guid.NewGuid()) };
        var processes = new FakeProcesses { Live = null };

        var result = new OrphanSessionCleaner(store, processes).Clean();

        Assert.True(result.Success);
        Assert.True(result.Data.ProcessExitConfirmed);
        Assert.False(result.Data.Forced);
        Assert.Null(processes.TerminatedPid);
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

    private sealed class MemoryStore(List<string>? events = null) : ISessionRecordStore
    {
        public ManagedEtabsSessionRecord? Record { get; set; }
        public ManagedEtabsSessionRecord? Read() => Record;
        public void Write(ManagedEtabsSessionRecord record)
        {
            events?.Add("record-write");
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
        public bool WaitResult { get; set; }
        public int? TerminatedPid { get; private set; }
        public TimeSpan? WaitTimeout { get; private set; }
        public EtabsProcessObservation ObserveEtabs() =>
            new(Live is null ? [] : [Live], 0);
        public ManagedProcessIdentity? Find(int pid) => Live?.Pid == pid ? Live : null;
        public void Terminate(int pid) => TerminatedPid = pid;
        public bool WaitForExit(int pid, TimeSpan timeout)
        {
            WaitTimeout = timeout;
            return WaitResult;
        }
    }

    private sealed class FakeManaged : IManagedEtabsApplication
    {
        private readonly List<string> _events;
        private readonly int _exitReturnCode;
        private readonly Exception? _exitException;
        private readonly Queue<bool> _waitResults;

        public FakeManaged(
            ManagedProcessIdentity identity,
            Guid launchId,
            List<string> events,
            int exitReturnCode,
            Exception? exitException,
            IEnumerable<bool> waitResults,
            bool hasExited)
        {
            Identity = identity;
            ManagedLaunchRecordId = launchId;
            _events = events;
            _exitReturnCode = exitReturnCode;
            _exitException = exitException;
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
        public int DisposeCount { get; private set; }
        public List<TimeSpan> WaitTimeouts { get; } = [];

        public int InitializeNewModel() => 0;

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
            _events.Add("dispose");
            DisposeCount++;
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
