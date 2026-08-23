using System.Runtime.ExceptionServices;
using System.Text.Json;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve.Operations;

public interface IOperationClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemOperationClock : IOperationClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IOperationDefinition
{
    string Kind { get; }
    TimeSpan OperationBudget { get; }
    TimeSpan StepBudget { get; }
    Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context);
}

public interface IOperationManager : IDisposable
{
    bool HasActiveOperation { get; }
    EtabsOperationContext CaptureEtabsContext(string visibilityStage);
    Result<StartOperationData> Start(string kind, JsonElement payload);
    Result<StartOperationData> Start(
        string kind,
        JsonElement payload,
        EtabsOperationContext context,
        IEtabsSession session);
    Result<OperationStatusData> GetStatus(string operationId);
    Result<GetOperationEventsData> GetEvents(string operationId, long sinceSequence);
    Result<CancelOperationData> Cancel(string operationId);
    Task<object> WaitAsync(string operationId, CancellationToken cancellationToken = default);
    Task<T> ExecuteSynchronousAsync<T>(Func<Task<T>> action);
    Task<object> ExecuteSynchronousEtabsAsync(
        IEtabsSession session,
        EtabsOperationContext context,
        Func<Task<object>> action);
}

public sealed class OperationManager : IOperationManager
{
    private const int CombinedDiagnosticPartLimit = 900;

    private readonly object _gate = new();
    private readonly IStaExecutionWorker _worker;
    private readonly IOperationEventJournalFactory _journals;
    private readonly IOperationClock _clock;
    private readonly IManagedEtabsStartIntentScope _startIntent;
    private readonly Dictionary<string, IOperationDefinition> _definitions;
    private readonly Dictionary<string, OperationState> _operations = new(StringComparer.Ordinal);
    private string? _activeOperationId;

    public OperationManager(
        IStaExecutionWorker worker,
        IOperationEventJournalFactory journals,
        IOperationClock clock,
        IEnumerable<IOperationDefinition> definitions,
        IManagedEtabsStartIntentScope? startIntent = null)
    {
        _worker = worker;
        _journals = journals;
        _clock = clock;
        _startIntent = startIntent ?? new ManagedEtabsStartIntentScope();
        _definitions = definitions.ToDictionary(item => item.Kind, StringComparer.Ordinal);
    }

    public bool HasActiveOperation
    {
        get
        {
            lock (_gate)
            {
                return _activeOperationId is not null;
            }
        }
    }

    public EtabsOperationContext CaptureEtabsContext(string visibilityStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visibilityStage);
        return new EtabsOperationContext(_startIntent.Current, visibilityStage);
    }

    public Result<StartOperationData> Start(string kind, JsonElement payload) =>
        StartCore(kind, payload, CaptureEtabsContext(kind), session: null);

    public Result<StartOperationData> Start(
        string kind,
        JsonElement payload,
        EtabsOperationContext context,
        IEtabsSession session)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        return StartCore(kind, payload, context, session);
    }

    private Result<StartOperationData> StartCore(
        string kind,
        JsonElement payload,
        EtabsOperationContext context,
        IEtabsSession? session)
    {
        if (!_definitions.TryGetValue(kind, out var definition))
        {
            return Result.Fail<StartOperationData>($"Unsupported operation kind: '{kind}'");
        }

        OperationState state;
        lock (_gate)
        {
            if (_activeOperationId is not null)
            {
                return Result.Fail<StartOperationData>(
                    $"Operation already active: '{_activeOperationId}'");
            }

            var operationId = Guid.NewGuid().ToString("N");
            var now = _clock.UtcNow;
            try
            {
                state = new OperationState(
                    operationId,
                    kind,
                    now,
                    definition.OperationBudget,
                    definition.StepBudget,
                    _journals.Create(operationId),
                    context,
                    session);
                _operations.Add(operationId, state);
                _activeOperationId = operationId;
                Append(state, "operation-queued", OperationPhase.Queued, "Operation accepted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _operations.Remove(operationId);
                _activeOperationId = null;
                return Result.Fail<StartOperationData>(
                    $"Could not create durable operation journal: {ex.Message}");
            }
        }

        _ = RunAsync(state, definition, payload.Clone());
        return Result.Ok(new StartOperationData(state.OperationId));
    }

    public Result<OperationStatusData> GetStatus(string operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var state))
            {
                return Result.Fail<OperationStatusData>($"Operation not found: '{operationId}'");
            }

            var now = _clock.UtcNow;
            if (!IsTerminal(state.Phase))
            {
                state.HeartbeatTimestamp = now;
            }
            var operationElapsed = NonNegativeMilliseconds(now - state.StartedAt);
            long? stepElapsed = state.StepStartedAt is null
                ? null
                : NonNegativeMilliseconds(now - state.StepStartedAt.Value);
            var suspectedHang = operationElapsed > state.OperationBudget.TotalMilliseconds
                || stepElapsed > state.StepBudget.TotalMilliseconds;

            return Result.Ok(new OperationStatusData
            {
                OperationId = state.OperationId,
                Kind = state.Kind,
                Phase = state.Phase,
                StepIndex = state.StepIndex,
                StepTotal = state.StepTotal,
                CurrentCsiOperation = state.CurrentCsiOperation,
                OperationElapsedMs = operationElapsed,
                CurrentStepElapsedMs = stepElapsed,
                LastEventSeq = state.Journal.LastSequence,
                CancellationState = state.CancellationState,
                HeartbeatTimestamp = state.HeartbeatTimestamp,
                SuspectedHang = suspectedHang
            });
        }
    }

    public Result<GetOperationEventsData> GetEvents(string operationId, long sinceSequence)
    {
        OperationState state;
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out state!))
            {
                return Result.Fail<GetOperationEventsData>($"Operation not found: '{operationId}'");
            }
        }

        try
        {
            var events = state.Journal.ReadSince(sinceSequence);
            return Result.Ok(new GetOperationEventsData(
                operationId, events, state.Journal.LastSequence));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result.Fail<GetOperationEventsData>("sinceSeq must be zero or greater");
        }
    }

    public Result<CancelOperationData> Cancel(string operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var state))
            {
                return Result.Fail<CancelOperationData>($"Operation not found: '{operationId}'");
            }

            if (!IsTerminal(state.Phase) && state.CancellationState == OperationCancellationState.NotRequested)
            {
                state.CancellationState = OperationCancellationState.Requested;
                state.Phase = OperationPhase.Cancelling;
                state.HeartbeatTimestamp = _clock.UtcNow;
                Append(state, "cancellation-requested", state.Phase, "Cancellation requested");
            }

            return Result.Ok(new CancelOperationData(operationId, state.CancellationState));
        }
    }

    public async Task<object> WaitAsync(string operationId, CancellationToken cancellationToken = default)
    {
        Task<object> completion;
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var state))
            {
                return Result.Fail($"Operation not found: '{operationId}'");
            }
            completion = state.Completion.Task;
        }

        return await completion.WaitAsync(cancellationToken);
    }

    public Task<T> ExecuteSynchronousAsync<T>(Func<Task<T>> action) => _worker.ExecuteAsync(action);

    public Task<object> ExecuteSynchronousEtabsAsync(
        IEtabsSession session,
        EtabsOperationContext context,
        Func<Task<object>> action)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        return _worker.ExecuteAsync(() => ExecuteEtabsCompletionAsync(session, context, action));
    }

    private async Task RunAsync(OperationState state, IOperationDefinition definition, JsonElement payload)
    {
        object result;
        try
        {
            result = await _worker.ExecuteAsync(async () =>
            {
                lock (_gate)
                {
                    ThrowIfCancellationRequested(state);
                    state.Phase = OperationPhase.Running;
                    state.HeartbeatTimestamp = _clock.UtcNow;
                    Append(state, "operation-started", state.Phase, "Operation started");
                }

                var context = new OperationExecutionContext(
                    state.EtabsContext,
                    (index, total, csiOperation, action) => RunStepAsync(
                        state, index, total, csiOperation, action));

                return state.Session is null
                    ? await definition.ExecuteAsync(payload, context)
                    : await ExecuteEtabsCompletionAsync(
                        state.Session,
                        state.EtabsContext,
                        () => definition.ExecuteAsync(payload, context));
            });

            lock (_gate)
            {
                state.Phase = result is Result { Success: false }
                    ? OperationPhase.Failed
                    : OperationPhase.Succeeded;
                state.CurrentCsiOperation = null;
                state.StepStartedAt = null;
                state.HeartbeatTimestamp = _clock.UtcNow;
                Append(
                    state,
                    state.Phase == OperationPhase.Succeeded ? "operation-succeeded" : "operation-failed",
                    state.Phase,
                    state.Phase == OperationPhase.Succeeded ? "Operation completed" : "Operation returned failure",
                    result);
            }
        }
        catch (OperationCanceledException)
        {
            result = Result.Fail("Operation cancelled between CSI calls");
            lock (_gate)
            {
                state.Phase = OperationPhase.Cancelled;
                state.CancellationState = OperationCancellationState.Honored;
                state.CurrentCsiOperation = null;
                state.StepStartedAt = null;
                state.HeartbeatTimestamp = _clock.UtcNow;
                Append(state, "operation-cancelled", state.Phase, "Cancellation honored between CSI calls");
            }
        }
        catch (Exception ex)
        {
            result = Result.Fail($"Operation failed: {ex.Message}");
            lock (_gate)
            {
                state.Phase = OperationPhase.Failed;
                state.CurrentCsiOperation = null;
                state.StepStartedAt = null;
                state.HeartbeatTimestamp = _clock.UtcNow;
                Append(state, "operation-failed", state.Phase, ex.Message);
            }
        }

        lock (_gate)
        {
            if (_activeOperationId == state.OperationId)
            {
                _activeOperationId = null;
            }
            state.Completion.TrySetResult(result);
        }
    }

    /// <summary>
    /// The single ETABS operation boundary for synchronous commands and queued operations.
    /// It republishes the immutable request intent on the STA, labels visibility evidence,
    /// and certifies terminal visibility state even when the ETABS-backed action throws.
    /// </summary>
    private async Task<object> ExecuteEtabsCompletionAsync(
        IEtabsSession session,
        EtabsOperationContext context,
        Func<Task<object>> action)
    {
        using var intent = _startIntent.Publish(context.StartIntent);
        session.MarkVisibilityStage(context.VisibilityStage);

        object? result = null;
        Exception? actionFailure = null;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            actionFailure = ex;
        }

        // A cold session temporarily labels InitializeNewModel while readiness is being
        // established. EtabsSession restores the pending command stage before handing the
        // application back; reassert it here as well so the final exact-owned census is
        // unquestionably attributed to the command/operation being certified.
        session.MarkVisibilityStage(context.VisibilityStage);
        var certification = CertifyOrTerminate(session);
        if (!certification.Success)
        {
            return actionFailure is null
                ? certification
                : Result.Fail(CombineFailures(context, actionFailure, certification));
        }

        if (actionFailure is not null)
        {
            ExceptionDispatchInfo.Capture(actionFailure).Throw();
        }

        return result!;
    }

    private static Result CertifyOrTerminate(IEtabsSession session)
    {
        try
        {
            return session.CertifyNoUnconsentedExposure();
        }
        catch (Exception certificationFailure)
        {
            var diagnostic = EtabsApiDiagnosticFormatter.InfrastructureException(
                "IEtabsSession.CertifyNoUnconsentedExposure",
                certificationFailure);
            try
            {
                var cleanup = session.Shutdown();
                diagnostic = EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    diagnostic,
                    $"certificationCleanupState={cleanup.Data.State}; " +
                    $"processExitConfirmed={cleanup.Data.ProcessExitConfirmed}; " +
                    $"recordRetained={cleanup.Data.RecordRetained}");
            }
            catch (Exception cleanupFailure)
            {
                diagnostic = EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    diagnostic,
                    "certificationCleanupException=" +
                    EtabsApiDiagnosticFormatter.Bounded(cleanupFailure.Message));
            }

            return Result.Fail(diagnostic);
        }
    }

    private static string CombineFailures(
        EtabsOperationContext context,
        Exception actionFailure,
        Result certification)
    {
        var actionDiagnostic = EtabsApiDiagnosticFormatter.InfrastructureException(
            $"ETABS-backed action ({context.VisibilityStage})",
            actionFailure);
        var certificationDiagnostic = EtabsApiDiagnosticFormatter.Bounded(
            certification.Error ?? "visibility certification failed without a diagnostic");

        return EtabsApiDiagnosticFormatter.Bounded(
            "actionFailure=" + BoundCombinedPart(actionDiagnostic) +
            "; visibilityCertification=" + BoundCombinedPart(certificationDiagnostic));
    }

    private static string BoundCombinedPart(string value) =>
        value.Length <= CombinedDiagnosticPartLimit
            ? value
            : string.Concat(value.AsSpan(0, CombinedDiagnosticPartLimit - 1), "…");

    private async Task<T> RunStepAsync<T>(
        OperationState state,
        int index,
        int total,
        string csiOperation,
        Func<Task<T>> action)
    {
        lock (_gate)
        {
            ThrowIfCancellationRequested(state);
            state.StepIndex = index;
            state.StepTotal = total;
            state.CurrentCsiOperation = csiOperation;
            state.StepStartedAt = _clock.UtcNow;
            state.HeartbeatTimestamp = state.StepStartedAt.Value;
            Append(state, "step-started", state.Phase, csiOperation);
        }

        var result = await action();

        lock (_gate)
        {
            state.HeartbeatTimestamp = _clock.UtcNow;
            Append(state, "step-completed", state.Phase, csiOperation);
            state.CurrentCsiOperation = null;
            state.StepStartedAt = null;
            ThrowIfCancellationRequested(state);
        }
        return result;
    }

    private static void ThrowIfCancellationRequested(OperationState state)
    {
        if (state.CancellationState == OperationCancellationState.Requested)
        {
            state.CancellationState = OperationCancellationState.Honored;
            throw new OperationCanceledException();
        }
    }

    private void Append(
        OperationState state,
        string type,
        OperationPhase phase,
        string? message,
        object? data = null)
    {
        state.Journal.Append(new OperationEvent
        {
            Timestamp = _clock.UtcNow,
            Type = type,
            Phase = phase,
            StepIndex = state.StepIndex,
            StepTotal = state.StepTotal,
            CsiOperation = state.CurrentCsiOperation,
            Message = message,
            Data = data is null
                ? null
                : JsonSerializer.SerializeToElement(data, data.GetType(), ServeJson.Options)
        });
    }

    private static bool IsTerminal(OperationPhase phase) =>
        phase is OperationPhase.Succeeded or OperationPhase.Failed or OperationPhase.Cancelled;

    private static long NonNegativeMilliseconds(TimeSpan elapsed) =>
        Math.Max(0, (long)elapsed.TotalMilliseconds);

    public void Dispose() => _worker.Dispose();

    private sealed class OperationState(
        string operationId,
        string kind,
        DateTimeOffset startedAt,
        TimeSpan operationBudget,
        TimeSpan stepBudget,
        IOperationEventJournal journal,
        EtabsOperationContext etabsContext,
        IEtabsSession? session)
    {
        public string OperationId { get; } = operationId;
        public string Kind { get; } = kind;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public TimeSpan OperationBudget { get; } = operationBudget;
        public TimeSpan StepBudget { get; } = stepBudget;
        public IOperationEventJournal Journal { get; } = journal;
        public EtabsOperationContext EtabsContext { get; } = etabsContext;
        public IEtabsSession? Session { get; } = session;
        public TaskCompletionSource<object> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OperationPhase Phase { get; set; } = OperationPhase.Queued;
        public int? StepIndex { get; set; }
        public int? StepTotal { get; set; }
        public string? CurrentCsiOperation { get; set; }
        public DateTimeOffset? StepStartedAt { get; set; }
        public DateTimeOffset HeartbeatTimestamp { get; set; } = startedAt;
        public OperationCancellationState CancellationState { get; set; }
    }
}

public sealed class OperationExecutionContext : IEtabsOperationProgress
{
    private readonly Func<int, int, string, Func<Task<object?>>, Task<object?>> _runStep;

    internal OperationExecutionContext(
        EtabsOperationContext etabs,
        Func<int, int, string, Func<Task<object?>>, Task<object?>> runStep)
    {
        Etabs = etabs;
        _runStep = runStep;
    }

    public EtabsOperationContext Etabs { get; }

    public async Task<T> RunStepAsync<T>(
        int index,
        int total,
        string csiOperation,
        Func<Task<T>> action)
    {
        var result = await _runStep(index, total, csiOperation, async () => await action());
        return (T)result!;
    }
}
