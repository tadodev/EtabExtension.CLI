using System.Text.Json;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class ServeLoopTests
{
    private sealed record Echo(string Value);

    private sealed class FakeDispatcher : IServeDispatcher
    {
        public IReadOnlyCollection<string> Capabilities { get; } =
            ["boom", "get-status", "open-model"];
        public List<string> Commands { get; } = [];

        public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            Commands.Add(command);
            object result = command == "boom"
                ? Result.Fail<Echo>("kaboom")
                : Result.Ok(new Echo(command));
            return Task.FromResult(result);
        }
    }

    private static async Task<List<JsonElement>> RunAsync(string input, FakeDispatcher dispatcher)
    {
        using var reader = new StringReader(input);
        await using var writer = new StringWriter();
        await CreateLoop(dispatcher).RunAsync(reader, writer);
        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(element => element.TryGetProperty("id", out _))
            .ToList();
    }

    private static ServeLoop CreateLoop(IServeDispatcher dispatcher) =>
        new(
            dispatcher,
            ShutdownCoordinator.Completed(SuccessShutdown()),
            Intents(),
            TestHandshake,
            TextWriter.Null);

    private static IManagedEtabsStartIntentScope Intents() =>
        new ManagedEtabsStartIntentScope();

    private static ServeHandshake TestHandshake(IReadOnlyList<string> capabilities) => new(
        "etab-cli-serve",
        1,
        "0.1.0",
        "0.1.0+gtest",
        Environment.ProcessId,
        Path.GetFullPath(Environment.ProcessPath!),
        capabilities);

    private sealed class SerialProbeDispatcher : IServeDispatcher
    {
        public IReadOnlyCollection<string> Capabilities { get; } = ["a", "b"];
        private int _inFlight;
        public int MaxInFlight { get; private set; }
        public async Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, current);
            await Task.Delay(10, ct);
            Interlocked.Decrement(ref _inFlight);
            return Result.Ok(new Echo(command));
        }
    }

    [Fact]
    public async Task Emits_versioned_handshake_before_responses()
    {
        using var reader = new StringReader("{\"id\":1,\"command\":\"get-status\"}\n");
        await using var writer = new StringWriter();
        await CreateLoop(new FakeDispatcher()).RunAsync(
            reader, writer, TestContext.Current.CancellationToken);
        var first = JsonSerializer.Deserialize<JsonElement>(
            writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);

        Assert.Equal("etab-cli-serve", first.GetProperty("protocol").GetString());
        Assert.Equal(1, first.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("ready", first.GetProperty("startup").GetString());
        Assert.Equal("0.1.0", first.GetProperty("version").GetString());
        Assert.Equal("0.1.0+gtest", first.GetProperty("buildId").GetString());
        Assert.True(first.GetProperty("pid").GetInt32() > 0);
        Assert.Equal(
            Path.GetFullPath(Environment.ProcessPath!),
            Path.GetFullPath(first.GetProperty("exePath").GetString()!));
        var capabilities = first.GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(["boom", "get-status", "open-model", "shutdown"], capabilities);
    }

    [Fact]
    public void Current_reads_metadata_from_the_protocol_assembly()
    {
        Assert.Same(
            typeof(ServeHandshake).Assembly,
            ServeHandshake.MetadataAssembly);
        var handshake = ServeHandshake.Current(["shutdown"]);

        Assert.Equal("0.1.0", handshake.Version);
        Assert.Equal("0.1.0+gtest", handshake.BuildId);
        Assert.Equal(["shutdown"], handshake.Capabilities);
    }

    [Fact]
    public void Handshake_requires_explicit_assembly_metadata()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServeHandshake.FromAssembly(
                typeof(string).Assembly,
                Environment.ProcessId,
                Path.GetFullPath(Environment.ProcessPath!),
                ["shutdown"]));

        Assert.Contains("SidecarVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Never_overlaps_dispatches()
    {
        var dispatcher = new SerialProbeDispatcher();
        using var reader = new StringReader(
            "{\"id\":1,\"command\":\"a\",\"request\":{}}\n{\"id\":2,\"command\":\"b\",\"request\":{}}\n");
        await using var writer = new StringWriter();
        await CreateLoop(dispatcher).RunAsync(reader, writer, TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatcher.MaxInFlight);
    }

    private static readonly string[] TwoExpectedCommands = ["get-status", "open-model"];

    [Fact]
    public async Task Dispatches_each_request_serially_and_correlates_the_id()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync(
            "{\"id\":1,\"command\":\"get-status\"}\n{\"id\":2,\"command\":\"open-model\",\"request\":{}}\n",
            dispatcher);

        Assert.Equal(TwoExpectedCommands, dispatcher.Commands);
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt64());
        Assert.True(responses[0].GetProperty("success").GetBoolean());
        Assert.Equal("get-status", responses[0].GetProperty("data").GetProperty("value").GetString());
        Assert.Equal(2, responses[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Failure_result_is_surfaced_with_id_and_omits_null_data()
    {
        var responses = await RunAsync("{\"id\":7,\"command\":\"boom\"}\n", new FakeDispatcher());

        Assert.Single(responses);
        Assert.Equal(7, responses[0].GetProperty("id").GetInt64());
        Assert.False(responses[0].GetProperty("success").GetBoolean());
        Assert.Equal("kaboom", responses[0].GetProperty("error").GetString());
        Assert.False(responses[0].TryGetProperty("data", out _));
    }

    private static readonly string[] OneExpectedCommand = ["get-status"];

    [Fact]
    public async Task Malformed_line_gets_an_error_but_the_loop_keeps_serving()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync("not json\n{\"id\":3,\"command\":\"get-status\"}\n", dispatcher);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("success").GetBoolean());
        Assert.True(responses[1].GetProperty("success").GetBoolean());
        Assert.Equal(OneExpectedCommand, dispatcher.Commands);
    }

    [Fact]
    public async Task Shutdown_command_stops_the_loop_without_dispatching()
    {
        var dispatcher = new FakeDispatcher();
        var responses = await RunAsync(
            "{\"id\":1,\"command\":\"shutdown\"}\n{\"id\":2,\"command\":\"get-status\"}\n",
            dispatcher);

        Assert.Single(responses);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt64());
        Assert.True(responses[0].GetProperty("success").GetBoolean());
        Assert.Empty(dispatcher.Commands);
    }

    [Fact]
    public async Task ShutdownWaitsForCleanupThenWritesOneSuccessWithTerminalDataAndStopsReading()
    {
        var dispatcher = new FakeDispatcher();
        var coordinator = new ShutdownCoordinator();
        using var reader = new CountingReader(
            "{\"id\":41,\"command\":\"shutdown\"}",
            "{\"id\":42,\"command\":\"get-status\"}");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(dispatcher, coordinator, Intents(), TestHandshake, TextWriter.Null);

        var running = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);
        await coordinator.Called;

        Assert.Empty(ResponseLines(writer));
        Assert.False(running.IsCompleted);
        Assert.Equal(1, reader.ReadCount);

        coordinator.Complete(SuccessShutdown(forced: true));
        await running;

        var response = Assert.Single(ResponseLines(writer));
        Assert.Equal(41, response.GetProperty("id").GetInt64());
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.True(data.GetProperty("processExitConfirmed").GetBoolean());
        Assert.True(data.GetProperty("forced").GetBoolean());
        Assert.Equal("succeeded", data.GetProperty("state").GetString());
        Assert.Equal(2, coordinator.CallCount);
        Assert.Equal(1, coordinator.UnderlyingStartCount);
        Assert.Equal(1, reader.ReadCount);
        Assert.Empty(dispatcher.Commands);
    }

    [Fact]
    public async Task ShutdownWaitsForCleanupThenWritesTypedFailureWithPopulatedData()
    {
        var coordinator = new ShutdownCoordinator();
        using var reader = new CountingReader(
            "{\"id\":51,\"command\":\"shutdown\"}",
            "{\"id\":52,\"command\":\"get-status\"}");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null);
        var failure = ShutdownFailure();

        var running = loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);
        await coordinator.Called;
        Assert.Empty(ResponseLines(writer));

        coordinator.Complete(failure);
        await running;

        var response = Assert.Single(ResponseLines(writer));
        Assert.Equal(51, response.GetProperty("id").GetInt64());
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            response.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        var data = response.GetProperty("data");
        Assert.False(data.GetProperty("processExitConfirmed").GetBoolean());
        Assert.True(data.GetProperty("recordRetained").GetBoolean());
        Assert.Equal("processExitUnconfirmed", data.GetProperty("state").GetString());
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task StdinEofConvergesOnShutdownCoordinator()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var reader = new StringReader(string.Empty);
        await using var writer = new StringWriter();

        await new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null).RunAsync(reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(1, coordinator.CallCount);
        Assert.Equal(1, coordinator.UnderlyingStartCount);
    }

    [Fact]
    public async Task CancellationCleansUpThenPreservesOriginalCancellation()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var reader = new BlockingReader();
        await using var writer = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var running = new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null).RunAsync(reader, writer, cancellation.Token);
        await reader.ReadStarted;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, coordinator.CallCount);
    }

    [Fact]
    public async Task FatalReaderErrorIsPreservedAfterCleanupFailureIsBoundedAndLogged()
    {
        var coordinator = ShutdownCoordinator.Completed(ShutdownFailure());
        using var reader = new FatalReader(new IOException("reader failed"));
        await using var writer = new StringWriter();
        await using var diagnostics = new StringWriter();
        var loop = new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            diagnostics);

        var error = await Assert.ThrowsAsync<IOException>(
            () => loop.RunAsync(reader, writer, TestContext.Current.CancellationToken));

        Assert.Equal("reader failed", error.Message);
        Assert.Contains(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.All(
            diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= 2048));
        Assert.Equal(1, coordinator.CallCount);
    }

    [Fact]
    public async Task FatalReaderErrorIsPreservedWhenCleanupItselfThrows()
    {
        var coordinator = new ThrowingCoordinator(
            new InvalidOperationException("cleanup exploded\r\n"));
        using var reader = new FatalReader(new IOException("reader failed"));
        await using var writer = new StringWriter();
        await using var diagnostics = new StringWriter();
        var loop = new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            diagnostics);

        var error = await Assert.ThrowsAsync<IOException>(
            () => loop.RunAsync(reader, writer, TestContext.Current.CancellationToken));

        Assert.Equal("reader failed", error.Message);
        Assert.Contains(
            "operation=IServeShutdownCoordinator.ShutdownAsync",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        var diagnostic = diagnostics.ToString().TrimEnd('\r', '\n');
        Assert.DoesNotContain(diagnostic, char.IsControl);
        Assert.True(diagnostic.Length <= 2048);
        Assert.Equal(1, coordinator.CallCount);
    }

    // A failing handler must cost the caller one request, not the whole managed
    // ETABS session: the offending id gets a bounded failure and the loop serves on.
    [Theory]
    [InlineData("{\"id\":61,\"command\":\"needs-payload\"}", "Missing 'request' payload")]
    [InlineData("{\"id\":61,\"command\":\"typed\",\"request\":{\"count\":\"twelve\"}}", "JsonException")]
    [InlineData("{\"id\":61,\"command\":\"explode\",\"request\":{}}", "dispatch failed")]
    public async Task HandlerFailureIsIsolatedToOneCorrelatedResponseAndTheLoopKeepsServing(
        string badRequest,
        string expectedFragment)
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        var dispatcher = new IsolationDispatcher();
        using var reader = new StringReader(
            $"{badRequest}\n{{\"id\":62,\"command\":\"get-status\"}}\n");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(dispatcher, coordinator, Intents(), TestHandshake, TextWriter.Null);

        await loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);

        var responses = ResponseLines(writer);
        Assert.Equal(2, responses.Count);

        var failure = responses[0];
        Assert.Equal(61, failure.GetProperty("id").GetInt64());
        Assert.False(failure.GetProperty("success").GetBoolean());
        var error = failure.GetProperty("error").GetString()!;
        Assert.Contains(EtabsApiErrorCodes.InfrastructureOperationFailed, error, StringComparison.Ordinal);
        Assert.Contains("operation=IServeDispatcher.DispatchAsync", error, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
        Assert.True(error.Length <= 2048);
        Assert.DoesNotContain(error, char.IsControl);

        Assert.Equal(62, responses[1].GetProperty("id").GetInt64());
        Assert.True(responses[1].GetProperty("success").GetBoolean());
        Assert.Equal(1, coordinator.CallCount);
    }

    // The live proof surfaced ETABS_MODEL_INITIALIZATION_FAILED only inside a generic
    // infrastructure envelope. A typed managed-session failure must keep its own code
    // as the leading token so a consumer can branch on it.
    [Fact]
    public async Task TypedLaunchFailureKeepsItsCodeInsteadOfTheGenericEnvelope()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var reader = new StringReader(
            "{\"id\":66,\"command\":\"launch-failure\",\"request\":{}}\n{\"id\":67,\"command\":\"get-status\"}\n");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(
            new IsolationDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null);

        await loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);

        var responses = ResponseLines(writer);
        Assert.Equal(2, responses.Count);
        var error = responses[0].GetProperty("error").GetString()!;
        Assert.StartsWith(
            $"[{EtabsLaunchErrorCodes.ModelInitializationFailed}]",
            error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            EtabsApiErrorCodes.InfrastructureOperationFailed,
            error,
            StringComparison.Ordinal);
        Assert.Contains("cSapModel.InitializeNewModel", error, StringComparison.Ordinal);
        Assert.EndsWith("command=launch-failure", error, StringComparison.Ordinal);
        Assert.True(error.Length <= 2048);

        // Typed or not, the failure is still contained to its own request.
        Assert.True(responses[1].GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task UnboundedHandlerFailureTextIsCappedInTheCorrelatedResponse()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var reader = new StringReader("{\"id\":63,\"command\":\"flood\",\"request\":{}}\n");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(
            new IsolationDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null);

        await loop.RunAsync(reader, writer, TestContext.Current.CancellationToken);

        var error = Assert.Single(ResponseLines(writer)).GetProperty("error").GetString()!;
        Assert.True(error.Length <= 2048);
        Assert.Contains("…", error, StringComparison.Ordinal);
        Assert.EndsWith("command=flood", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringDispatchTerminatesThroughTheShutdownCoordinatorOnce()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new IsolationDispatcher(cancellation);
        using var reader = new StringReader(
            "{\"id\":64,\"command\":\"cancel\",\"request\":{}}\n{\"id\":65,\"command\":\"get-status\"}\n");
        await using var writer = new StringWriter();
        var loop = new ServeLoop(dispatcher, coordinator, Intents(), TestHandshake, TextWriter.Null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loop.RunAsync(reader, writer, cancellation.Token));

        Assert.Empty(ResponseLines(writer));
        Assert.Equal(["cancel"], dispatcher.Commands);
        Assert.Equal(1, coordinator.CallCount);
        Assert.Equal(1, coordinator.UnderlyingStartCount);
    }

    [Fact]
    public async Task FatalHandshakeWriterErrorIsPreservedAfterCleanup()
    {
        var coordinator = ShutdownCoordinator.Completed(SuccessShutdown());
        using var reader = new StringReader(string.Empty);
        await using var writer = new FatalWriter(new IOException("handshake failed"));
        var loop = new ServeLoop(
            new FakeDispatcher(),
            coordinator,
            Intents(),
            TestHandshake,
            TextWriter.Null);

        var error = await Assert.ThrowsAsync<IOException>(
            () => loop.RunAsync(reader, writer, TestContext.Current.CancellationToken));

        Assert.Equal("handshake failed", error.Message);
        Assert.Equal(1, coordinator.CallCount);
    }

    private static List<JsonElement> ResponseLines(StringWriter writer) =>
        writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(element => element.TryGetProperty("id", out _))
            .ToList();

    private static Result<ManagedEtabsShutdownData> SuccessShutdown(bool forced = false) =>
        Result.Ok(new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: 42));

    private static Result<ManagedEtabsShutdownData> ShutdownFailure() => new(
        false,
        new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: true,
            RecordRetained: true,
            ApplicationExitReturnCode: 7,
            OwnedPid: 42),
        $"{ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed}: cleanup failed");

    private sealed class ShutdownCoordinator : IServeShutdownCoordinator
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<Result<ManagedEtabsShutdownData>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<Result<ManagedEtabsShutdownData>>? _shutdown;
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public int UnderlyingStartCount { get; private set; }
        public Task Called => _called.Task;

        public static ShutdownCoordinator Completed(Result<ManagedEtabsShutdownData> result)
        {
            var coordinator = new ShutdownCoordinator();
            coordinator.Complete(result);
            return coordinator;
        }

        public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
        {
            lock (_gate)
            {
                CallCount++;
                _called.TrySetResult();
                if (_shutdown is null)
                {
                    UnderlyingStartCount++;
                    _shutdown = _completion.Task;
                }
                return _shutdown;
            }
        }

        public void Complete(Result<ManagedEtabsShutdownData> result) =>
            _completion.TrySetResult(result);

        public async ValueTask DisposeAsync() =>
            _ = await ShutdownAsync();
    }

    private sealed class ThrowingCoordinator(Exception exception) : IServeShutdownCoordinator
    {
        public int CallCount { get; private set; }

        public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
        {
            CallCount++;
            return Task.FromException<Result<ManagedEtabsShutdownData>>(exception);
        }

        public async ValueTask DisposeAsync() =>
            _ = await ShutdownAsync();
    }

    private sealed class CountingReader(params string[] lines) : TextReader
    {
        private readonly Queue<string> _lines = new(lines);
        public int ReadCount { get; private set; }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<string?>(
                _lines.Count == 0 ? null : _lines.Dequeue());
        }
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReadStarted => _started.Task;

        public override async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class FatalReader(Exception exception) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<string?>(exception);
    }

    private sealed class FatalWriter(Exception exception) : StringWriter
    {
        public override Task WriteLineAsync(string? value) => Task.FromException(exception);
    }

    private sealed record TypedPayload(int Count);

    /// <summary>
    /// Reproduces the ways <see cref="ServeDispatcher"/> can throw out of a handler:
    /// a missing payload (<c>RequirePayload</c>), a payload whose field types do not
    /// match (<c>Deserialize&lt;T&gt;</c>), an unexpected handler escape, and a
    /// cancellation raised by the serve token.
    /// </summary>
    private sealed class IsolationDispatcher(CancellationTokenSource? cancellation = null)
        : IServeDispatcher
    {
        public IReadOnlyCollection<string> Capabilities { get; } =
            ["cancel", "explode", "flood", "get-status", "launch-failure", "needs-payload", "typed"];
        public List<string> Commands { get; } = [];

        public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct)
        {
            Commands.Add(command);
            switch (command)
            {
                case "needs-payload":
                    _ = request ?? throw new InvalidOperationException(
                        "Missing 'request' payload for this command");
                    break;
                case "typed":
                    _ = request!.Value.Deserialize<TypedPayload>(ServeJson.Options);
                    break;
                case "explode":
                    throw new InvalidOperationException("dispatch failed");
                case "launch-failure":
                    throw new EtabsLaunchException(
                        EtabsLaunchErrorCodes.ModelInitializationFailed,
                        EtabsApiDiagnosticFormatter.ApiReturn("cSapModel.InitializeNewModel", 7));
                case "flood":
                    throw new InvalidOperationException(new string('x', 5_000));
                case "cancel":
                    cancellation!.Cancel();
                    ct.ThrowIfCancellationRequested();
                    break;
                default:
                    break;
            }

            return Task.FromResult<object>(Result.Ok(new Echo(command)));
        }
    }
}
