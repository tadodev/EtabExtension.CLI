using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Features.Serve.Operations;

public enum OperationPhase { Queued, Running, Cancelling, Succeeded, Failed, Cancelled }
public enum OperationCancellationState { NotRequested, Requested, Honored }

/// <summary>
/// Immutable request intent captured before ETABS work crosses onto the queued STA.
/// A later protocol request can neither replace the cold-start consent nor relabel the
/// visibility evidence for work that is already queued or running.
/// </summary>
public sealed record EtabsOperationContext(
    ManagedEtabsStartIntent StartIntent,
    string VisibilityStage);

public sealed record StartOperationRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("payload")] JsonElement Payload);

public sealed record OperationIdRequest(
    [property: JsonPropertyName("operationId")] string OperationId);

public sealed record GetOperationEventsRequest(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("sinceSeq")] long SinceSeq = 0);

public sealed record StartOperationData(
    [property: JsonPropertyName("operationId")] string OperationId);

public sealed record CancelOperationData(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("cancellationState")] OperationCancellationState CancellationState);

public sealed record OperationStatusData
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("phase")] public required OperationPhase Phase { get; init; }
    [JsonPropertyName("stepIndex")] public int? StepIndex { get; init; }
    [JsonPropertyName("stepTotal")] public int? StepTotal { get; init; }
    [JsonPropertyName("currentCsiOperation")] public string? CurrentCsiOperation { get; init; }
    [JsonPropertyName("operationElapsedMs")] public long OperationElapsedMs { get; init; }
    [JsonPropertyName("currentStepElapsedMs")] public long? CurrentStepElapsedMs { get; init; }
    [JsonPropertyName("lastEventSeq")] public long LastEventSeq { get; init; }
    [JsonPropertyName("cancellationState")] public OperationCancellationState CancellationState { get; init; }
    [JsonPropertyName("heartbeatTimestamp")] public DateTimeOffset HeartbeatTimestamp { get; init; }
    [JsonPropertyName("suspectedHang")] public bool SuspectedHang { get; init; }
}

public sealed record OperationEvent
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("phase")] public OperationPhase Phase { get; init; }
    [JsonPropertyName("stepIndex")] public int? StepIndex { get; init; }
    [JsonPropertyName("stepTotal")] public int? StepTotal { get; init; }
    [JsonPropertyName("csiOperation")] public string? CsiOperation { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
}

public sealed record GetOperationEventsData(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("events")] IReadOnlyList<OperationEvent> Events,
    [property: JsonPropertyName("lastSeq")] long LastSeq);
