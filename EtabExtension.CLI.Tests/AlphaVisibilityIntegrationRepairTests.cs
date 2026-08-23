using System.Text.Json;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// Regression proof for the final CLI Alpha visibility-integration repair. Everything here
/// is ETABS-free: the real operation/session orchestration runs against a deterministic
/// managed-application seam and a worker that deliberately does NOT inherit request ambient
/// context, matching the queued STA boundary that exposed the original consent loss.
/// </summary>
public sealed class AlphaVisibilityIntegrationRepairTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-alpha-visibility-repair", Guid.NewGuid().ToString("N"));
    private OperationManager? _manager;

    [Fact]
    public async Task Deferred_operation_keeps_request_consent_and_first_command_stage_after_cold_readiness()
    {
        using var fixture = SessionFixture.Create();
        var worker = new ContextIsolatingGateWorker();
        _manager = CreateManager(
            worker,
            fixture.Scope,
            new DelegateOperation("analyze-and-extract", (_, _) =>
            {
                fixture.Session.GetOrStartOwned();
                return Task.FromResult<object>(Result.Ok());
            }));

        Result<StartOperationData> started;
        using (fixture.Scope.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            var context = _manager.CaptureEtabsContext("analyze-and-extract");
            started = _manager.Start(
                "analyze-and-extract",
                EmptyPayload(),
                context,
                fixture.Session);
        }

        // The request has returned and its ambient scope is gone before the STA is released.
        Assert.Equal(ManagedEtabsStartIntent.Unspecified, fixture.Scope.Current);
        worker.Release();

        var result = Assert.IsType<Result>(
            await _manager.WaitAsync(started.Data!.OperationId),
            exactMatch: false);

        Assert.True(result.Success);
        Assert.Equal(1, fixture.Launcher.LaunchCount);
        Assert.Contains("cSapModel.InitializeNewModel", fixture.Managed.Stages);
        Assert.Equal("analyze-and-extract", fixture.Managed.Stages[^1]);
    }

    [Fact]
    public async Task Later_request_cannot_overwrite_queued_operation_start_intent()
    {
        using var fixture = SessionFixture.Create();
        var worker = new ContextIsolatingGateWorker();
        ManagedEtabsStartIntent? observedInsideOperation = null;
        _manager = CreateManager(
            worker,
            fixture.Scope,
            new DelegateOperation("analyze-and-extract", (_, context) =>
            {
                observedInsideOperation = fixture.Scope.Current;
                Assert.Equal("analyze-and-extract", context.Etabs.VisibilityStage);
                fixture.Session.GetOrStartOwned();
                return Task.FromResult<object>(Result.Ok());
            }));

        Result<StartOperationData> started;
        EtabsOperationContext captured;
        using (fixture.Scope.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            captured = _manager.CaptureEtabsContext("analyze-and-extract");
            started = _manager.Start(
                "analyze-and-extract",
                EmptyPayload(),
                captured,
                fixture.Session);
        }

        // Simulate a later protocol request while the operation is still queued. Its
        // execution-local value and stage are different, but the queued record is immutable.
        using (fixture.Scope.Publish(ManagedEtabsStartIntent.Unspecified))
        {
            var later = _manager.CaptureEtabsContext("get-status");
            Assert.Equal(ManagedEtabsStartIntent.Unspecified, later.StartIntent);
            Assert.Equal("get-status", later.VisibilityStage);

            worker.Release();
            var result = Assert.IsType<Result>(
                await _manager.WaitAsync(started.Data!.OperationId),
                exactMatch: false);
            Assert.True(result.Success);
        }

        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, captured.StartIntent);
        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, observedInsideOperation);
        Assert.Equal("analyze-and-extract", fixture.Managed.Stages[^1]);
    }

    [Fact]
    public async Task Async_analyze_exposure_found_by_final_forced_census_fails_and_terminates_operation()
    {
        using var fixture = SessionFixture.Create(exposeOnSuppressionConfirmation: 2);
        _manager = CreateManager(
            new ContextIsolatingWorker(),
            fixture.Scope,
            new DelegateOperation("analyze-and-extract", (_, _) =>
            {
                fixture.Session.GetOrStartOwned();
                return Task.FromResult<object>(Result.Ok());
            }));

        var started = StartWithConsent(_manager, fixture, "analyze-and-extract");
        var result = Assert.IsType<Result>(
            await _manager.WaitAsync(started.Data!.OperationId),
            exactMatch: false);

        Assert.False(result.Success);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.UnconsentedExposure,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("analyze-and-extract", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Managed.SuppressionConfirmations);
        Assert.True(fixture.Managed.ExposureInjectedByForcedConfirmation);
        Assert.False(fixture.Session.IsStarted);
        Assert.Equal(1, fixture.Managed.ExitCount);
        Assert.Equal(OperationPhase.Failed, _manager.GetStatus(started.Data.OperationId).Data!.Phase);
    }

    [Fact]
    public async Task Action_throw_plus_visibility_breach_terminates_and_preserves_both_bounded_diagnostics()
    {
        using var fixture = SessionFixture.Create(exposeOnSuppressionConfirmation: 2);
        _manager = CreateManager(
            new ContextIsolatingWorker(),
            fixture.Scope,
            new DelegateOperation("analyze-and-extract", (_, _) =>
            {
                fixture.Session.GetOrStartOwned();
                return Task.FromException<object>(new InvalidOperationException("analysis exploded"));
            }));

        var started = StartWithConsent(_manager, fixture, "analyze-and-extract");
        var result = Assert.IsType<Result>(
            await _manager.WaitAsync(started.Data!.OperationId),
            exactMatch: false);

        Assert.False(result.Success);
        Assert.Contains("actionFailure=", result.Error, StringComparison.Ordinal);
        Assert.Contains("analysis exploded", result.Error, StringComparison.Ordinal);
        Assert.Contains("visibilityCertification=", result.Error, StringComparison.Ordinal);
        Assert.Contains(
            ManagedEtabsWindowErrorCodes.UnconsentedExposure,
            result.Error,
            StringComparison.Ordinal);
        Assert.True(result.Error!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
        Assert.False(fixture.Session.IsStarted);
        Assert.Equal(1, fixture.Managed.ExitCount);
    }

    [Fact]
    public async Task Action_throw_with_clean_visibility_runs_certification_and_preserves_original_failure()
    {
        using var fixture = SessionFixture.Create();
        _manager = CreateManager(
            new ContextIsolatingWorker(),
            fixture.Scope,
            new DelegateOperation("analyze-and-extract", (_, _) =>
            {
                fixture.Session.GetOrStartOwned();
                return Task.FromException<object>(new InvalidOperationException("analysis exploded"));
            }));

        var started = StartWithConsent(_manager, fixture, "analyze-and-extract");
        var result = Assert.IsType<Result>(
            await _manager.WaitAsync(started.Data!.OperationId),
            exactMatch: false);

        Assert.False(result.Success);
        Assert.Equal("Operation failed: analysis exploded", result.Error);
        Assert.Equal(2, fixture.Managed.SuppressionConfirmations);
        Assert.True(fixture.Session.IsStarted);
        Assert.Equal(0, fixture.Managed.ExitCount);
    }

    private OperationManager CreateManager(
        IStaExecutionWorker worker,
        IManagedEtabsStartIntentScope scope,
        IOperationDefinition definition) => new(
            worker,
            new OperationEventJournalFactory(_directory),
            new SystemOperationClock(),
            [definition],
            scope);

    private static Result<StartOperationData> StartWithConsent(
        OperationManager manager,
        SessionFixture fixture,
        string stage)
    {
        using var consent = fixture.Scope.Publish(ManagedEtabsStartIntent.VisibleByConsent);
        var context = manager.CaptureEtabsContext(stage);
        return manager.Start(stage, EmptyPayload(), context, fixture.Session);
    }

    private static JsonElement EmptyPayload() => JsonSerializer.Deserialize<JsonElement>("{}");

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DelegateOperation(
        string kind,
        Func<JsonElement, OperationExecutionContext, Task<object>> execute) : IOperationDefinition
    {
        public string Kind { get; } = kind;
        public TimeSpan OperationBudget => TimeSpan.FromMinutes(10);
        public TimeSpan StepBudget => TimeSpan.FromMinutes(5);
        public Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context) =>
            execute(payload, context);
    }

    /// <summary>
    /// Mimics the real manual STA queue's crucial property: request ExecutionContext is not
    /// inherited. The operation can run only after Release, so the request scope can be
    /// deterministically gone or replaced first.
    /// </summary>
    private sealed class ContextIsolatingGateWorker : IStaExecutionWorker
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(async () =>
                {
                    await _release.Task;
                    return await action();
                });
            }
        }

        public void Release() => _release.TrySetResult();
        public void Dispose() { }
    }

    private sealed class ContextIsolatingWorker : IStaExecutionWorker
    {
        public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            using (ExecutionContext.SuppressFlow())
            {
                return Task.Run(action);
            }
        }

        public void Dispose() { }
    }

    private sealed class SessionFixture : IDisposable
    {
        private SessionFixture(
            ManagedEtabsStartIntentScope scope,
            FakeManagedEtabsApplication managed,
            FakeManagedEtabsLauncher launcher,
            EtabsSession session)
        {
            Scope = scope;
            Managed = managed;
            Launcher = launcher;
            Session = session;
        }

        public ManagedEtabsStartIntentScope Scope { get; }
        public FakeManagedEtabsApplication Managed { get; }
        public FakeManagedEtabsLauncher Launcher { get; }
        public EtabsSession Session { get; }

        public static SessionFixture Create(int? exposeOnSuppressionConfirmation = null)
        {
            var scope = new ManagedEtabsStartIntentScope();
            var managed = new FakeManagedEtabsApplication(exposeOnSuppressionConfirmation);
            var launcher = new FakeManagedEtabsLauncher(managed);
            var records = new MemorySessionRecordStore();
            var session = new EtabsSession(
                launcher,
                new FakeProcessInspector(managed),
                records,
                scope);
            return new SessionFixture(scope, managed, launcher, session);
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeManagedEtabsLauncher(FakeManagedEtabsApplication managed)
        : IManagedEtabsLauncher
    {
        public int LaunchCount { get; private set; }

        public IManagedEtabsApplication Launch()
        {
            LaunchCount++;
            return managed;
        }
    }

    private sealed class FakeProcessInspector(FakeManagedEtabsApplication managed) : IProcessInspector
    {
        public EtabsProcessObservation ObserveEtabs() => new([managed.Identity], 0);
        public ManagedProcessIdentity? Find(int pid) =>
            pid == managed.Identity.Pid ? managed.Identity : null;
        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;
        public ExactProcessTerminationResult TerminateExact(
            ManagedProcessIdentity expected,
            TimeSpan timeout) => throw new NotSupportedException();
    }

    private sealed class MemorySessionRecordStore : ISessionRecordStore
    {
        public string FilePath => "memory://managed-etabs-session";
        public ManagedEtabsSessionRecord? Record { get; private set; }
        public ManagedEtabsSessionRecord? Read() => Record;
        public void Write(ManagedEtabsSessionRecord record) => Record = record;
        public void Clear() => Record = null;
    }

    private sealed class FakeManagedEtabsApplication : IManagedEtabsApplication
    {
        private readonly int? _exposeOnSuppressionConfirmation;
        private string _stage = "unknown";
        private bool _hasExited;

        public FakeManagedEtabsApplication(int? exposeOnSuppressionConfirmation)
        {
            _exposeOnSuppressionConfirmation = exposeOnSuppressionConfirmation;
            Identity = new ManagedProcessIdentity(
                4242,
                new DateTimeOffset(2026, 8, 23, 1, 2, 3, TimeSpan.Zero),
                Path.Combine(Path.GetTempPath(), "ETABS.exe"));
            ManagedLaunchRecordId = Guid.NewGuid();
        }

        public ETABSApplication Application => throw new NotSupportedException();
        public ManagedProcessIdentity Identity { get; }
        public Guid ManagedLaunchRecordId { get; }
        public ManagedEtabsVisibilityState VisibilityState { get; private set; } =
            ManagedEtabsVisibilityState.BackgroundHidden;
        public ManagedEtabsExposureEvidence Exposure { get; private set; } =
            ManagedEtabsExposureEvidence.None;
        public List<string> Stages { get; } = [];
        public int SuppressionConfirmations { get; private set; }
        public bool ExposureInjectedByForcedConfirmation { get; private set; }
        public int ExitCount { get; private set; }
        public int KillCount { get; private set; }
        public int DisposeWindowGuardCount { get; private set; }
        public int ReleaseOwnedProcessHandleCount { get; private set; }
        public int ReleaseApiReferencesCount { get; private set; }
        public bool HasExited => _hasExited;

        public int InitializeNewModel() => 0;
        public void CompleteApiReadiness() { }

        public ManagedEtabsVisibilityOutcome ApplyCsiHideForBackgroundWork() => new(
            ManagedEtabsVisibilityIntent.Hidden,
            Issued: true,
            Confirmed: true,
            ReturnCode: 0,
            CsiVisibleAfter: false,
            Diagnostic: null);

        public ManagedEtabsVisibilityOutcome ApplyCsiUnhideForExplicitUserAction() => new(
            ManagedEtabsVisibilityIntent.Visible,
            Issued: true,
            Confirmed: true,
            ReturnCode: 0,
            CsiVisibleAfter: true,
            Diagnostic: null);

        public void BeginExplicitReveal() => VisibilityState = ManagedEtabsVisibilityState.RevealPending;
        public void EnterUserVisible() => VisibilityState = ManagedEtabsVisibilityState.UserVisible;

        public void MarkVisibilityStage(string stage)
        {
            _stage = stage;
            Stages.Add(stage);
        }

        public ManagedEtabsWindowConfirmation ConfirmWindowsSuppressedAndCloseConsentInterval() =>
            ConfirmedSuppressed();

        public ManagedEtabsWindowConfirmation ConfirmWindowsSuppressed()
        {
            SuppressionConfirmations++;
            if (_exposeOnSuppressionConfirmation == SuppressionConfirmations)
            {
                ExposureInjectedByForcedConfirmation = true;
                var observation = new ManagedEtabsExposureObservation(
                    (nint)0x2A4,
                    new WindowBounds(-8, -8, 1928, 1040),
                    7,
                    _stage);
                Exposure = new ManagedEtabsExposureEvidence(
                    Observed: true,
                    Observations: 1,
                    First: observation,
                    Last: observation,
                    ObservedTotalVisibleMs: 1,
                    ObservedMaxContiguousVisibleMs: 1,
                    ObservationDurationMs: 7);
                return new ManagedEtabsWindowConfirmation(
                    Confirmed: false,
                    Observations: 1,
                    Waited: TimeSpan.Zero,
                    ObservedWindows: [(nint)0x2A4],
                    Diagnostic: ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed);
            }

            return ConfirmedSuppressed();
        }

        public ManagedEtabsWindowConfirmation ConfirmWindowsRevealed() => new(
            Confirmed: true,
            Observations: 1,
            Waited: TimeSpan.Zero,
            ObservedWindows: [(nint)0x2A4],
            Diagnostic: null);

        public void ReleaseWindowGuardForExplicitUserAction() =>
            VisibilityState = ManagedEtabsVisibilityState.RevealPending;

        public void DisposeWindowGuard()
        {
            DisposeWindowGuardCount++;
            VisibilityState = ManagedEtabsVisibilityState.Retired;
        }

        public int ExitWithoutSaving()
        {
            ExitCount++;
            return 0;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            _hasExited = true;
            return true;
        }

        public void Kill()
        {
            KillCount++;
            _hasExited = true;
        }

        public void ReleaseOwnedProcessHandle() => ReleaseOwnedProcessHandleCount++;
        public void ReleaseApiReferences() => ReleaseApiReferencesCount++;

        private static ManagedEtabsWindowConfirmation ConfirmedSuppressed() => new(
            Confirmed: true,
            Observations: 1,
            Waited: TimeSpan.Zero,
            ObservedWindows: [],
            Diagnostic: null);
    }
}
