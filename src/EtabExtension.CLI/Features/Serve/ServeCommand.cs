using System.CommandLine;
using System.Text.Json;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// How serve startup ended. <see cref="StartupRefused"/> means orphan recovery
/// fail-closed: the refusal frame was written, the request loop never ran, and
/// the retained recovery record is untouched.
/// </summary>
public enum ServeLifecycleOutcome
{
    Served,
    StartupRefused
}

public static class ServeCommand
{
    /// <summary>Nonzero exit for a refused startup — same 0/1 convention as the one-shot commands.</summary>
    internal const int StartupRefusedExitCode = 1;

    public static Command Create(IServiceProvider services)
    {
        var command = new Command(
            "serve",
            "Long-lived daemon: one owned ETABS instance shared across all requests. " +
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
            var records = provider.GetRequiredService<ISessionRecordStore>();
            var startIntent = provider.GetRequiredService<IManagedEtabsStartIntentScope>();

            // Program.cs redirects Console.Out to stderr — write the protocol to the
            // REAL stdout. "\n" line endings keep framing clean for the Rust reader.
            await using var stdout = new StreamWriter(Console.OpenStandardOutput())
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var stdin = new StreamReader(Console.OpenStandardInput());

            var outcome = await RunLifecycleAsync(
                orphanCleaner,
                shutdown,
                () => new ServeLoop(dispatcher, shutdown, startIntent).RunAsync(stdin, stdout),
                stdout,
                Console.Error,
                recovery => ServeStartupRefusal.Current(recovery, records.FilePath));

            if (outcome == ServeLifecycleOutcome.StartupRefused)
            {
                await stdout.FlushAsync();
                Environment.Exit(StartupRefusedExitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// Runs orphan recovery, then the request loop, then converges on the one
    /// idempotent shutdown coordinator.
    ///
    /// <para>Recovery runs before the loop on purpose — a daemon must not serve
    /// requests while a previous managed ETABS process may still be alive. When it
    /// fails closed, startup is still <b>protocol-visible</b>: exactly one
    /// <c>refused</c> frame is written to <paramref name="output"/>, no <c>ready</c>
    /// handshake is emitted, no request is read, and the retained recovery record is
    /// left alone. A consumer therefore sees a typed refusal instead of an opaque
    /// process exit, and can stop respawning into the same refusal.</para>
    /// </summary>
    internal static async Task<ServeLifecycleOutcome> RunLifecycleAsync(
        IOrphanSessionCleaner orphanCleaner,
        IServeShutdownCoordinator shutdown,
        Func<Task> runLoop,
        TextWriter output,
        TextWriter diagnostics,
        Func<ManagedEtabsShutdownResult, ServeStartupRefusal> refusalFactory)
    {
        ArgumentNullException.ThrowIfNull(orphanCleaner);
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(runLoop);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(refusalFactory);

        try
        {
            var recovery = orphanCleaner.Clean();
            if (!recovery.Success)
            {
                var refusal = refusalFactory(recovery);
                await output.WriteLineAsync(
                    JsonSerializer.Serialize(refusal, ServeJson.Options));
                await output.FlushAsync();
                await diagnostics.WriteLineAsync(
                    EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        refusal.Error,
                        $"startup=refused; code={refusal.ErrorCode}; " +
                        $"state={refusal.State}; recordRetained={refusal.RecordRetained}; " +
                        $"ownedPid={refusal.OwnedPid?.ToString() ?? "unknown"}"));
                await diagnostics.FlushAsync();
                return ServeLifecycleOutcome.StartupRefused;
            }

            await runLoop();
            return ServeLifecycleOutcome.Served;
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
