using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.ExtractMaterials;
using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.GenerateE2K;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.Serve.Inspection;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. The handler registry is also the authoritative source
/// for the capabilities advertised by the persistent protocol.
/// </summary>
public sealed class ServeDispatcher : IServeDispatcher
{
    private readonly IEtabsSession _session;
    private readonly IGetStatusService _status;
    private readonly IOpenModelService _open;
    private readonly ISnapshotExportService _snapshot;
    private readonly ICloseModelService _close;
    private readonly IUnlockModelService _unlock;
    private readonly IExtractResultsService _extractResults;
    private readonly IExtractMaterialsService _extractMaterials;
    private readonly IGenerateE2KService _generateE2K;
    private readonly IReadModelMetadataService _metadata;
    private readonly IServeInspectionService _inspection;
    private readonly IEtabsInspectionApiFactory _inspectionApiFactory;
    private readonly ISessionRecordStore _sessionRecords;
    private readonly IOperationManager _operations;
    private readonly ICachedSessionStatus _cachedStatus;
    private readonly IProcessInspector _processes;
    private readonly IRunAnalysisService _runAnalysis;
    private readonly IEtabsWorkEnvelope _envelope;
    private readonly Dictionary<
        string,
        Func<JsonElement?, CancellationToken, Task<object>>> _handlers;

    public IReadOnlyCollection<string> Capabilities { get; }

    public ServeDispatcher(
        IEtabsSession session,
        IGetStatusService status,
        IOpenModelService open,
        ISnapshotExportService snapshot,
        ICloseModelService close,
        IUnlockModelService unlock,
        IExtractResultsService extractResults,
        IExtractMaterialsService extractMaterials,
        IGenerateE2KService generateE2K,
        IReadModelMetadataService metadata,
        IServeInspectionService inspection,
        IEtabsInspectionApiFactory inspectionApiFactory,
        ISessionRecordStore sessionRecords,
        IOperationManager operations,
        ICachedSessionStatus cachedStatus,
        IProcessInspector processes,
        IRunAnalysisService runAnalysis,
        IEtabsWorkEnvelope envelope)
    {
        _session = session;
        _status = status;
        _open = open;
        _snapshot = snapshot;
        _close = close;
        _unlock = unlock;
        _extractResults = extractResults;
        _extractMaterials = extractMaterials;
        _generateE2K = generateE2K;
        _metadata = metadata;
        _inspection = inspection;
        _inspectionApiFactory = inspectionApiFactory;
        _sessionRecords = sessionRecords;
        _operations = operations;
        _cachedStatus = cachedStatus;
        _processes = processes;
        _runAnalysis = runAnalysis;
        _envelope = envelope;

        var handlers = new Dictionary<
            string,
            Func<JsonElement?, CancellationToken, Task<object>>>(StringComparer.Ordinal)
        {
            ["analyze-and-extract"] = DispatchAnalyzeAndExtractAsync,
            ["cancel-operation"] = DispatchCancelOperationAsync,
            ["close-model"] = DispatchCloseModelAsync,
            ["extract-materials"] = DispatchExtractMaterialsAsync,
            ["extract-results"] = DispatchExtractResultsAsync,
            ["generate-e2k"] = DispatchGenerateE2KAsync,
            ["get-model-state"] = DispatchGetModelStateAsync,
            ["get-operation-events"] = DispatchGetOperationEventsAsync,
            ["get-operation-status"] = DispatchGetOperationStatusAsync,
            ["get-status"] = DispatchGetStatusAsync,
            ["inspect-wall-property"] = DispatchInspectWallPropertyAsync,
            ["list-wall-properties"] = DispatchListWallPropertiesAsync,
            ["open-model"] = DispatchOpenModelAsync,
            ["read-model-metadata"] = DispatchReadModelMetadataAsync,
            ["resolve-area-targets"] = DispatchResolveAreaTargetsAsync,
            ["run-analysis"] = DispatchRunAnalysisAsync,
            ["snapshot-export"] = DispatchSnapshotExportAsync,
            ["start-operation"] = DispatchStartOperationAsync,
            ["unlock-model"] = DispatchUnlockModelAsync
        };
        _handlers = handlers;
        Capabilities = handlers.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public Task<object> DispatchAsync(
        string command,
        JsonElement? request,
        CancellationToken ct)
    {
        // Freeze this request's declared intent and its CLI #24 label into one immutable
        // context, on the thread where the declaration is valid. Nothing about the session
        // is touched here: while an async operation runs, the worker holds the session lock
        // for its whole duration, and a protocol thread that blocked on it would stop
        // answering get-operation-status - the one thing that must keep working.
        _pendingWork = _envelope.Capture(command);

        return _handlers.TryGetValue(command, out var handler)
            ? handler(request, ct)
            : Task.FromResult<object>(Result.Fail(
                $"Command not supported in serve mode yet: '{command}'"));
    }

    private async Task<object> DispatchGetStatusAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        // During an async operation the protocol thread must never issue COM
        // calls. Report the most recent worker-owned snapshot instead.
        if (_operations.HasActiveOperation)
        {
            return ReadActiveStatus();
        }

        var work = _pendingWork;
        return await _operations.ExecuteSynchronousAsync(() => _envelope.RunAsync(work, () =>
        {
            try
            {
                var observation = _processes.ObserveEtabs();
                var current = _session.IsStarted
                    ? _status.GetStatusOnApp(_session.GetOrStart(), _session.ProcessId)
                    : Result.Ok(new GetStatusData());
                current = EtabsStatusOwnership.Decorate(
                    current,
                    observation,
                    _session.ProcessId);
                _cachedStatus.Update(current);
                return Task.FromResult<object>(current);
            }
            catch (Exception ex)
            {
                return Task.FromResult<object>(Result.Fail<GetStatusData>(
                    $"ETABS process observation failed: {ex.Message}"));
            }
        }));
    }

    private Task<object> DispatchStartOperationAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<StartOperationRequest>(request);
        return Task.FromResult<object>(_operations.Start(req.Kind, req.Payload));
    }

    private Task<object> DispatchGetOperationStatusAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<OperationIdRequest>(request);
        return Task.FromResult<object>(_operations.GetStatus(req.OperationId));
    }

    private Task<object> DispatchGetOperationEventsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<GetOperationEventsRequest>(request);
        return Task.FromResult<object>(_operations.GetEvents(req.OperationId, req.SinceSeq));
    }

    private Task<object> DispatchCancelOperationAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<OperationIdRequest>(request);
        return Task.FromResult<object>(_operations.Cancel(req.OperationId));
    }

    /// <summary>
    /// The daemon's only user-visible ETABS intent.
    ///
    /// <para>Every other handler here is background work against a session that was
    /// created hidden and stays hidden. This one — and only this one — ends with ETABS on
    /// screen, because the engineer asked to look at a model.</para>
    /// </summary>
    private async Task<object> DispatchOpenModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeOpenModelRequest>(request);
        return await ExecuteComAsync(async () => (object)RevealAfterConfirmedOpen(
            await _open.OpenModelOnAppAsync(
                _session.GetOrStart(),
                req.FilePath,
                req.SaveOnClose)));
    }

    /// <summary>
    /// Shows ETABS once the requested model is confirmed open — never before.
    ///
    /// <para>The ordering is the point of CLI #22. Revealing first is exactly the
    /// packaged-RC symptom: a blank <c>(Untitled)</c> window on screen for seconds while
    /// the real model is still loading, which reads as a hung application. A failed open
    /// therefore reveals nothing at all.</para>
    ///
    /// <para>An open that cannot be made visible is reported as a FAILURE even though the
    /// model is loaded: "Open in ETABS" that leaves nothing on screen has not done what was
    /// asked, and the response names the CSI call that disagreed rather than claiming a
    /// success the engineer cannot see.</para>
    /// </summary>
    private Result<OpenModelData> RevealAfterConfirmedOpen(Result<OpenModelData> opened)
    {
        if (!opened.Success)
        {
            return opened;
        }

        var revealed = _session.RevealForExplicitUserRequest();
        return revealed.Success
            ? opened
            : Result.Fail<OpenModelData>(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                revealed.Error ?? "Managed ETABS could not be confirmed visible.",
                $"modelOpened={opened.Data?.FilePath}"));
    }

    private async Task<object> DispatchAnalyzeAndExtractAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        // Frozen Rust compatibility: start through the generic envelope,
        // then internally wait and return the original Result<T> unchanged.
        var payload = RequirePayload(request);
        var started = _operations.Start("analyze-and-extract", payload);
        if (!started.Success || started.Data is null)
        {
            return Result.Fail<AnalyzeAndExtractData>(
                started.Error ?? "Could not start analyze-and-extract operation");
        }

        return await _operations.WaitAsync(started.Data.OperationId, ct);
    }

    private async Task<object> DispatchSnapshotExportAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var loc = Deserialize<ServeFileLocator>(request);
        var snapReq = Deserialize<SnapshotExportRequest>(request);
        return await ExecuteComAsync(async () => await _snapshot.SnapshotExportOnAppAsync(
            _session.GetOrStart(), loc.FilePath, loc.OutputDir, snapReq));
    }

    private async Task<object> DispatchCloseModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeCloseModelRequest>(request);
        return await ExecuteComAsync(async () => await _close.CloseModelOnAppAsync(
            _session.GetOrStart(), req.Save));
    }

    private async Task<object> DispatchUnlockModelAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeFileRequest>(request);
        return await ExecuteComAsync(async () => await _unlock.UnlockModelOnAppAsync(
            _session.GetOrStart(), req.FilePath));
    }

    private async Task<object> DispatchExtractResultsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        return await ExecuteComAsync(async () => await _extractResults.ExtractOnAppAsync(
            _session.GetOrStart(), Deserialize<ExtractResultsRequest>(request)));
    }

    private async Task<object> DispatchExtractMaterialsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        return await ExecuteComAsync(async () => await _extractMaterials.ExtractMaterialsOnAppAsync(
            _session.GetOrStart(), Deserialize<ExtractMaterialsRequest>(request)));
    }

    private async Task<object> DispatchGenerateE2KAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeGenerateE2KRequest>(request);
        return await ExecuteComAsync(async () => await _generateE2K.GenerateE2KOnAppAsync(
            _session.GetOrStart(), req.FilePath, req.OutputFile, req.Overwrite));
    }

    private async Task<object> DispatchReadModelMetadataAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeFileRequest>(request);
        return await ExecuteComAsync(async () => await _metadata.ReadOnAppAsync(
            _session.GetOrStart(), req.FilePath));
    }

    private async Task<object> DispatchRunAnalysisAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ServeRunAnalysisRequest>(request);
        return await ExecuteComAsync(async () => await _runAnalysis.RunAnalysisOnAppAsync(
            _session.GetOrStart(), req.FilePath, req.Cases, req.Units));
    }

    private Task<object> DispatchGetModelStateAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(
                _inspection.GetModelState(api, _sessionRecords.Read()));
        });
    }

    private Task<object> DispatchListWallPropertiesAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = request;
        _ = ct;
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(_inspection.ListWallProperties(api));
        });
    }

    private Task<object> DispatchInspectWallPropertyAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<InspectWallPropertyRequest>(request);
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(_inspection.InspectWallProperty(api, req.Name));
        });
    }

    private Task<object> DispatchResolveAreaTargetsAsync(
        JsonElement? request,
        CancellationToken ct)
    {
        _ = ct;
        var req = Deserialize<ResolveAreaTargetsRequest>(request);
        return ExecuteComAsync(() =>
        {
            var api = _inspectionApiFactory.Create(_session.GetOrStart());
            return Task.FromResult<object>(
                _inspection.ResolveAreaTargets(api, req.SourceProperty));
        });
    }

    private static T Deserialize<T>(JsonElement? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("Missing 'request' payload for this command");
        }

        return request.Value.Deserialize<T>(ServeJson.Options)
            ?? throw new InvalidOperationException("Request payload deserialised to null");
    }

    private static JsonElement RequirePayload(JsonElement? request) => request
        ?? throw new InvalidOperationException("Missing 'request' payload for this command");

    /// <summary>
    /// The context captured for the request currently being dispatched.
    ///
    /// <para>Written on the protocol thread at the top of
    /// <see cref="DispatchAsync"/> and read there again when the synchronous lane hands
    /// work to the COM worker - both on the same thread, and the serve loop dispatches
    /// strictly one request at a time. From the hand-off onwards the value travels as an
    /// immutable copy, so nothing the worker relies on can be overwritten underneath
    /// it.</para>
    /// </summary>
    private EtabsWorkContext _pendingWork = EtabsWorkContext.None;

    /// <summary>
    /// Every ETABS-backed command runs through here, which makes it the one place a
    /// CLI #24 certification can cover them all.
    ///
    /// <para>The check runs AFTER the work and BEFORE the response leaves: a background
    /// command that put ETABS on screen must not answer success just because ETABS hid
    /// itself again first. A failed certification replaces the result outright rather than
    /// annotating it - a partially-successful export whose session breached the visibility
    /// contract is not a success the desktop should act on.</para>
    /// </summary>
    /// <summary>
    /// The synchronous ETABS lane. Same envelope as the queued lane - captured context in,
    /// stage labelled, completion certified - so the two lanes cannot drift apart in what
    /// the visibility contract means.
    /// </summary>
    private Task<object> ExecuteComAsync(Func<Task<object>> action)
    {
        if (_operations.HasActiveOperation)
        {
            return Task.FromResult<object>(Result.Fail(
                "A daemon operation is active; synchronous ETABS commands are unavailable until it completes"));
        }

        var work = _pendingWork;
        return _operations.ExecuteSynchronousAsync(() => _envelope.RunAsync(work, action));
    }

    private Result<GetStatusData> ReadActiveStatus()
    {
        try
        {
            return _cachedStatus.Read(_session, _processes.ObserveEtabs());
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>(
                $"ETABS process observation failed: {ex.Message}");
        }
    }
}
