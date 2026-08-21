using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeOperationDispatcherTests : IDisposable
{
    private static readonly string[] ExpectedCapabilities =
    [
        "analyze-and-extract",
        "cancel-operation",
        "close-model",
        "extract-materials",
        "extract-results",
        "generate-e2k",
        "get-model-state",
        "get-operation-events",
        "get-operation-status",
        "get-status",
        "inspect-wall-property",
        "list-wall-properties",
        "open-model",
        "read-model-metadata",
        "resolve-area-targets",
        "run-analysis",
        "snapshot-export",
        "start-operation",
        "unlock-model"
    ];

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-serve-operation-tests", Guid.NewGuid().ToString("N"));
    private OperationManager? _manager;

    [Fact]
    public async Task New_commands_start_poll_replay_and_cancel_a_running_operation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.CsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok();
        }));
        var dispatcher = CreateDispatcher(_manager);

        var start = Assert.IsType<Result<StartOperationData>>(await dispatcher.DispatchAsync(
            "start-operation",
            Json("""{"kind":"analyze-and-extract","payload":{"filePath":"model.edb"}}"""),
            TestContext.Current.CancellationToken));
        Assert.True(start.Success);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var status = Assert.IsType<Result<OperationStatusData>>(await dispatcher.DispatchAsync(
            "get-operation-status",
            Json($$"""{"operationId":"{{start.Data!.OperationId}}"}"""),
            TestContext.Current.CancellationToken));
        Assert.Equal("Fake.CsiCall", status.Data!.CurrentCsiOperation);

        var events = Assert.IsType<Result<GetOperationEventsData>>(await dispatcher.DispatchAsync(
            "get-operation-events",
            Json($$"""{"operationId":"{{start.Data.OperationId}}","sinceSeq":0}"""),
            TestContext.Current.CancellationToken));
        Assert.True(events.Data!.Events.Count >= 3);
        Assert.True(events.Data.Events.SequenceEqual(events.Data.Events.OrderBy(item => item.Seq)));

        var cancel = Assert.IsType<Result<CancelOperationData>>(await dispatcher.DispatchAsync(
            "cancel-operation",
            Json($$"""{"operationId":"{{start.Data.OperationId}}"}"""),
            TestContext.Current.CancellationToken));
        Assert.Equal(OperationCancellationState.Requested, cancel.Data!.CancellationState);

        release.SetResult();
        await _manager.WaitAsync(start.Data.OperationId, TestContext.Current.CancellationToken);
        Assert.Equal(OperationPhase.Cancelled, _manager.GetStatus(start.Data.OperationId).Data!.Phase);
    }

    [Fact]
    public async Task Get_status_uses_cached_session_state_while_operation_runs()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.CsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok();
        }));
        var session = new FakeSession();
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            new FakeProcesses(new EtabsProcessObservation([Identity(42)], 0)));
        var started = _manager.Start("analyze-and-extract", Json("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var response = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(
            "get-status", null, TestContext.Current.CancellationToken));

        Assert.True(response.Data!.IsRunning);
        Assert.Equal(42, response.Data.Pid);
        Assert.Equal(EtabsInstanceOwnership.Managed, response.Data.Ownership);
        Assert.Equal([42], response.Data.ObservedPids);
        Assert.Equal(0, session.GetOrStartCalls);
        release.SetResult();
        await _manager.WaitAsync(started.Data!.OperationId, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_status_reports_process_ambiguity_without_com(bool unidentified)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.CsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok();
        }));
        var session = new FakeSession();
        var observation = unidentified
            ? new EtabsProcessObservation([Identity(42)], 1)
            : new EtabsProcessObservation([Identity(42), Identity(99)], 0);
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            new FakeProcesses(observation));
        var started = _manager.Start("analyze-and-extract", Json("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            var response = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(
                "get-status", null, TestContext.Current.CancellationToken));

            Assert.True(response.Success);
            Assert.Equal(EtabsInstanceOwnership.Ambiguous, response.Data!.Ownership);
            Assert.Equal(
                unidentified ? [42] : [42, 99],
                response.Data.ObservedPids);
            Assert.Equal(0, session.GetOrStartCalls);
        }
        finally
        {
            release.SetResult();
            await _manager.WaitAsync(
                started.Data!.OperationId,
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Active_status_preserves_cached_failure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.CsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok();
        }));
        var session = new FakeSession();
        var cachedStatus = new CachedSessionStatus();
        cachedStatus.Update(Result.Fail<GetStatusData>("cached status failed"));
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            new FakeProcesses(new EtabsProcessObservation([Identity(42)], 0)),
            cachedStatus: cachedStatus);
        var started = _manager.Start("analyze-and-extract", Json("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            var response = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(
                "get-status", null, TestContext.Current.CancellationToken));

            Assert.False(response.Success);
            Assert.Equal("cached status failed", response.Error);
            Assert.Equal(0, session.GetOrStartCalls);
        }
        finally
        {
            release.SetResult();
            await _manager.WaitAsync(
                started.Data!.OperationId,
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_status_reports_external_process_without_touching_com()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession(isStarted: false, processId: null);
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            new FakeProcesses(new EtabsProcessObservation([Identity(99)], 0)));

        var response = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(
            "get-status", null, TestContext.Current.CancellationToken));

        Assert.True(response.Data!.IsRunning);
        Assert.Equal(EtabsInstanceOwnership.External, response.Data.Ownership);
        Assert.Equal([99], response.Data.ObservedPids);
        Assert.Equal(0, session.GetOrStartCalls);
    }

    [Fact]
    public async Task Legacy_analyze_command_waits_and_returns_the_original_result_shape()
    {
        _manager = CreateManager(new DelegateOperation((payload, _) =>
        {
            var filePath = payload.GetProperty("filePath").GetString()!;
            object result = Result.Ok(new AnalyzeAndExtractData
            {
                FilePath = filePath,
                OutputDir = payload.GetProperty("outputDir").GetString()!
            });
            return Task.FromResult(result);
        }));
        var dispatcher = CreateDispatcher(_manager);

        var response = Assert.IsType<Result<AnalyzeAndExtractData>>(await dispatcher.DispatchAsync(
            "analyze-and-extract",
            Json("""{"filePath":"C:\\model.edb","outputDir":"C:\\results","units":"SI_kN_m_C","tables":{}}"""),
            TestContext.Current.CancellationToken));

        Assert.True(response.Success);
        Assert.Equal(@"C:\model.edb", response.Data!.FilePath);
        Assert.Equal(@"C:\results", response.Data.OutputDir);
    }

    [Fact]
    public async Task Run_analysis_uses_the_shared_serve_session()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var runAnalysis = new FakeRunAnalysisService();
        var dispatcher = CreateDispatcher(_manager, session, runAnalysis: runAnalysis);

        var response = Assert.IsType<Result<RunAnalysisData>>(await dispatcher.DispatchAsync(
            "run-analysis",
            Json("""{"filePath":"C:\\model.edb","cases":["DEAD"],"units":"SI_kN_m_C"}"""),
            TestContext.Current.CancellationToken));

        Assert.True(response.Success);
        Assert.Equal(1, runAnalysis.SharedCalls);
        Assert.Equal(0, runAnalysis.OneShotCalls);
        Assert.Equal(1, session.GetOrStartCalls);
        Assert.Equal(@"C:\model.edb", runAnalysis.FilePath);
        Assert.Equal(["DEAD"], runAnalysis.Cases);
        Assert.Equal("SI_kN_m_C", runAnalysis.Units);
    }

    [Fact]
    public async Task SnapshotExportUsesTheSharedServeSessionAndForwardsTheFlattenedRequest()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var snapshot = new FakeSnapshotExportService();
        var dispatcher = CreateDispatcher(_manager, session, snapshot: snapshot);

        var response = Assert.IsType<Result<SnapshotExportData>>(await dispatcher.DispatchAsync(
            "snapshot-export",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","units":"US_Kip_Ft","e2kFileName":"model.e2k","materialsDirName":"materials","metadataFileName":"model-metadata.json","metricsFileName":"run-metrics.json","extractionProfile":"snapshot","tables":{}}"""),
            TestContext.Current.CancellationToken));

        Assert.True(response.Success);
        // Shared session only: the one-shot overload is what would spawn a second ETABS.
        Assert.Equal(1, snapshot.SharedCalls);
        Assert.Equal(0, snapshot.OneShotCalls);
        Assert.Equal(1, session.GetOrStartCalls);
        Assert.Equal(0, session.GetOrStartOwnedCalls);
        Assert.Equal(@"C:\v1\sample_v2.edb", snapshot.FilePath);
        Assert.Equal(@"C:\v1\snapshot", snapshot.OutputDir);
        Assert.Equal("US_Kip_Ft", snapshot.Request!.Units);
        Assert.Equal("snapshot", snapshot.Request.ExtractionProfile);
        Assert.Equal("model.e2k", snapshot.Request.E2KFileName);
        Assert.Equal("run-metrics.json", snapshot.Request.MetricsFileName);
        Assert.Equal(@"C:\v1\sample_v2.edb", response.Data!.FilePath);
        Assert.Equal(@"C:\v1\snapshot", response.Data.OutputDir);
    }

    [Fact]
    public async Task SnapshotExportPreservesTheCorrelatedFailureShape()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var snapshot = new FakeSnapshotExportService
        {
            Failure = "ETABS_API_CALL_FAILED; operation=cFile.OpenFile; returnCode=23"
        };
        var dispatcher = CreateDispatcher(_manager, session, snapshot: snapshot);

        var response = Assert.IsType<Result<SnapshotExportData>>(await dispatcher.DispatchAsync(
            "snapshot-export",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","tables":{}}"""),
            TestContext.Current.CancellationToken));

        Assert.False(response.Success);
        Assert.Equal("ETABS_API_CALL_FAILED; operation=cFile.OpenFile; returnCode=23", response.Error);
        Assert.Null(response.Data);
        Assert.Equal(1, snapshot.SharedCalls);
        Assert.Equal(0, snapshot.OneShotCalls);
    }

    [Fact]
    public async Task RepeatedSnapshotExportsReuseTheOneSharedSession()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var snapshot = new FakeSnapshotExportService();
        var dispatcher = CreateDispatcher(_manager, session, snapshot: snapshot);
        var payload = Json("""{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","tables":{}}""");

        await dispatcher.DispatchAsync("snapshot-export", payload, TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync("snapshot-export", payload, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.SharedCalls);
        Assert.Equal(0, snapshot.OneShotCalls);
        Assert.Equal(0, session.GetOrStartOwnedCalls);
        // Both dispatches asked the SAME shared session for its app; neither reached
        // for a session of its own.
        Assert.Equal(2, session.GetOrStartCalls);
    }

    [Theory]
    [InlineData("get-model-state", null)]
    [InlineData("list-wall-properties", null)]
    [InlineData("inspect-wall-property", "{\"name\":\"W1500\"}")]
    [InlineData("resolve-area-targets", "{\"sourceProperty\":\"W1500\"}")]
    public async Task InspectionCommandsAreRejectedWhileAnAsyncOperationIsActive(
        string command,
        string? requestJson)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.CsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok();
        }));
        var dispatcher = CreateDispatcher(_manager);
        var started = _manager.Start("analyze-and-extract", Json("{}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var response = Assert.IsType<Result>(await dispatcher.DispatchAsync(
            command,
            requestJson is null ? null : Json(requestJson),
            TestContext.Current.CancellationToken));

        Assert.False(response.Success);
        Assert.Contains("operation is active", response.Error, StringComparison.Ordinal);
        release.SetResult();
        await _manager.WaitAsync(started.Data!.OperationId, TestContext.Current.CancellationToken);
    }

    // ── CLI #22: the two intents must not collapse into one behaviour ───────────
    //
    // The daemon creates its managed ETABS hidden. Whether it ever reaches the screen is
    // decided HERE, by which command was asked for — not by whether an ETABS process
    // happens to exist. These tests fail if a background command starts revealing, if
    // open-model stops revealing, or if the reveal drifts ahead of the model confirmation.

    /// <summary>
    /// Explicit "Open in ETABS": ends visible, and visible only AFTER the open returned
    /// success. The ordering is the whole point — revealing first is precisely the blank
    /// <c>(Untitled)</c> window this issue exists to remove.
    /// </summary>
    [Fact]
    public async Task OpenModelRevealsEtabsOnlyAfterTheRequestedModelIsConfirmedOpen()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var open = new FakeOpenModelService(session.Events);
        var dispatcher = CreateDispatcher(_manager, session, open: open);

        var response = Assert.IsType<Result<OpenModelData>>(await dispatcher.DispatchAsync(
            "open-model",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","saveOnClose":false}"""),
            TestContext.Current.CancellationToken));

        Assert.True(response.Success);
        Assert.Equal(1, session.RevealCalls);
        Assert.Equal(["get-or-start", "open", "reveal"], session.Events);
        Assert.Equal(@"C:\v1\sample_v2.edb", open.FilePath);
    }

    /// <summary>
    /// A failed open leaves nothing on screen. The user asked to see a model, not an empty
    /// application, so an open that could not be confirmed must not put a window up.
    /// </summary>
    [Fact]
    public async Task AFailedOpenNeverRevealsEtabs()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var open = new FakeOpenModelService
        {
            Failure = "ETABS_MODEL_OPEN_NOT_CONFIRMED; operation=cFile.OpenFile"
        };
        var dispatcher = CreateDispatcher(_manager, session, open: open);

        var response = Assert.IsType<Result<OpenModelData>>(await dispatcher.DispatchAsync(
            "open-model",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","saveOnClose":false}"""),
            TestContext.Current.CancellationToken));

        Assert.False(response.Success);
        Assert.Equal("ETABS_MODEL_OPEN_NOT_CONFIRMED; operation=cFile.OpenFile", response.Error);
        Assert.Equal(0, session.RevealCalls);
    }

    /// <summary>
    /// "Open in ETABS" that leaves nothing on screen has not done what was asked, even
    /// though the model is loaded. The response says so rather than reporting success.
    /// </summary>
    [Fact]
    public async Task AnOpenThatCannotBeMadeVisibleIsReportedAsAFailure()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession { RevealSucceeds = false };
        var dispatcher = CreateDispatcher(_manager, session, open: new FakeOpenModelService());

        var response = Assert.IsType<Result<OpenModelData>>(await dispatcher.DispatchAsync(
            "open-model",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","saveOnClose":false}"""),
            TestContext.Current.CancellationToken));

        Assert.False(response.Success);
        Assert.Contains("cOAPI.Unhide", response.Error, StringComparison.Ordinal);
        Assert.Contains(@"C:\v1\sample_v2.edb", response.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The background half. These commands need COM; they do not need a window. If one of
    /// them ever calls the reveal, this fails — which is the "two intents did not collapse"
    /// acceptance criterion, stated as an executable assertion.
    /// </summary>
    [Theory]
    [InlineData(
        "snapshot-export",
        """{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","tables":{}}""")]
    [InlineData(
        "run-analysis",
        """{"filePath":"C:\\v1\\sample_v2.edb","cases":["DEAD"],"units":"SI_kN_m_C"}""")]
    public async Task BackgroundCommandsNeverAskForEtabsToBeShown(string command, string payload)
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            runAnalysis: new FakeRunAnalysisService(),
            snapshot: new FakeSnapshotExportService(),
            open: new FakeOpenModelService());

        await dispatcher.DispatchAsync(command, Json(payload), TestContext.Current.CancellationToken);

        Assert.Equal(1, session.GetOrStartCalls);
        Assert.Equal(0, session.RevealCalls);
        Assert.DoesNotContain("reveal", session.Events);
    }

    private OperationManager CreateManager(IOperationDefinition definition) => new(
        new StaExecutionWorker(),
        new OperationEventJournalFactory(_directory, memoryCapacity: 4),
        new SystemOperationClock(),
        [definition]);

    [Fact]
    public async Task Capabilities_are_the_registered_dispatch_handlers()
    {
        _manager = CreateManager(new DelegateOperation((_, _) =>
            Task.FromResult<object>(Result.Ok())));
        var dispatcher = CreateDispatcher(_manager);

        Assert.Equal(ExpectedCapabilities, dispatcher.Capabilities);

        var unsupported = Assert.IsType<Result>(await dispatcher.DispatchAsync(
            "not-a-command",
            null,
            TestContext.Current.CancellationToken));
        Assert.False(unsupported.Success);
        Assert.Contains("not supported", unsupported.Error, StringComparison.Ordinal);
    }

    private static ServeDispatcher CreateDispatcher(
        IOperationManager operations,
        IEtabsSession? session = null,
        IProcessInspector? processes = null,
        IRunAnalysisService? runAnalysis = null,
        ICachedSessionStatus? cachedStatus = null,
        ISnapshotExportService? snapshot = null,
        IOpenModelService? open = null) => new(
            session ?? null!,
            null!,
            open ?? null!,
            snapshot ?? null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            operations,
            cachedStatus ?? new CachedSessionStatus(),
            processes ?? new FakeProcesses(new EtabsProcessObservation([], 0)),
            runAnalysis ?? null!);

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class DelegateOperation(
        Func<JsonElement, OperationExecutionContext, Task<object>> execute) : IOperationDefinition
    {
        public string Kind => "analyze-and-extract";
        public TimeSpan OperationBudget => TimeSpan.FromMinutes(10);
        public TimeSpan StepBudget => TimeSpan.FromMinutes(5);
        public Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context) =>
            execute(payload, context);
    }

    private static ManagedProcessIdentity Identity(int pid) => new(
        pid,
        new DateTimeOffset(2026, 8, 9, 1, 2, pid % 60, TimeSpan.Zero),
        $@"C:\ETABS-{pid}\ETABS.exe");

    private sealed class FakeSession(bool isStarted = true, int? processId = 42) : IEtabsSession
    {
        public int GetOrStartCalls { get; private set; }
        public int GetOrStartOwnedCalls { get; private set; }
        public int RevealCalls { get; private set; }
        public bool RevealSucceeds { get; init; } = true;

        /// <summary>
        /// Ordered log of what the dispatcher asked this session to do, so a reveal that
        /// happened BEFORE the model was confirmed open is distinguishable from one that
        /// happened after it.
        /// </summary>
        public List<string> Events { get; } = [];

        public bool IsStarted => isStarted;
        public int? ProcessId => processId;
        public ETABSApplication GetOrStart()
        {
            GetOrStartCalls++;
            Events.Add("get-or-start");
            return null!;
        }
        public IManagedEtabsApplication GetOrStartOwned()
        {
            GetOrStartOwnedCalls++;
            return null!;
        }
        public Result RevealForExplicitUserRequest()
        {
            RevealCalls++;
            Events.Add("reveal");
            return RevealSucceeds
                ? Result.Ok()
                : Result.Fail("ETABS_VISIBILITY_NOT_CONFIRMED; operation=cOAPI.Unhide");
        }
        public ManagedEtabsShutdownResult Shutdown() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class FakeOpenModelService : IOpenModelService
    {
        private readonly List<string>? _events;

        public FakeOpenModelService(List<string>? events = null) => _events = events;

        public string? FilePath { get; private set; }
        public bool Save { get; private set; }
        public string? Failure { get; init; }

        public Task<Result<OpenModelData>> OpenModelAsync(
            string filePath,
            bool save,
            bool newInstance) => throw new NotSupportedException();

        public Task<Result<OpenModelData>> OpenModelOnAppAsync(
            ETABSApplication app,
            string filePath,
            bool save)
        {
            FilePath = filePath;
            Save = save;
            _events?.Add("open");
            return Task.FromResult(Failure is null
                ? Result.Ok(new OpenModelData { FilePath = filePath })
                : Result.Fail<OpenModelData>(Failure));
        }
    }

    private sealed class FakeSnapshotExportService : ISnapshotExportService
    {
        public int OneShotCalls { get; private set; }
        public int SharedCalls { get; private set; }
        public string? FilePath { get; private set; }
        public string? OutputDir { get; private set; }
        public SnapshotExportRequest? Request { get; private set; }
        public string? Failure { get; init; }

        public Task<Result<SnapshotExportData>> SnapshotExportAsync(
            string filePath,
            string outputDir,
            SnapshotExportRequest request)
        {
            OneShotCalls++;
            return Task.FromResult(Result.Ok(new SnapshotExportData { FilePath = filePath }));
        }

        public Task<Result<SnapshotExportData>> SnapshotExportOnAppAsync(
            ETABSApplication app,
            string filePath,
            string outputDir,
            SnapshotExportRequest request)
        {
            SharedCalls++;
            FilePath = filePath;
            OutputDir = outputDir;
            Request = request;
            return Task.FromResult(Failure is null
                ? Result.Ok(new SnapshotExportData { FilePath = filePath, OutputDir = outputDir })
                : Result.Fail<SnapshotExportData>(Failure));
        }
    }

    private sealed class FakeProcesses(EtabsProcessObservation observation) : IProcessInspector
    {
        public EtabsProcessObservation ObserveEtabs() => observation;
        public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected) => null;
        public ManagedProcessIdentity? Find(int pid) =>
            observation.Identified.FirstOrDefault(identity => identity.Pid == pid);
        public ExactProcessTerminationResult TerminateExact(
            ManagedProcessIdentity expected,
            TimeSpan timeout) => throw new NotSupportedException();
    }

    private sealed class FakeRunAnalysisService : IRunAnalysisService
    {
        public int OneShotCalls { get; private set; }
        public int SharedCalls { get; private set; }
        public string? FilePath { get; private set; }
        public List<string>? Cases { get; private set; }
        public string? Units { get; private set; }

        public Task<Result<RunAnalysisData>> RunAnalysisAsync(
            string filePath,
            List<string>? cases,
            string? units = null)
        {
            OneShotCalls++;
            return Task.FromResult(Result.Ok(new RunAnalysisData { FilePath = filePath }));
        }

        public Task<Result<RunAnalysisData>> RunAnalysisOnAppAsync(
            ETABSApplication app,
            string filePath,
            List<string>? cases,
            string? units = null)
        {
            SharedCalls++;
            FilePath = filePath;
            Cases = cases;
            Units = units;
            return Task.FromResult(Result.Ok(new RunAnalysisData { FilePath = filePath }));
        }
    }
}
