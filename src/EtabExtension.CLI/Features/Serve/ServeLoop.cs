using System.Text.Json;
using System.Text.Json.Nodes;
using EtabExtension.CLI.Shared.Common;

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
    private readonly IServeDispatcher _dispatcher;

    public ServeLoop(IServeDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Runs until stdin EOF, a <c>shutdown</c> command, or cancellation. Never
    /// throws for a bad request — malformed lines get an error response and the
    /// loop keeps serving.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(
            ServeHandshake.Current(), ServeJson.Options));
        await output.FlushAsync(ct);
        Console.Error.WriteLine("ℹ etab-cli serve: ready (line-delimited JSON on stdin/stdout)");

        while (!ct.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(ct);
            if (line is null)
            {
                break; // stdin closed
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            long id = 0;
            try
            {
                var request = JsonSerializer.Deserialize<ServeRequest>(line, ServeJson.Options);
                if (request is null || string.IsNullOrWhiteSpace(request.Command))
                {
                    await WriteAsync(output, id, Result.Fail("Malformed request: missing command"));
                    continue;
                }

                id = request.Id;

                if (string.Equals(request.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(output, id, Result.Ok());
                    return; // graceful shutdown — caller disposes the session
                }

                object result = await _dispatcher.DispatchAsync(request.Command, request.Request, ct);
                await WriteAsync(output, id, result);
            }
            catch (JsonException ex)
            {
                await WriteAsync(output, id, Result.Fail($"Invalid request JSON: {ex.Message}"));
            }
            catch (Exception ex)
            {
                await WriteAsync(output, id, Result.Fail($"Sidecar error: {ex.Message}"));
            }
        }
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
