using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Common;

/// <summary>
/// Represents the result of an operation
/// </summary>
public record Result
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    protected Result(bool success, string? error)
    {
        Success = success;
        Error = error;
        Timestamp = DateTime.UtcNow;
    }

    public static Result Ok() => new(true, null);
    public static Result Fail(string error) => new(false, error);
}