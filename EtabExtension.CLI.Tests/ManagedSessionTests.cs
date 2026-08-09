using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class ManagedSessionTests
{
    private static readonly ManagedProcessIdentity Identity = new(
        42, new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero), @"C:\ETABS\ETABS.exe");

    [Fact]
    public void Record_store_round_trips_and_clears()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "record.json");
        try
        {
            var store = new JsonSessionRecordStore(path);
            var record = new ManagedEtabsSessionRecord(1, 42, Identity.ProcessStartTimeUtc,
                Identity.ExecutablePath, Guid.NewGuid(), DateTimeOffset.UtcNow);
            store.Write(record);
            Assert.Equal(record, store.Read());
            store.Clear();
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void Orphan_cleanup_terminates_only_a_full_tuple_match()
    {
        var store = new MemoryStore();
        store.Record = new ManagedEtabsSessionRecord(1, 42, Identity.ProcessStartTimeUtc,
            Identity.ExecutablePath, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var processes = new FakeProcesses { Live = Identity };
        new OrphanSessionCleaner(store, processes).Clean();
        Assert.Equal(42, processes.TerminatedPid);
        Assert.Null(store.Record);

        store.Record = new ManagedEtabsSessionRecord(1, 42, Identity.ProcessStartTimeUtc,
            Identity.ExecutablePath, Guid.NewGuid(), DateTimeOffset.UtcNow);
        processes.Live = Identity with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddSeconds(1) };
        processes.TerminatedPid = null;
        new OrphanSessionCleaner(store, processes).Clean();
        Assert.Null(processes.TerminatedPid);
    }

    [Fact]
    public void Identity_match_requires_pid_start_time_and_executable_path()
    {
        var record = new ManagedEtabsSessionRecord(1, Identity.Pid, Identity.ProcessStartTimeUtc,
            Identity.ExecutablePath, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.True(OrphanSessionCleaner.IdentityMatches(record, Identity));
        Assert.False(OrphanSessionCleaner.IdentityMatches(record, Identity with { Pid = 43 }));
        Assert.False(OrphanSessionCleaner.IdentityMatches(record,
            Identity with { ProcessStartTimeUtc = Identity.ProcessStartTimeUtc.AddMilliseconds(1) }));
        Assert.False(OrphanSessionCleaner.IdentityMatches(record,
            Identity with { ExecutablePath = @"C:\Other\ETABS.exe" }));
    }

    [Fact]
    public void Session_launches_once_verifies_each_use_and_cleans_once()
    {
        var store = new MemoryStore();
        var processes = new FakeProcesses { Live = Identity };
        var handle = new FakeManaged(Identity, Guid.NewGuid());
        var launcher = new FakeLauncher(handle);
        var session = new EtabsSession(launcher, processes, store);

        Assert.Same(handle, session.GetOrStartOwned());
        Assert.Same(handle, session.GetOrStartOwned());
        Assert.Equal(1, launcher.Count);
        Assert.Equal(2, processes.FindCount);

        session.Shutdown();
        session.Shutdown();
        Assert.Equal(1, handle.ExitCount);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Null(store.Record);
    }

    [Fact]
    public void Session_rejects_launch_record_id_mismatch()
    {
        var store = new MemoryStore { RewriteLaunchId = true };
        var processes = new FakeProcesses { Live = Identity };
        var session = new EtabsSession(
            new FakeLauncher(new FakeManaged(Identity, Guid.NewGuid())), processes, store);
        Assert.Throws<InvalidOperationException>(() => session.GetOrStartOwned());
    }

    private sealed class MemoryStore : ISessionRecordStore
    {
        public ManagedEtabsSessionRecord? Record { get; set; }
        public bool RewriteLaunchId { get; init; }
        public ManagedEtabsSessionRecord? Read() => Record;
        public void Write(ManagedEtabsSessionRecord record) =>
            Record = RewriteLaunchId ? record with { ManagedLaunchRecordId = Guid.NewGuid() } : record;
        public void Clear() => Record = null;
    }

    private sealed class FakeProcesses : IProcessInspector
    {
        public ManagedProcessIdentity? Live { get; set; }
        public int? TerminatedPid { get; set; }
        public int FindCount { get; private set; }
        public EtabsProcessObservation ObserveEtabs() =>
            new(Live is null ? [] : [Live], 0);
        public ManagedProcessIdentity? Find(int pid) { FindCount++; return Live?.Pid == pid ? Live : null; }
        public void Terminate(int pid) => TerminatedPid = pid;
        public bool WaitForExit(int pid, TimeSpan timeout) => true;
    }

    private sealed class FakeManaged(ManagedProcessIdentity identity, Guid launchId) : IManagedEtabsApplication
    {
        public ETABSApplication Application => throw new InvalidOperationException("Fake must not expose COM");
        public ManagedProcessIdentity Identity { get; } = identity;
        public Guid ManagedLaunchRecordId { get; } = launchId;
        public int ExitCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void ExitWithoutSaving() => ExitCount++;
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeLauncher(IManagedEtabsApplication handle) : IManagedEtabsLauncher
    {
        public int Count { get; private set; }
        public IManagedEtabsApplication Launch() { Count++; return handle; }
    }
}
