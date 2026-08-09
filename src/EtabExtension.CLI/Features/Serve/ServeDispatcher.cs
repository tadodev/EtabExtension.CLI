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
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.Serve.Inspection;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. All commands here operate on the SAME instance
/// (<see cref="IEtabsSession.GetOrStart"/>) — that is the fix for the
/// multi-instance golden-path bug.
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
        IProcessInspector processes)
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
    }

    public async Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
    {
        switch (command)
        {
            case "get-status":
                // During an async operation the protocol thread must never issue COM
                // calls. Report the most recent worker-owned snapshot instead.
                if (_operations.HasActiveOperation)
                {
                    return _cachedStatus.Read(_session);
                }
                return await _operations.ExecuteSynchronousAsync(() =>
                {
                    var observation = _processes.ObserveEtabs();
                    var current = _session.IsStarted
                        ? _status.GetStatusOnApp(_session.GetOrStart(), _session.ProcessId)
                        : Result.Ok(new GetStatusData());
                    current = DecorateStatus(current, observation, _session.ProcessId);
                    _cachedStatus.Update(current);
                    return Task.FromResult<object>(current);
                });

            case "start-operation":
            {
                var req = Deserialize<StartOperationRequest>(request);
                return _operations.Start(req.Kind, req.Payload);
            }

            case "get-operation-status":
            {
                var req = Deserialize<OperationIdRequest>(request);
                return _operations.GetStatus(req.OperationId);
            }

            case "get-operation-events":
            {
                var req = Deserialize<GetOperationEventsRequest>(request);
                return _operations.GetEvents(req.OperationId, req.SinceSeq);
            }

            case "cancel-operation":
            {
                var req = Deserialize<OperationIdRequest>(request);
                return _operations.Cancel(req.OperationId);
            }

            case "open-model":
            {
                var req = Deserialize<ServeOpenModelRequest>(request);
                return await ExecuteComAsync(async () => await _open.OpenModelOnAppAsync(
                    _session.GetOrStart(), req.FilePath, req.SaveOnClose));
            }

            case "analyze-and-extract":
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

            case "snapshot-export":
            {
                var loc = Deserialize<ServeFileLocator>(request);
                var snapReq = Deserialize<SnapshotExportRequest>(request);
                return await ExecuteComAsync(async () => await _snapshot.SnapshotExportOnAppAsync(
                    _session.GetOrStart(), loc.FilePath, loc.OutputDir, snapReq));
            }

            case "close-model":
            {
                var req = Deserialize<ServeCloseModelRequest>(request);
                return await ExecuteComAsync(async () => await _close.CloseModelOnAppAsync(
                    _session.GetOrStart(), req.Save));
            }

            case "unlock-model":
            {
                var req = Deserialize<ServeFileRequest>(request);
                return await ExecuteComAsync(async () => await _unlock.UnlockModelOnAppAsync(
                    _session.GetOrStart(), req.FilePath));
            }

            case "extract-results":
                return await ExecuteComAsync(async () => await _extractResults.ExtractOnAppAsync(
                    _session.GetOrStart(), Deserialize<ExtractResultsRequest>(request)));

            case "extract-materials":
                return await ExecuteComAsync(async () => await _extractMaterials.ExtractMaterialsOnAppAsync(
                    _session.GetOrStart(), Deserialize<ExtractMaterialsRequest>(request)));

            case "generate-e2k":
            {
                var req = Deserialize<ServeGenerateE2KRequest>(request);
                return await ExecuteComAsync(async () => await _generateE2K.GenerateE2KOnAppAsync(
                    _session.GetOrStart(), req.FilePath, req.OutputFile, req.Overwrite));
            }

            case "read-model-metadata":
            {
                var req = Deserialize<ServeFileRequest>(request);
                return await ExecuteComAsync(async () => await _metadata.ReadOnAppAsync(
                    _session.GetOrStart(), req.FilePath));
            }

            case "get-model-state":
                return await ExecuteComAsync(() =>
                {
                    var api = _inspectionApiFactory.Create(_session.GetOrStart());
                    return Task.FromResult<object>(
                        _inspection.GetModelState(api, _sessionRecords.Read()));
                });

            case "list-wall-properties":
                return await ExecuteComAsync(() =>
                {
                    var api = _inspectionApiFactory.Create(_session.GetOrStart());
                    return Task.FromResult<object>(_inspection.ListWallProperties(api));
                });

            case "inspect-wall-property":
            {
                var req = Deserialize<InspectWallPropertyRequest>(request);
                return await ExecuteComAsync(() =>
                {
                    var api = _inspectionApiFactory.Create(_session.GetOrStart());
                    return Task.FromResult<object>(_inspection.InspectWallProperty(api, req.Name));
                });
            }

            case "resolve-area-targets":
            {
                var req = Deserialize<ResolveAreaTargetsRequest>(request);
                return await ExecuteComAsync(() =>
                {
                    var api = _inspectionApiFactory.Create(_session.GetOrStart());
                    return Task.FromResult<object>(
                        _inspection.ResolveAreaTargets(api, req.SourceProperty));
                });
            }

            default:
                return Result.Fail($"Command not supported in serve mode yet: '{command}'");
        }
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

    private Task<object> ExecuteComAsync(Func<Task<object>> action) =>
        _operations.HasActiveOperation
            ? Task.FromResult<object>(Result.Fail(
                "A daemon operation is active; synchronous ETABS commands are unavailable until it completes"))
            : _operations.ExecuteSynchronousAsync(action);

    private static Result<GetStatusData> DecorateStatus(
        Result<GetStatusData> status,
        EtabsProcessObservation observation,
        int? managedPid)
    {
        if (!status.Success || status.Data is null)
        {
            return status;
        }

        var ownership = EtabsOwnershipResolver.Resolve(observation, managedPid);
        if (managedPid.HasValue && ownership == EtabsInstanceOwnership.None)
        {
            ownership = EtabsInstanceOwnership.Ambiguous;
        }
        var observedPids = observation.Identified
            .Select(identity => identity.Pid)
            .Distinct()
            .Order()
            .ToList();

        return Result.Ok(status.Data with
        {
            IsRunning = ownership != EtabsInstanceOwnership.None,
            Pid = ownership == EtabsInstanceOwnership.External
                ? observedPids.SingleOrDefault()
                : managedPid,
            Ownership = ownership,
            ObservedPids = observedPids
        });
    }
}
