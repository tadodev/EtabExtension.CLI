using System.CommandLine;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

public static class ServeCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "serve",
            "Long-lived daemon: one hidden ETABS instance shared across all requests. " +
            "Reads line-delimited JSON requests on stdin, writes one JSON response per line on stdout.");

        command.SetAction(async _ =>
        {
            // One DI scope for the daemon's whole life so the shared session and the
            // (scoped) feature services live and die together.
            await using var scope = services.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var dispatcher = provider.GetRequiredService<IServeDispatcher>();
            var orphanCleaner = provider.GetRequiredService<IOrphanSessionCleaner>();
            var shutdown = provider.GetRequiredService<IServeShutdownCoordinator>();

            // Program.cs redirects Console.Out to stderr — write the protocol to the
            // REAL stdout. "\n" line endings keep framing clean for the Rust reader.
            await using var stdout = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var stdin = new StreamReader(Console.OpenStandardInput());

            await RunLifecycleAsync(
                orphanCleaner,
                shutdown,
                () => new ServeLoop(dispatcher, shutdown).RunAsync(stdin, stdout),
                Console.Error);
        });

        return command;
    }

    internal static async Task RunLifecycleAsync(
        IOrphanSessionCleaner orphanCleaner,
        IServeShutdownCoordinator shutdown,
        Func<Task> runLoop,
        TextWriter diagnostics)
    {
        ArgumentNullException.ThrowIfNull(orphanCleaner);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(runLoop);
        ArgumentNullException.ThrowIfNull(diagnostics);

        try
        {
            var recovery = orphanCleaner.Clean();
            if (!recovery.Success)
            {
                throw new EtabsLaunchException(
                    recovery.ErrorCode ?? ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                    recovery.Error ?? "Managed ETABS orphan recovery failed closed.");
            }

            await runLoop();
        }
        finally
        {
            try
            {
                var cleanup = await shutdown.ShutdownAsync();
                if (!cleanup.Success)
                {
                    var diagnostic = cleanup.Error
                        ?? "Serve cleanup failed without a diagnostic.";
                    await diagnostics.WriteLineAsync(
                        EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                            diagnostic,
                            $"state={cleanup.Data?.State.ToString() ?? "unknown"}; " +
                            $"processExitConfirmed={cleanup.Data?.ProcessExitConfirmed ?? false}; " +
                            $"recordRetained={cleanup.Data?.RecordRetained ?? true}"));
                }
                await diagnostics.FlushAsync();
            }
            catch (Exception cleanupException)
            {
                await diagnostics.WriteLineAsync(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeShutdownCoordinator.ShutdownAsync",
                        cleanupException));
                await diagnostics.FlushAsync();
            }
        }
    }
}
