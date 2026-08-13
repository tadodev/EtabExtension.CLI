using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

// The managed startup contract, exercised end to end without ETABS or COM.
//
// Sequence under test (Cardex ETABS 23.3): preflight census -> cHelper.CreateObject
// (which starts the program) -> cOAPI.ApplicationStart() == 0 -> cOAPI.SapModel present
// -> exact OS census -> authoritative owned handle. Initialization and the EtabSharp wrap
// happen afterwards, in EtabsSession, so the record is written between ownership and
// initialization.
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
                "application-start",
                "sap-model",
                "census-ownership",
                "open-exact",
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
            ExactTerminationResult = new(ExactProcessTerminationState.ExitUnconfirmed, Identity)
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
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, processes.TerminateExactCalls);
        Assert.Equal(Identity, processes.TerminateExactExpected);
        Assert.Equal(TimeSpan.FromSeconds(10), processes.TerminateExactTimeout);
        Assert.Equal(0, api.WrapCount);
        Assert.DoesNotContain("open-exact", events);
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
        Assert.Equal(1, processes.TerminateExactCalls);
    }

    [Fact]
    public void MissingSapModelFailsTypedBeforeAnyOwnershipClaim()
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
        Assert.DoesNotContain("open-exact", events);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, processes.TerminateExactCalls);
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
            TextWriter.Null);

        var error = Assert.Throws<EtabsLaunchException>(launcher.Launch);

        Assert.Equal(EtabsLaunchErrorCodes.ProcessIdentityFailed, error.Code);
        Assert.Equal(1, api.ExitCount);
        Assert.Equal(1, owned.KillCount);
        Assert.True(owned.Disposed);

        // The handle was authoritative, so the exact-identity terminate path is not needed.
        Assert.Equal(0, processes.TerminateExactCalls);
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

    // The raw boundary exposes no visibility or attach surface at all, so an early Hide or
    // a ROT lookup is not expressible from managed startup.
    [Fact]
    public void RawApiBoundaryExposesOnlyLifecycleMembers()
    {
        var members = typeof(IEtabsRawApi).GetMembers()
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
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
                "InitializeNewModel",
                "ReleaseApiReferences"
            ],
            members);
    }

    private static ManagedEtabsLauncher Build(
        FakeRawApi api,
        FakeProcesses processes,
        out List<string> events)
    {
        var recorded = new List<string>();
        events = recorded;
        api.Events = recorded;
        processes.Events = recorded;
        return new ManagedEtabsLauncher(
            processes,
            new FakeResolver(Identity.ExecutablePath),
            new FakeApiFactory(api, recorded),
            new FakeVersionProbe(recorded),
            TextWriter.Null);
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
            return StartException is null ? StartReturnCode : throw StartException;
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

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public bool WaitForExit(TimeSpan timeout) => HasExited;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcesses : IProcessInspector
    {
        private bool _preflightObserved;

        public List<string> Events { get; set; } = [];
        public ManagedProcessIdentity[] Preflight { get; set; } = [];
        public ManagedProcessIdentity[] AfterStart { get; set; } = [];
        public int AfterStartUnidentified { get; set; }
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
            return new(AfterStart, AfterStartUnidentified);
        }

        public ManagedProcessIdentity? Find(int pid) =>
            AfterStart.FirstOrDefault(identity => identity.Pid == pid);

        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected)
        {
            Events.Add("open-exact");
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
