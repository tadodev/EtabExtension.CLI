// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.RunAnalysis;

public interface IRunAnalysisService
{
    /// <summary>
    /// Runs analysis on a snapshot .edb using a hidden ETABS instance (Mode B).
    /// Results are written to sidecar files by ETABS during the run — SaveFile()
    /// is intentionally NOT called (it would delete those sidecar files).
    /// </summary>
    /// <param name="filePath">Path to the .edb file.</param>
    /// <param name="cases">
    /// Specific load case names to run. null or empty = all cases (default).
    /// </param>
    /// <param name="units">
    /// Unit preset string to normalise to before analysis.
    /// null / empty = default (US_Kip_Ft).
    /// See <see cref="EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit.EtabsUnitPreset"/> for valid values.
    /// </param>
    Task<Result<RunAnalysisData>> RunAnalysisAsync(
        string filePath,
        List<string>? cases,
        string? units = null);

    /// <summary>
    /// Runs analysis on the ETABS instance owned by the persistent serve session.
    /// The caller retains ownership of the application lifecycle.
    /// </summary>
    Task<Result<RunAnalysisData>> RunAnalysisOnAppAsync(
        ETABSApplication app,
        string filePath,
        List<string>? cases,
        string? units = null);
}
