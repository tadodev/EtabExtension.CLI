using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
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

    /// <summary>
    /// The legacy command ACCEPTS on the protocol thread and answers later — but what it
    /// answers with is unchanged: the original <c>Result&lt;AnalyzeAndExtractData&gt;</c>.
    /// The dispatch itself must complete immediately, because the serve loop awaits it
    /// before reading the next line.
    /// </summary>
    [Fact]
    public async Task Legacy_analyze_command_defers_and_answers_with_the_original_result_shape()
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

        var dispatch = dispatcher.DispatchAsync(
            "analyze-and-extract",
            Json("""{"filePath":"C:\\model.edb","outputDir":"C:\\results","units":"SI_kN_m_C","tables":{}}"""),
            TestContext.Current.CancellationToken);

        Assert.True(dispatch.IsCompleted, "the dispatch must not block the protocol thread");
        var deferred = Assert.IsType<DeferredServeResponse>(await dispatch);

        var response = Assert.IsType<Result<AnalyzeAndExtractData>>(await ResolveAsync(deferred));
        Assert.True(response.Success);
        Assert.Equal(@"C:\model.edb", response.Data!.FilePath);
        Assert.Equal(@"C:\results", response.Data.OutputDir);
    }

    /// <summary>
    /// Resolves whatever the dispatcher returned. <c>analyze-and-extract</c> accepts on the
    /// protocol thread and answers later, so a dispatcher-level test has to do what
    /// <see cref="ServeLoop"/> does and take the deferred completion.
    /// </summary>
    private static async Task<object> ResolveAsync(object response) =>
        response is DeferredServeResponse deferred
            ? await deferred.Completion.WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken)
            : response;

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

    /// <summary>
    /// CLI #24, at the seam where it actually has to bite: the RESPONSE.
    ///
    /// <para>A background command can succeed at its own job and still have put ETABS in
    /// front of the engineer while doing it. The readiness gate cannot catch that - it runs
    /// once, at session creation - so without a per-request certification the daemon would
    /// observe the exposure, record it faithfully, and still answer success to the very
    /// request that caused it.</para>
    ///
    /// <para>The successful result is REPLACED, not annotated: a partially-successful
    /// export whose session breached the visibility contract is not a success the desktop
    /// should act on.</para>
    /// </summary>
    [Theory]
    [InlineData(
        "snapshot-export",
        """{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","tables":{}}""")]
    [InlineData(
        "run-analysis",
        """{"filePath":"C:\\v1\\sample_v2.edb","cases":["DEAD"],"units":"SI_kN_m_C"}""")]
    public async Task ABackgroundCommandThatExposedEtabsCannotReturnSuccess(
        string command,
        string payload)
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession
        {
            ExposureCertification = Result.Fail(
                "ETABS_WINDOW_UNCONSENTED_EXPOSURE; observations=3; firstHandle=0x2A4")
        };
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            runAnalysis: new FakeRunAnalysisService(),
            snapshot: new FakeSnapshotExportService(),
            open: new FakeOpenModelService());

        var response = await dispatcher.DispatchAsync(
            command,
            Json(payload),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<Result>(response, exactMatch: false);
        Assert.False(result.Success);
        Assert.Contains(
            "ETABS_WINDOW_UNCONSENTED_EXPOSURE",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>And a clean session is not interfered with.</summary>
    [Fact]
    public async Task ACleanBackgroundCommandKeepsItsOwnSuccessfulResult()
    {
        _manager = CreateManager(new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())));
        var session = new FakeSession();
        var dispatcher = CreateDispatcher(
            _manager,
            session,
            runAnalysis: new FakeRunAnalysisService(),
            snapshot: new FakeSnapshotExportService(),
            open: new FakeOpenModelService());

        var response = await dispatcher.DispatchAsync(
            "snapshot-export",
            Json("""{"filePath":"C:\\v1\\sample_v2.edb","outputDir":"C:\\v1\\snapshot","tables":{}}"""),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<Result>(response, exactMatch: false);
        Assert.True(result.Success);

        // And the command was labelled for CLI #24 evidence.
        Assert.Contains("snapshot-export", session.Stages);
    }

    /// <summary>
    /// The ASYNC lane, which R5 did not reach. <c>analyze-and-extract</c> and
    /// <c>start-operation</c> do not run through the synchronous COM wrapper at all - they
    /// queue work on the STA worker and answer later - so a certification bolted onto the
    /// synchronous wrapper left the daemon's longest-running command as the one path that
    /// could expose ETABS and still finish successfully.
    /// </summary>
    [Fact]
    public async Task AQueuedOperationThatExposedEtabsCannotFinishSuccessfully()
    {
        var session = new FakeSession
        {
            ExposureCertification = Result.Fail(
                "ETABS_WINDOW_UNCONSENTED_EXPOSURE; observations=5; totalVisibleMs=4210")
        };
        _manager = CreateManager(
            new DelegateOperation((_, _) => Task.FromResult<object>(
                Result.Ok(new AnalyzeAndExtractData
                {
                    FilePath = @"C:\model.edb",
                    OutputDir = @"C:
esults"
                }))),
            session);
        var dispatcher = CreateDispatcher(_manager, session);

        var response = await ResolveAsync(await dispatcher.DispatchAsync(
            "analyze-and-extract",
            Json("""{"filePath":"C:\\model.edb","outputDir":"C:\\results","units":"SI_kN_m_C","tables":{}}"""),
            TestContext.Current.CancellationToken));

        var result = Assert.IsType<Result>(response, exactMatch: false);
        Assert.False(result.Success);
        Assert.Contains(
            "ETABS_WINDOW_UNCONSENTED_EXPOSURE",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the OPERATION itself is terminal-failed, not merely the response. A desktop that
    /// polls status rather than waiting on the reply must reach the same conclusion.
    /// </summary>
    [Fact]
    public async Task AQueuedOperationThatExposedEtabsEndsInTheFailedPhase()
    {
        var session = new FakeSession
        {
            ExposureCertification = Result.Fail(
                "ETABS_WINDOW_UNCONSENTED_EXPOSURE; observations=2")
        };
        _manager = CreateManager(
            new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())),
            session);
        var dispatcher = CreateDispatcher(_manager, session);

        var start = Assert.IsType<Result<StartOperationData>>(await dispatcher.DispatchAsync(
            "start-operation",
            Json("""{"kind":"analyze-and-extract","payload":{"filePath":"model.edb"}}"""),
            TestContext.Current.CancellationToken));
        _ = await _manager.WaitAsync(
            start.Data!.OperationId,
            TestContext.Current.CancellationToken);

        var status = Assert.IsType<Result<OperationStatusData>>(await dispatcher.DispatchAsync(
            "get-operation-status",
            Json($$"""{"operationId":"{{start.Data.OperationId}}"}"""),
            TestContext.Current.CancellationToken));

        Assert.Equal(OperationPhase.Failed, status.Data!.Phase);
    }

    /// <summary>A queued operation is labelled with its kind, so #24 evidence attributes it.</summary>
    [Fact]
    public async Task AQueuedOperationLabelsTheVisibilityStageWithItsKind()
    {
        var session = new FakeSession();
        _manager = CreateManager(
            new DelegateOperation((_, _) => Task.FromResult<object>(Result.Ok())),
            session);

        var started = _manager.Start("analyze-and-extract", Json("{}"));
        _ = await _manager.WaitAsync(
            started.Data!.OperationId,
            TestContext.Current.CancellationToken);

        Assert.Contains("analyze-and-extract", session.Stages);
    }

    /// <summary>
    /// The lease-poisoning round trip, driven through the REAL line protocol rather than
    /// through the manager alone — because the claim being made is about what a client
    /// sees on the wire.
    ///
    /// <para>A <c>start-operation</c> that omits <c>"payload"</c> comes back as ONE bounded
    /// failure carried on ITS OWN request id, and the very next valid start is accepted.
    /// Before the fix the malformed request took the operation lease and never gave it
    /// back: request 8 answered "Operation already active", and so did every request after
    /// it until the daemon was restarted.</para>
    /// </summary>
    [Fact]
    public async Task AStartOperationWithoutAPayloadFailsOnItsOwnIdAndLeavesTheDaemonServing()
    {
        var runs = 0;
        _manager = CreateManager(new DelegateOperation((_, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<object>(Result.Ok());
        }));

        var responses = await RunLoopAsync(
            _manager,
            """{"id":7,"command":"start-operation","request":{"kind":"analyze-and-extract"}}""",
            """{"id":8,"command":"start-operation","request":{"kind":"analyze-and-extract","payload":{"filePath":"model.edb"}}}""");

        Assert.Equal(2, responses.Count);
        Assert.Equal(7, responses[0].GetProperty("id").GetInt64());
        Assert.False(responses[0].GetProperty("success").GetBoolean());
        Assert.Contains(
            "payload",
            responses[0].GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(8, responses[1].GetProperty("id").GetInt64());
        Assert.True(responses[1].GetProperty("success").GetBoolean());
        var operationId = responses[1].GetProperty("data").GetProperty("operationId").GetString()!;

        _ = await _manager
            .WaitAsync(operationId, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.False(_manager.HasActiveOperation);
    }

    /// <summary>
    /// And the SYNCHRONOUS lane is not collateral damage. The poisoned lease made every
    /// ETABS-backed command answer "a daemon operation is active" — a daemon that looked
    /// permanently busy while doing nothing at all.
    /// </summary>
    [Fact]
    public async Task AStartOperationWithoutAPayloadDoesNotBlockSynchronousCommands()
    {
        _manager = CreateManager(new DelegateOperation((_, _) =>
            Task.FromResult<object>(Result.Ok())));
        var dispatcher = CreateDispatcher(
            _manager,
            new FakeSession(isStarted: false),
            new FakeProcesses(new EtabsProcessObservation([Identity(42)], 0)));

        var refused = Assert.IsType<Result<StartOperationData>>(await dispatcher.DispatchAsync(
            "start-operation",
            Json("""{"kind":"analyze-and-extract"}"""),
            TestContext.Current.CancellationToken));
        Assert.False(refused.Success);

        var status = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(
            "get-status", null, TestContext.Current.CancellationToken));

        Assert.True(status.Success);
        Assert.Equal([42], status.Data!.ObservedPids);
    }

    /// <summary>
    /// Runs real requests through the real <see cref="ServeLoop"/> over the real dispatcher
    /// and operation manager, and returns the correlated response lines.
    /// </summary>
    private static async Task<List<JsonElement>> RunLoopAsync(
        IOperationManager operations,
        params string[] requests)
    {
        using var reader = new StringReader(string.Join('\n', requests) + "\n");
        await using var writer = new StringWriter();

        await CreateLoop(CreateDispatcher(operations))
            .RunAsync(reader, writer, TestContext.Current.CancellationToken);

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(element => element.TryGetProperty("id", out _))
            .ToList();
    }

    private static ServeLoop CreateLoop(IServeDispatcher dispatcher) => new(
        dispatcher,
        new NoopShutdownCoordinator(),
        new ManagedEtabsStartIntentScope(),
        capabilities => new ServeHandshake(
            "etab-cli-serve",
            1,
            "0.1.0",
            "0.1.0+gtest",
            Environment.ProcessId,
            Path.GetFullPath(Environment.ProcessPath!),
            capabilities),
        TextWriter.Null);

    /// <summary>
    /// THE responsiveness property. <c>ServeLoop</c> awaits each dispatch before reading
    /// the next line, and the legacy <c>analyze-and-extract</c> used to await the whole
    /// operation inside its dispatch — so for up to the 60-minute operation budget the
    /// daemon read NOTHING. <c>get-operation-status</c>, <c>get-operation-events</c> and
    /// <c>cancel-operation</c> were unreachable exactly while they mattered.
    ///
    /// <para>Driven through a stdin the test feeds one line at a time on purpose: a
    /// <c>StringReader</c> hands the loop every line up front and would prove nothing about
    /// WHEN they were read.</para>
    /// </summary>
    [Fact]
    public async Task ControlCommandsAreAnsweredWhileTheLegacyAnalyzeIsStillRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.LongCsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok(new AnalyzeAndExtractData());
        }));

        using var reader = new ScriptedReader();
        var writer = new ResponseCollector();
        var run = CreateLoop(CreateDispatcher(_manager)).RunAsync(
            reader, writer, TestContext.Current.CancellationToken);

        try
        {
            reader.Send(AnalyzeRequest(1));
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var operationId = RunningOperationId();
            reader.Send(Request(2, "get-operation-status", new { operationId }));
            reader.Send(Request(3, "get-operation-events", new { operationId, sinceSeq = 0 }));
            reader.Send(Request(4, "cancel-operation", new { operationId }));

            await WaitUntilAsync(
                () => writer.Responses().Count >= 3,
                "the daemon must answer control commands while the analysis is still running");

            // Answered WHILE the analysis is still inside its CSI call: the analyze request
            // is accepted and unanswered, and these three came back ahead of it.
            var duringTheRun = writer.Responses();
            Assert.Equal([2L, 3L, 4L], duringTheRun.Select(Id).ToArray());
            Assert.All(
                duringTheRun,
                response => Assert.True(response.GetProperty("success").GetBoolean()));
            Assert.Equal(
                "Fake.LongCsiCall",
                duringTheRun[0].GetProperty("data").GetProperty("currentCsiOperation").GetString());
            Assert.True(_manager.HasActiveOperation);

            release.SetResult();
            reader.Close();
            await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // And the accepted request is still settled, on its own id, last.
            var all = writer.Responses();
            Assert.Equal([2L, 3L, 4L, 1L], all.Select(Id).ToArray());
            Assert.Equal(OperationPhase.Cancelled, _manager.GetStatus(operationId).Data!.Phase);
        }
        finally
        {
            await UnblockAsync(release, reader, run);
        }
    }

    /// <summary>
    /// <c>shutdown</c> has to be reachable too — a daemon that cannot be told to stop for
    /// an hour is a daemon the desktop can only kill.
    ///
    /// <para>The discriminating fact is that the shutdown LINE IS READ while the analysis
    /// is still blocked. Asserting on response order alone would pass against the old code,
    /// which would simply read it an hour later and answer in the same order.</para>
    /// </summary>
    [Fact]
    public async Task ShutdownIsReadWhileTheAnalysisIsStillRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.LongCsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok(new AnalyzeAndExtractData());
        }));

        using var reader = new ScriptedReader();
        var writer = new ResponseCollector();
        var run = CreateLoop(CreateDispatcher(_manager)).RunAsync(
            reader, writer, TestContext.Current.CancellationToken);

        try
        {
            reader.Send(AnalyzeRequest(1));
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            reader.Send(Request(9, "shutdown"));
            await WaitUntilAsync(
                () => reader.LinesRead == 2,
                "the daemon must read the shutdown request while the analysis is still running");

            release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            // The accepted analysis is answered before the daemon announces it is gone.
            Assert.Equal([1L, 9L], writer.Responses().Select(Id).ToArray());
        }
        finally
        {
            await UnblockAsync(release, reader, run);
        }
    }

    /// <summary>
    /// Responsiveness must come from not blocking the READER, never from running two things
    /// against ETABS at once. A synchronous COM command arriving mid-analysis is still
    /// refused, and the feature service behind it is never called.
    /// </summary>
    [Fact]
    public async Task ASynchronousComCommandIsStillRefusedWhileTheAnalysisHoldsTheWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.LongCsiCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return Result.Ok(new AnalyzeAndExtractData());
        }));
        var session = new FakeSession();
        var snapshot = new FakeSnapshotExportService();

        using var reader = new ScriptedReader();
        var writer = new ResponseCollector();
        var run = CreateLoop(CreateDispatcher(_manager, session, snapshot: snapshot)).RunAsync(
            reader, writer, TestContext.Current.CancellationToken);

        try
        {
            reader.Send(AnalyzeRequest(1));
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            reader.Send(Request(
                5,
                "snapshot-export",
                new { filePath = @"C:\m.edb", outputDir = @"C:\out", tables = new { } }));
            await WaitUntilAsync(
                () => writer.Responses().Count >= 1,
                "the daemon must answer the synchronous command rather than queue it silently");

            var refused = writer.Responses()[0];
            Assert.Equal(5L, Id(refused));
            Assert.False(refused.GetProperty("success").GetBoolean());
            Assert.Contains(
                "daemon operation is active",
                refused.GetProperty("error").GetString()!,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, snapshot.SharedCalls);
            Assert.Equal(0, session.GetOrStartCalls);

            release.SetResult();
            reader.Close();
            await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        finally
        {
            await UnblockAsync(release, reader, run);
        }
    }

    /// <summary>
    /// The frozen wire contract, asserted on the BYTES rather than on a typed object: one
    /// request in, exactly one response out, on the same id, flat
    /// <c>{id, success, data}</c> with the original <c>AnalyzeAndExtractData</c> fields.
    /// The Rust client reads the next id-bearing line and requires it to be its own answer,
    /// so an extra line here — or a different shape — would break it.
    /// </summary>
    [Fact]
    public async Task TheLegacyAnalyzeAnswersExactlyOnceOnItsOwnIdWithTheOriginalShape()
    {
        _manager = CreateManager(new DelegateOperation((payload, _) => Task.FromResult<object>(
            Result.Ok(new AnalyzeAndExtractData
            {
                FilePath = payload.GetProperty("filePath").GetString()!,
                OutputDir = payload.GetProperty("outputDir").GetString()!
            }))));

        var responses = await RunLoopAsync(_manager, AnalyzeRequest(11));

        var only = Assert.Single(responses);
        Assert.Equal(11L, Id(only));
        Assert.True(only.GetProperty("success").GetBoolean());
        Assert.Equal(@"C:\model.edb", only.GetProperty("data").GetProperty("filePath").GetString());
        Assert.Equal(@"C:\results", only.GetProperty("data").GetProperty("outputDir").GetString());
    }

    /// <summary>
    /// The flattened payload <c>request_from_args</c> produces on the Rust side for
    /// <c>analyze-and-extract</c>.
    /// </summary>
    private static string AnalyzeRequest(long id) => Request(
        id,
        "analyze-and-extract",
        new
        {
            filePath = @"C:\model.edb",
            outputDir = @"C:\results",
            units = "SI_kN_m_C",
            tables = new { }
        });

    /// <summary>One request line, in the envelope the daemon's stdin expects.</summary>
    private static string Request(long id, string command, object? request = null) =>
        JsonSerializer.Serialize(new { id, command, request }, RequestJson);

    private static readonly JsonSerializerOptions RequestJson =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static long Id(JsonElement response) => response.GetProperty("id").GetInt64();

    /// <summary>
    /// The id of the operation the daemon is running, read from its own durable journal —
    /// the legacy command never puts an operationId on the wire, so this is the only honest
    /// way for a client-side test to name it.
    /// </summary>
    private string RunningOperationId() =>
        Path.GetFileName(Directory.EnumerateDirectories(_directory).Single());

    /// <summary>
    /// Always lets the fake CSI call finish, however the test ended. Disposing the manager
    /// joins the STA thread, so an assertion that failed while the operation was still
    /// blocked would HANG the whole run instead of failing one test - which is exactly what
    /// happened the first time the defect was reintroduced to check these tests catch it.
    /// </summary>
    private static async Task UnblockAsync(
        TaskCompletionSource release,
        ScriptedReader reader,
        Task run)
    {
        release.TrySetResult();
        reader.Close();
        _ = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail(because);
    }

    /// <summary>
    /// A stdin the test feeds one line at a time, and which counts what the daemon actually
    /// consumed. "The loop read the next request while the previous one was still running"
    /// is the property under test, and only the reader can witness it.
    /// </summary>
    private sealed class ScriptedReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private int _read;

        public int LinesRead => Volatile.Read(ref _read);

        public void Send(string line) => _lines.Writer.TryWrite(line);

        public void Close() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                var line = await _lines.Reader.ReadAsync(cancellationToken);
                Interlocked.Increment(ref _read);
                return line;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override string? ReadLine() => throw new NotSupportedException();
    }

    /// <summary>
    /// Collects the daemon's response lines so the test can read them WHILE the loop is
    /// still writing — which a <see cref="StringWriter"/> cannot safely offer.
    /// </summary>
    private sealed class ResponseCollector : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            lock (_gate)
            {
                if (value is not null)
                {
                    _lines.Add(value);
                }
            }
            return Task.CompletedTask;
        }

        public override void Write(char value) => throw new NotSupportedException();

        public override Task FlushAsync() => Task.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Correlated responses only — the handshake banner carries no id.</summary>
        public IReadOnlyList<JsonElement> Responses()
        {
            lock (_gate)
            {
                return _lines
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(element => element.TryGetProperty("id", out _))
                    .ToArray();
            }
        }
    }

    /// <summary>Shutdown is not what these tests are about; the session here is a fake.</summary>
    private sealed class NoopShutdownCoordinator : IServeShutdownCoordinator
    {
        public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync() =>
            Task.FromResult(Result.Ok(new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.Succeeded,
                ProcessExitConfirmed: true,
                Forced: false,
                RecordRetained: false,
                ApplicationExitReturnCode: 0,
                OwnedPid: null)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private OperationManager CreateManager(
        IOperationDefinition definition,
        IEtabsSession? session = null) => new(
        new StaExecutionWorker(),
        new OperationEventJournalFactory(_directory, memoryCapacity: 4),
        new SystemOperationClock(),
        WorkEnvelopeFixtures.Consented(session ?? new FakeSession()),
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
            runAnalysis ?? null!,
            WorkEnvelopeFixtures.Consented(session ?? new FakeSession()));

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
        /// <summary>CLI #24 certification the test can force to fail.</summary>
        public Result ExposureCertification { get; set; } = Result.Ok();

        public List<string> Stages { get; } = [];

        public Result CertifyNoUnconsentedExposure() => ExposureCertification;

        public void MarkVisibilityStage(string stage) => Stages.Add(stage);

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
