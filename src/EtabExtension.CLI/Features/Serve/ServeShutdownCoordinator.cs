using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

public interface IServeShutdownCoordinator : IAsyncDisposable
{
    Task<Result<ManagedEtabsShutdownData>> ShutdownAsync();
}

public sealed class ServeShutdownCoordinator(
    IStaExecutionWorker worker,
    IEtabsSession session) : IServeShutdownCoordinator
{
    private readonly object _gate = new();
    private Task<Result<ManagedEtabsShutdownData>>? _shutdown;

    public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
    {
        lock (_gate)
        {
            return _shutdown ??= RunShutdownAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task<Result<ManagedEtabsShutdownData>> RunShutdownAsync()
    {
        try
        {
            var terminal = await worker.ExecuteAsync(
                () => Task.FromResult(session.Shutdown())).ConfigureAwait(false);
            if (terminal.Success)
            {
                return Result.Ok(terminal.Data);
            }

            var errorCode = terminal.ErrorCode
                ?? ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed;
            var diagnostic = terminal.Error
                ?? "Managed ETABS shutdown returned failure without a diagnostic.";
            return new(
                false,
                terminal.Data,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    diagnostic,
                    $"code={errorCode}"));
        }
        catch (Exception exception)
        {
            var errorCode = ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed;
            var data = new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ProcessExitConfirmed: false,
                Forced: false,
                RecordRetained: true,
                ApplicationExitReturnCode: null,
                OwnedPid: session.ProcessId);
            return new(
                false,
                data,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IEtabsSession.Shutdown",
                        exception),
                    $"code={errorCode}"));
        }
        finally
        {
            worker.Dispose();
        }
    }
}
