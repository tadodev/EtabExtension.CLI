using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

public interface ICachedSessionStatus
{
    void Update(Result<GetStatusData> status);
    Result<GetStatusData> Read(
        IEtabsSession session,
        EtabsProcessObservation observation);
}

public sealed class CachedSessionStatus : ICachedSessionStatus
{
    private readonly object _gate = new();
    private Result<GetStatusData>? _status;

    public void Update(Result<GetStatusData> status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }

    public Result<GetStatusData> Read(
        IEtabsSession session,
        EtabsProcessObservation observation)
    {
        lock (_gate)
        {
            if (_status is { Success: false })
            {
                return _status;
            }

            var baseline = _status ?? Result.Ok(new GetStatusData());
            return EtabsStatusOwnership.Decorate(
                baseline,
                observation,
                session.ProcessId);
        }
    }
}

internal static class EtabsStatusOwnership
{
    public static Result<GetStatusData> Decorate(
        Result<GetStatusData> status,
        EtabsProcessObservation observation,
        int? managedPid)
    {
        if (!status.Success)
        {
            return status;
        }

        if (status.Data is null)
        {
            return Result.Fail<GetStatusData>(
                "ETABS status reported success without data");
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
        var pid = ownership switch
        {
            EtabsInstanceOwnership.Managed => managedPid,
            EtabsInstanceOwnership.External => observedPids.SingleOrDefault(),
            EtabsInstanceOwnership.Ambiguous => managedPid,
            _ => null
        };

        return Result.Ok(status.Data with
        {
            IsRunning = ownership != EtabsInstanceOwnership.None,
            Pid = pid,
            Ownership = ownership,
            ObservedPids = observedPids
        });
    }
}
