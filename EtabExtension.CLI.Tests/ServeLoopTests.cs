using System.Text.Json;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
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
        new(dispatcher, TestHandshake);

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
}
