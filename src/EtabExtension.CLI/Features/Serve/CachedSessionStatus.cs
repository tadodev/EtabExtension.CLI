using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

public interface ICachedSessionStatus
{
    void Update(Result<GetStatusData> status);
    Result<GetStatusData> Read(IEtabsSession session);
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

    public Result<GetStatusData> Read(IEtabsSession session)
    {
        lock (_gate)
        {
            if (_status is { Success: true, Data: not null })
            {
                return Result.Ok(_status.Data with
                {
                    IsRunning = session.IsStarted,
                    Pid = session.ProcessId,
                    Ownership = session.IsStarted
                        ? EtabsInstanceOwnership.Managed
                        : EtabsInstanceOwnership.None,
                    ObservedPids = session.ProcessId is int cachedPid ? [cachedPid] : []
                });
            }

            return Result.Ok(new GetStatusData
            {
                IsRunning = session.IsStarted,
                Pid = session.ProcessId,
                Ownership = session.IsStarted
                    ? EtabsInstanceOwnership.Managed
                    : EtabsInstanceOwnership.None,
                ObservedPids = session.ProcessId is int fallbackPid ? [fallbackPid] : []
            });
        }
    }
}
