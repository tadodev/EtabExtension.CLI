using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.Validation.Models;

public record ValidationData
{
    [JsonPropertyName("etabsInstalled")]
    public bool EtabsInstalled { get; init; }

    [JsonPropertyName("etabsVersion")]
    public string? EtabsVersion { get; init; }

    [JsonPropertyName("fileValid")]
    public bool? FileValid { get; init; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("fileExists")]
    public bool? FileExists { get; init; }

    [JsonPropertyName("fileExtension")]
    public string? FileExtension { get; init; }

    [JsonPropertyName("isAnalyzed")]
    public bool? IsAnalyzed { get; init; }

    [JsonPropertyName("validationMessages")]
    public List<string> ValidationMessages { get; init; } = new();
}