using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ManagedEtabsLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-launcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitConfiguredExecutableWinsOverDiscovery()
    {
        var configured = CreateExecutable("configured", "ETABS.exe");
        var discovered = CreateExecutable("registry", "ETABS.exe");
        var configuration = new ConfigurationManager
        {
            [EtabsExecutableResolver.ConfigurationKey] = configured
        };

        var resolved = new EtabsExecutableResolver(
            configuration,
            new FakeDiscovery([discovered], [])).Resolve();

        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void RegistryCandidatePrecedesDefaultInstallCandidate()
    {
        var registry = CreateExecutable("registry", "ETABS.exe");
        var fallback = CreateExecutable("default", "ETABS.exe");

        var resolved = new EtabsExecutableResolver(
            new ConfigurationManager(),
            new FakeDiscovery([registry], [fallback])).Resolve();

        Assert.Equal(Path.GetFullPath(registry), resolved);
    }

    [Fact]
    public void ExplicitMissingExecutableReturnsStableNotFoundError()
    {
        var missing = Path.Combine(_directory, "missing", "ETABS.exe");
        var configuration = new ConfigurationManager
        {
            [EtabsExecutableResolver.ConfigurationKey] = missing
        };

        var error = Assert.Throws<EtabsLaunchException>(() =>
            new EtabsExecutableResolver(configuration, new FakeDiscovery([], [])).Resolve());

        Assert.Equal(EtabsLaunchErrorCodes.ExecutableNotFound, error.Code);
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingConfigAndDiscoveryReturnsStableUnresolvedError()
    {
        var error = Assert.Throws<EtabsLaunchException>(() =>
            new EtabsExecutableResolver(
                new ConfigurationManager(),
                new FakeDiscovery([], [])).Resolve());

        Assert.Equal(EtabsLaunchErrorCodes.ExecutableUnresolved, error.Code);
        Assert.Contains(EtabsExecutableResolver.ConfigurationKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostLaunchAmbiguityCleansOnlyOwnedApplication(bool unidentified)
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var secondObservation = unidentified
            ? new EtabsProcessObservation([owned.Identity], 1)
            : Observation(owned.Identity, Identity(99));
        var processes = new FakeProcesses([Observation(), secondObservation]);
        var connector = new FakeConnector(succeedOnAttempt: 1);
        var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal([42], connector.RequestedPids);
        Assert.Equal(1, connector.Managed!.ExitCount);
        Assert.Equal(0, connector.Managed.WrapperDisposeCount);
        Assert.Equal(1, connector.Managed.ProcessHandleReleaseCount);
        Assert.Equal(1, owned.KillCount);
        Assert.Equal(1, owned.WaitForExitCount);
        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(processes.TerminatedPids);
    }

    [Fact]
    public void PostLaunchAmbiguityKillsOwnedProcessWhenComExitThrows()
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var processes = new FakeProcesses(
            [Observation(), Observation(owned.Identity, Identity(99))]);
        var connector = new FakeConnector(succeedOnAttempt: 1, throwOnExit: true);
        var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(1, connector.Managed!.ExitCount);
        Assert.Equal(1, owned.KillCount);
        Assert.Equal(1, owned.WaitForExitCount);
        Assert.Equal(0, connector.Managed.WrapperDisposeCount);
        Assert.Equal(1, connector.Managed.ProcessHandleReleaseCount);
        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(processes.TerminatedPids);
        Assert.DoesNotContain(99, processes.TerminatedPids);
    }

    [Fact]
    public void PostLaunchAmbiguityDoesNotReleaseHandleWhenExitIsUnconfirmed()
    {
        var owned = new FakeOwnedProcess(Identity(42), waitForExitResult: false);
        var processes = new FakeProcesses(
            [Observation(), Observation(owned.Identity, Identity(99))]);
        var connector = new FakeConnector(succeedOnAttempt: 1);
        var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(1, connector.Managed!.ExitCount);
        Assert.Equal(1, owned.KillCount);
        Assert.Equal(1, owned.WaitForExitCount);
        Assert.Equal(0, connector.Managed.WrapperDisposeCount);
        Assert.Equal(0, connector.Managed.ProcessHandleReleaseCount);
        Assert.Equal(0, owned.DisposeCount);
    }

    [Fact]
    public void AttachRetriesOnlyTheOwnedPidUntilSuccess()
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var connector = new FakeConnector(succeedOnAttempt: 3);
        var clock = new FakeClock();
        var launcher = CreateLauncher(
            owned,
            new FakeProcesses([Observation(), Observation(owned.Identity)]),
            connector,
            new StringWriter(),
            clock);

        var result = launcher.Launch();

        Assert.Equal([42, 42, 42], connector.RequestedPids);
        Assert.Equal(2, clock.SleepCount);
        Assert.Equal(0, owned.KillCount);
        Assert.Same(connector.Managed, result);
        Assert.Equal(0, connector.Managed!.WrapperDisposeCount);
        Assert.Equal(0, connector.Managed.ProcessHandleReleaseCount);
    }

    [Fact]
    public void AttachDeadlineFailureKillsAndDisposesOnlyTheOwnedProcess()
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var processes = new FakeProcesses([Observation(), Observation(Identity(99))]);
        var connector = new FakeConnector(succeedOnAttempt: null);
        var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.AttachTimeout, error.Code);
        Assert.All(connector.RequestedPids, pid => Assert.Equal(42, pid));
        Assert.Equal(1, owned.KillCount);
        Assert.Equal(1, owned.WaitForExitCount);
        Assert.Equal(1, owned.DisposeCount);
        Assert.Empty(processes.TerminatedPids);
    }

    [Fact]
    public void OwnershipResolverClassifiesObservedProcesses()
    {
        Assert.Equal(
            EtabsInstanceOwnership.None,
            EtabsOwnershipResolver.Resolve(Observation(), null));
        Assert.Equal(
            EtabsInstanceOwnership.External,
            EtabsOwnershipResolver.Resolve(Observation(Identity(99)), null));
        Assert.Equal(
            EtabsInstanceOwnership.Managed,
            EtabsOwnershipResolver.Resolve(Observation(Identity(42)), 42));
        Assert.Equal(
            EtabsInstanceOwnership.Ambiguous,
            EtabsOwnershipResolver.Resolve(Observation(Identity(99)), 42));
        Assert.Equal(
            EtabsInstanceOwnership.Ambiguous,
            EtabsOwnershipResolver.Resolve(Observation(Identity(42), Identity(99)), 42));
        Assert.Equal(
            EtabsInstanceOwnership.Ambiguous,
            EtabsOwnershipResolver.Resolve(new EtabsProcessObservation([], 1), null));
    }

    [Fact]
    public void LaunchRejectsExternalProcessBeforeStartingEtabs()
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var starter = new FakeStarter(owned);
        var launcher = CreateLauncher(
            owned,
            new FakeProcesses([Observation(Identity(99))]),
            new FakeConnector(succeedOnAttempt: 1),
            new StringWriter(),
            starter: starter);

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(0, starter.StartCount);
    }

    [Fact]
    public void LaunchRejectsUnidentifiedProcessBeforeStartingEtabs()
    {
        var owned = new FakeOwnedProcess(Identity(42));
        var starter = new FakeStarter(owned);
        var launcher = CreateLauncher(
            owned,
            new FakeProcesses([new EtabsProcessObservation([], 1)]),
            new FakeConnector(succeedOnAttempt: 1),
            new StringWriter(),
            starter: starter);

        var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

        Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
        Assert.Equal(0, starter.StartCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ManagedEtabsLauncher CreateLauncher(
        FakeOwnedProcess owned,
        FakeProcesses processes,
        FakeConnector connector,
        TextWriter diagnostics,
        FakeClock? clock = null,
        FakeStarter? starter = null) => new(
            processes,
            new FixedResolver(@"C:\ETABS\ETABS.exe"),
            starter ?? new FakeStarter(owned),
            connector,
            clock ?? new FakeClock(),
            diagnostics);

    private string CreateExecutable(string directory, string fileName)
    {
        var path = Path.Combine(_directory, directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake executable");
        return path;
    }

    private static ManagedProcessIdentity Identity(int pid) => new(
        pid,
        new DateTimeOffset(2026, 7, 18, 1, 2, pid % 60, TimeSpan.Zero),
        $@"C:\ETABS-{pid}\ETABS.exe");

    private static EtabsProcessObservation Observation(params ManagedProcessIdentity[] identified) =>
        new(identified, 0);

    private sealed class FakeDiscovery(
        IReadOnlyList<string> registry,
        IReadOnlyList<string> defaults) : IEtabsInstallDiscovery
    {
        public IReadOnlyList<string> RegistryCandidates() => registry;
        public IReadOnlyList<string> DefaultInstallCandidates() => defaults;
    }

    private sealed class FixedResolver(string path) : IEtabsExecutableResolver
    {
        public string Resolve() => path;
    }

    private sealed class FakeStarter(FakeOwnedProcess process) : IEtabsProcessStarter
    {
        public IOwnedEtabsProcess Start(string executablePath)
        {
            StartCount++;
            StartedPath = executablePath;
            return process;
        }

        public string? StartedPath { get; private set; }
        public int StartCount { get; private set; }
    }

    private sealed class FakeOwnedProcess(
        ManagedProcessIdentity identity,
        bool waitForExitResult = true) : IOwnedEtabsProcess
    {
        public ManagedProcessIdentity Identity { get; } = identity;
        public bool HasExited { get; set; }
        public int KillCount { get; private set; }
        public int WaitForExitCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Kill()
        {
            KillCount++;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            WaitForExitCount++;
            if (waitForExitResult)
            {
                HasExited = true;
            }
            return waitForExitResult;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeConnector(
        int? succeedOnAttempt,
        bool throwOnExit = false) : IManagedEtabsConnector
    {
        public List<int> RequestedPids { get; } = [];
        public FakeManaged? Managed { get; private set; }

        public IManagedEtabsApplication? TryConnect(
            IOwnedEtabsProcess process,
            Guid launchRecordId,
            out string? error)
        {
            RequestedPids.Add(process.Identity.Pid);
            if (succeedOnAttempt == RequestedPids.Count)
            {
                error = null;
                Managed = new FakeManaged(process, launchRecordId, throwOnExit);
                return Managed;
            }

            error = "COM server not ready";
            return null;
        }
    }

    private sealed class FakeManaged(
        IOwnedEtabsProcess process,
        Guid launchRecordId,
        bool throwOnExit) : IManagedEtabsApplication
    {
        public ETABSApplication Application =>
            throw new InvalidOperationException("Fake must not expose COM");
        public ManagedProcessIdentity Identity => process.Identity;
        public Guid ManagedLaunchRecordId { get; } = launchRecordId;
        public bool HasExited => process.HasExited;
        public int ExitCount { get; private set; }
        public int WrapperDisposeCount { get; private set; }
        public int ProcessHandleReleaseCount { get; private set; }
        public int InitializeNewModel() => 0;
        public int ExitWithoutSaving()
        {
            ExitCount++;
            if (throwOnExit)
            {
                throw new InvalidOperationException("COM exit failed");
            }
            return 0;
        }
        public bool WaitForExit(TimeSpan timeout) => process.WaitForExit(timeout);
        public void Kill() => process.Kill();
        public void Dispose()
        {
            WrapperDisposeCount++;
            process.Dispose();
        }
        public void ReleaseOwnedProcessHandle()
        {
            ProcessHandleReleaseCount++;
            process.Dispose();
        }
    }

    private sealed class FakeClock : IEtabsLaunchClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        public int SleepCount { get; private set; }

        public void Sleep(TimeSpan duration)
        {
            SleepCount++;
            UtcNow += duration;
        }
    }

    private sealed class FakeProcesses : IProcessInspector
    {
        private readonly Queue<EtabsProcessObservation> _observations;
        private EtabsProcessObservation _last = Observation();

        public FakeProcesses(IEnumerable<EtabsProcessObservation> observations) =>
            _observations = new(observations);

        public List<int> TerminatedPids { get; } = [];

        public EtabsProcessObservation ObserveEtabs()
        {
            if (_observations.Count > 0)
            {
                _last = _observations.Dequeue();
            }

            return _last;
        }

        public ManagedProcessIdentity? Find(int pid) =>
            _last.Identified.FirstOrDefault(identity => identity.Pid == pid);
        public ExactProcessTerminationResult TerminateExact(
            ManagedProcessIdentity expected,
            TimeSpan timeout) => throw new NotSupportedException();
    }
}
