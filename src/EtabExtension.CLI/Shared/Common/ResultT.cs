using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Shared.Common;

/// <summary>
/// Represents the result of an operation with a return value
/// </summary>
public record Result<T>: Result
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    private Result(bool success, T? data, string? error) : base(success, error)
    {
        Data = data;
    }

    public static Result<T> Ok(T data) => new(true, data, null);
    public new static Result<T> Fail(string error) => new(false, default, error);
}