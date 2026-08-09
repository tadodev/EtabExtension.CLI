// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.GetStatus.Models;

public enum EtabsInstanceOwnership
{
    None,
    Managed,
    External,
    Ambiguous
}

public record GetStatusData
{
    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }

    [JsonPropertyName("pid")]
    public int? Pid { get; init; }

    [JsonPropertyName("ownership")]
    public EtabsInstanceOwnership Ownership { get; init; }

    [JsonPropertyName("observedPids")]
    public IReadOnlyList<int> ObservedPids { get; init; } = [];

    [JsonPropertyName("etabsVersion")]
    public string? EtabsVersion { get; init; }

    [JsonPropertyName("openFilePath")]
    public string? OpenFilePath { get; init; }

    [JsonPropertyName("isModelOpen")]
    public bool IsModelOpen { get; init; }

    [JsonPropertyName("isLocked")]
    public bool? IsLocked { get; init; }

    [JsonPropertyName("isAnalyzed")]
    public bool? IsAnalyzed { get; init; }

    /// <summary>
    /// Present unit system. Tells Rust what units all extracted table values are in.
    /// Null when ETABS is not running or no model is open.
    /// </summary>
    [JsonPropertyName("unitSystem")]
    public UnitSystemInfo? UnitSystem { get; init; }
}
