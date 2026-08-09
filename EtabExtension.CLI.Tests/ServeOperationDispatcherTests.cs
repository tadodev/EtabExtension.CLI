using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeOperationDispatcherTests : IDisposable
{
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
        var dispatcher = CreateDispatcher(_manager, session);
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

    private OperationManager CreateManager(IOperationDefinition definition) => new(
        new StaExecutionWorker(),
        new OperationEventJournalFactory(_directory, memoryCapacity: 4),
        new SystemOperationClock(),
        [definition]);

    private static ServeDispatcher CreateDispatcher(
        IOperationManager operations,
        IEtabsSession? session = null,
        IProcessInspector? processes = null) => new(
            session ?? null!,
            null!,
            null!,
            null!,
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
            new CachedSessionStatus(),
            processes ?? new FakeProcesses(new EtabsProcessObservation([], 0)));

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
        public bool IsStarted => isStarted;
        public int? ProcessId => processId;
        public ETABSApplication GetOrStart()
        {
            GetOrStartCalls++;
            throw new InvalidOperationException("COM must not be touched for cached status");
        }
        public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();
        public void Shutdown() { }
        public void Dispose() { }
    }

    private sealed class FakeProcesses(EtabsProcessObservation observation) : IProcessInspector
    {
        public EtabsProcessObservation ObserveEtabs() => observation;
        public ManagedProcessIdentity? Find(int pid) =>
            observation.Identified.FirstOrDefault(identity => identity.Pid == pid);
        public void Terminate(int pid) => throw new NotSupportedException();
        public bool WaitForExit(int pid, TimeSpan timeout) => throw new NotSupportedException();
    }
}
