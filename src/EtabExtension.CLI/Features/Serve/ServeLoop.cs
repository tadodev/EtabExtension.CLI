using System.Text.Json;
using System.Text.Json.Nodes;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// The persistent stdin/stdout request loop for <c>etab-cli serve</c>.
///
/// Reads one JSON request per line, dispatches it <b>serially</b> (one request in
/// flight at a time — ETABS COM is single-threaded), and writes exactly one
/// compact JSON response per line. Transport only: all ETABS work happens in the
/// injected <see cref="IServeDispatcher"/> against a single shared session.
///
/// Testable in isolation: pass any <see cref="TextReader"/>/<see cref="TextWriter"/>
/// and a fake dispatcher — no ETABS required.
/// </summary>
public sealed class ServeLoop
{
    internal const string ShutdownCommand = "shutdown";

    private readonly IServeDispatcher _dispatcher;
    private readonly IServeShutdownCoordinator _shutdown;
    private readonly ServeHandshake _handshake;
    private readonly TextWriter _diagnostics;

    public ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown) : this(
            dispatcher,
            shutdown,
            ServeHandshake.Current,
            Console.Error)
    {
    }

    internal ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown,
        Func<IReadOnlyList<string>, ServeHandshake> handshakeFactory,
        TextWriter diagnostics)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
        _diagnostics = diagnostics;
        var capabilities = dispatcher.Capabilities
            .Append(ShutdownCommand)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _handshake = handshakeFactory(capabilities);
    }

    /// <summary>
    /// Runs until stdin EOF, a <c>shutdown</c> command, or cancellation. Never
    /// throws for a bad request — malformed lines and failing handlers both get a
    /// correlated error response and the loop keeps serving. Only transport and
    /// cancellation failures end the run.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var explicitShutdown = false;
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                _handshake, ServeJson.Options));
            await output.FlushAsync(ct);
            Console.Error.WriteLine("ℹ etab-cli serve: ready (line-delimited JSON on stdin/stdout)");

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await input.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ServeRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<ServeRequest>(line, ServeJson.Options);
                }
                catch (JsonException exception)
                {
                    await WriteAsync(
                        output,
                        id: 0,
                        Result.Fail($"Invalid request JSON: {exception.Message}"));
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Command))
                {
                    await WriteAsync(
                        output,
                        request?.Id ?? 0,
                        Result.Fail("Malformed request: missing command"));
                    continue;
                }

                if (string.Equals(
                    request.Command,
                    ShutdownCommand,
                    StringComparison.OrdinalIgnoreCase))
                {
                    explicitShutdown = true;
                    var terminal = await _shutdown.ShutdownAsync();
                    await WriteAsync(output, request.Id, terminal);
                    return;
                }

                await WriteAsync(
                    output,
                    request.Id,
                    await DispatchIsolatedAsync(request, ct));
            }
        }
        finally
        {
            try
            {
                var cleanup = await _shutdown.ShutdownAsync();
                if (!cleanup.Success && !explicitShutdown)
                {
                    await WriteCleanupFailureAsync(cleanup);
                }
            }
            catch (Exception cleanupException)
            {
                await WriteCleanupExceptionAsync(cleanupException);
            }
        }
    }

    /// <summary>
    /// Runs one request against the dispatcher with its failures contained to that
    /// request. A bad payload — a missing <c>request</c> object, a wrong field type,
    /// an unexpected handler or COM escape — becomes one bounded correlated failure
    /// response, and the daemon keeps serving. Without this the offending request
    /// would get no response at all and would take the managed ETABS session down
    /// with the loop.
    ///
    /// <para>Cancellation is deliberately not contained: when the serve token is
    /// cancelled the daemon really is stopping, so it propagates and terminates
    /// through the same shutdown coordinator as every other exit path.</para>
    ///
    /// <para>A typed managed-session failure keeps its own code. Wrapping an
    /// <see cref="EtabsLaunchException"/> in the generic infrastructure envelope
    /// would bury the one token a consumer branches on — the live proof surfaced
    /// <c>ETABS_MODEL_INITIALIZATION_FAILED</c> only inside a generic message.</para>
    /// </summary>
    private async Task<object> DispatchIsolatedAsync(ServeRequest request, CancellationToken ct)
    {
        try
        {
            return await _dispatcher.DispatchAsync(request.Command, request.Request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EtabsLaunchException launch)
        {
            return Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.Bounded(launch.Message),
                    $"command={request.Command}"));
        }
        catch (Exception exception)
        {
            return Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeDispatcher.DispatchAsync",
                        exception),
                    $"command={request.Command}"));
        }
    }

    private async Task WriteCleanupFailureAsync(
        Result<ManagedEtabsShutdownData> cleanup)
    {
        var diagnostic = cleanup.Error
            ?? "Serve cleanup failed without a diagnostic.";
        var bounded = EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            diagnostic,
            $"state={cleanup.Data?.State.ToString() ?? "unknown"}; " +
            $"processExitConfirmed={cleanup.Data?.ProcessExitConfirmed ?? false}; " +
            $"recordRetained={cleanup.Data?.RecordRetained ?? true}");
        await _diagnostics.WriteLineAsync(bounded);
        await _diagnostics.FlushAsync();
    }

    private async Task WriteCleanupExceptionAsync(Exception exception)
    {
        var bounded = EtabsApiDiagnosticFormatter.InfrastructureException(
            "IServeShutdownCoordinator.ShutdownAsync",
            exception);
        await _diagnostics.WriteLineAsync(bounded);
        await _diagnostics.FlushAsync();
    }

    /// <summary>
    /// Serialize the feature result by its <b>runtime</b> type (so the concrete
    /// <c>Result&lt;T&gt;.data</c> is emitted, not an empty <c>object</c>), inject
    /// the correlation <c>id</c>, and write one compact line.
    /// </summary>
    private static async Task WriteAsync(TextWriter output, long id, object result)
    {
        var node = JsonSerializer.SerializeToNode(result, result.GetType(), ServeJson.Options)!.AsObject();
        node["id"] = id;
        await output.WriteLineAsync(node.ToJsonString(ServeJson.Options));
        await output.FlushAsync();
    }
}
