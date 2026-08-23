// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using System.Text.Json.Serialization;

namespace EtabExtension.CLI.Features.RunAnalysis.Models;

public record RunAnalysisData
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("casesRequested")]
    public List<string>? CasesRequested { get; init; }

    /// <summary>How many load cases the model defines, in total.</summary>
    [JsonPropertyName("caseCount")]
    public int CaseCount { get; init; }

    /// <summary>
    /// Cases at status "finished" anywhere in the MODEL — including results left over
    /// from earlier runs.
    ///
    /// <para>This is a fact about the model, not a verdict on this run. Success is
    /// decided by <see cref="CasesNotFinished"/> over <see cref="CasesRun"/>; a run whose
    /// own cases failed is a failure no matter how high this number is (CLI #31).</para>
    /// </summary>
    [JsonPropertyName("finishedCaseCount")]
    public int FinishedCaseCount { get; init; }

    /// <summary>
    /// The cases this run was actually configured to produce, read back from the model
    /// after the selection was established. For a default run this is every load case the
    /// model defines, because a default run restores the all-cases selection rather than
    /// inheriting whatever the previous request left behind.
    /// </summary>
    [JsonPropertyName("casesRun")]
    public IReadOnlyList<string> CasesRun { get; init; } = [];

    /// <summary>How many members of <see cref="CasesRun"/> finished on this run.</summary>
    [JsonPropertyName("casesRunFinishedCount")]
    public int CasesRunFinishedCount { get; init; }

    /// <summary>
    /// Members of <see cref="CasesRun"/> that did not finish, each with the status the
    /// model reported. Empty on success.
    /// </summary>
    [JsonPropertyName("casesNotFinished")]
    public IReadOnlyList<string> CasesNotFinished { get; init; } = [];

    /// <summary>
    /// Requested case names the model's own load-case census does not contain, so they
    /// were never selected. Never inferred from a CSI call that failed.
    /// </summary>
    [JsonPropertyName("casesNotInModel")]
    public IReadOnlyList<string> CasesNotInModel { get; init; } = [];

    [JsonPropertyName("analysisTimeMs")]
    public long AnalysisTimeMs { get; init; }

    /// <summary>
    /// Units that were active when analysis ran.
    ///
    /// NOTE: We do NOT call SaveFile() after analysis — ETABS writes results
    /// directly into sidecar files (.Y*, .K_*, .msh) during the run.
    /// Calling SaveFile() would delete those sidecar files.
    ///
    /// The .EDB unit system is whatever the model was saved with originally.
    /// Downstream extract-results commands normalise to kip/ft regardless.
    /// </summary>
    [JsonPropertyName("units")]
    public UnitInfo? Units { get; init; }
}
