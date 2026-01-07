using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.GenerateE2K.Models;

public record GenerateE2KData
{
    [JsonPropertyName("inputFile")]
    public string InputFile { get; init; } = string.Empty;

    [JsonPropertyName("outputFile")]
    public string? OutputFile { get; init; }

    [JsonPropertyName("fileExists")]
    public bool FileExists { get; init; }

    [JsonPropertyName("fileExtension")]
    public string? FileExtension { get; init; }

    [JsonPropertyName("outputExists")]
    public bool? OutputExists { get; init; }

    [JsonPropertyName("generationSuccessful")]
    public bool? GenerationSuccessful { get; init; }

    [JsonPropertyName("fileSizeBytes")]
    public long? FileSizeBytes { get; init; }

    [JsonPropertyName("generationTimeMs")]
    public long? GenerationTimeMs { get; init; }

    [JsonPropertyName("messages")]
    public List<string> Messages { get; init; } = new();
}