using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.Serve;

/// <summary>
/// A single request line on the serve daemon's stdin:
/// <c>{"id":123,"command":"analyze-and-extract","request":{...}}</c>.
/// The <c>request</c> payload is the existing per-command request JSON, unchanged.
/// </summary>
public sealed class ServeRequest
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("command")] public string Command { get; init; } = string.Empty;
    [JsonPropertyName("request")] public JsonElement? Request { get; init; }
}

public static class ServeCapabilities
{
    public static readonly IReadOnlyList<string> All =
    [
        "analyze-and-extract",
        "cancel-operation",
        "close-model",
        "extract-materials",
        "extract-results",
        "generate-e2k",
        "get-model-state",
        "get-operation-events",
        "get-operation-status",
        "get-status",
        "inspect-wall-property",
        "list-wall-properties",
        "open-model",
        "read-model-metadata",
        "resolve-area-targets",
        "run-analysis",
        "shutdown",
        "snapshot-export",
        "start-operation",
        "unlock-model"
    ];
}

public sealed record ServeHandshake(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("exePath")] string ExePath,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities)
{
    public static ServeHandshake Current()
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("Entry assembly is unavailable");
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? throw new InvalidOperationException("Adapter version is unavailable");
        var buildId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "SidecarBuildId")?.Value
            ?? $"{version}+gdev";
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Process executable path is unavailable");

        return new(
            "etab-cli-serve",
            1,
            version,
            buildId,
            Environment.ProcessId,
            Path.GetFullPath(exePath),
            ServeCapabilities.All);
    }
}

/// <summary>
/// JSON options for the line-delimited serve protocol. Mirrors
/// <c>JsonExtensions.DefaultOptions</c> (camelCase, null-ignored, enum-as-string)
/// but <b>compact</b> (<c>WriteIndented = false</c>) so every response is exactly
/// one line — pretty-printing would break line framing.
/// </summary>
internal static class ServeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>
/// Routes one serve request to the matching feature, executed against the single
/// shared ETABS session. Returns the feature's existing <c>Result</c>/<c>Result&lt;T&gt;</c>
/// (the loop serializes it by runtime type and injects the correlation id).
/// </summary>
public interface IServeDispatcher
{
    Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct);
}
