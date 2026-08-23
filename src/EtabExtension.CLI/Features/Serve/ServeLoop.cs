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
    private readonly IManagedEtabsStartIntentScope _startIntent;
    private readonly ServeHandshake _handshake;
    private readonly TextWriter _diagnostics;

    public ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown,
        IManagedEtabsStartIntentScope startIntent) : this(
            dispatcher,
            shutdown,
            startIntent,
            ServeHandshake.Current,
            Console.Error)
    {
    }

    internal ServeLoop(
        IServeDispatcher dispatcher,
        IServeShutdownCoordinator shutdown,
        IManagedEtabsStartIntentScope startIntent,
        Func<IReadOnlyList<string>, ServeHandshake> handshakeFactory,
        TextWriter diagnostics)
    {
        _dispatcher = dispatcher;
        _shutdown = shutdown;
        _startIntent = startIntent;
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
    ///
    /// <para>A handler may ACCEPT a request and answer it later by returning a
    /// <see cref="DeferredServeResponse"/>. The reader then goes straight back to stdin
    /// instead of waiting, which is what keeps <c>get-operation-status</c>,
    /// <c>get-operation-events</c>, <c>cancel-operation</c> and <c>shutdown</c> reachable
    /// while a long operation runs. It introduces no concurrent ETABS work: exactly one
    /// dispatch executes at a time, and every COM call still goes through the single STA
    /// worker.</para>
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var explicitShutdown = false;

        // Accepted-but-unanswered requests. The operation lease permits one at a time, so
        // this holds at most one live entry; completed ones are pruned as they land.
        var deferred = new List<Task>();

        // One line at a time on stdout: a deferred answer is written from its own
        // continuation, and two responses interleaved mid-line would corrupt the protocol
        // for every request after them, not just those two. Owned by this run and released
        // only once every accepted request has been settled in the finally below.
        using var writes = new SemaphoreSlim(1, 1);
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
                        writes,
                        output,
                        id: 0,
                        Result.Fail($"Invalid request JSON: {exception.Message}"));
                    continue;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Command))
                {
                    await WriteAsync(
                        writes,
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

                    // Answer everything already accepted before announcing the daemon is
                    // gone. An id a caller is still blocked on must not be dropped.
                    await DrainAsync(deferred);
                    await WriteAsync(writes, output, request.Id, terminal);
                    return;
                }

                var response = await DispatchIsolatedAsync(request, ct);
                if (response is DeferredServeResponse pending)
                {
                    // Accepted, not answered. Straight back to stdin so control commands
                    // stay reachable for the whole run.
                    deferred.RemoveAll(item => item.IsCompleted);
                    deferred.Add(AnswerWhenReadyAsync(
                        writes, output, request.Id, request.Command, pending));
                    continue;
                }

                await WriteAsync(writes, output, request.Id, response);
            }
        }
        finally
        {
            // Settle accepted requests first, then release the session - in that order and
            // in separate guards, because a stuck answer must not be the reason ETABS is
            // left running, and a failing cleanup must not be the reason a caller waits
            // forever.
            try
            {
                await DrainAsync(deferred);
            }
            catch (Exception deferredException)
            {
                await WriteCleanupExceptionAsync(deferredException);
            }

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
            // Scoped to THIS request and cleared on the way out, so a consented cold start
            // can never be inherited by the next request that did not declare one.
            using var intent = _startIntent.Publish(
                ManagedEtabsStartIntents.Parse(request.StartIntent));
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
    /// Writes the answer to a request that was ACCEPTED earlier, once its work finishes.
    ///
    /// <para>Every failure becomes a response, cancellation included. A caller blocked on
    /// an id that never gets answered is a worse outcome than any diagnostic: the accepted
    /// request must be settled exactly once, whatever happened to the work behind it.</para>
    /// </summary>
    private static async Task AnswerWhenReadyAsync(
        SemaphoreSlim writes,
        TextWriter output,
        long id,
        string command,
        DeferredServeResponse pending)
    {
        object result;
        try
        {
            result = await pending.Completion;
        }
        catch (Exception exception)
        {
            result = Result.Fail(
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "IServeDispatcher deferred completion",
                        exception),
                    $"command={command}"));
        }

        await WriteAsync(writes, output, id, result);
    }

    /// <summary>Settles every accepted-but-unanswered request before the loop exits.</summary>
    private static async Task DrainAsync(List<Task> deferred)
    {
        if (deferred.Count == 0)
        {
            return;
        }

        var outstanding = deferred.ToArray();
        deferred.Clear();
        await Task.WhenAll(outstanding);
    }

    /// <summary>
    /// Serialize the feature result by its <b>runtime</b> type (so the concrete
    /// <c>Result&lt;T&gt;.data</c> is emitted, not an empty <c>object</c>), inject
    /// the correlation <c>id</c>, and write one compact line.
    ///
    /// <para>Serialized on the run's write gate: a deferred answer is written from its own
    /// continuation, and two responses interleaved mid-line would corrupt stdout for every
    /// request after them, not just those two.</para>
    /// </summary>
    private static async Task WriteAsync(
        SemaphoreSlim writes,
        TextWriter output,
        long id,
        object result)
    {
        var node = JsonSerializer.SerializeToNode(result, result.GetType(), ServeJson.Options)!.AsObject();
        node["id"] = id;
        var line = node.ToJsonString(ServeJson.Options);

        await writes.WaitAsync();
        try
        {
            await output.WriteLineAsync(line);
            await output.FlushAsync();
        }
        finally
        {
            writes.Release();
        }
    }
}
