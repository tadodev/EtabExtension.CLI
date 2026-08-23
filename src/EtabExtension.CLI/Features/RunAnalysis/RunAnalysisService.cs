// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.RunAnalysis;

public class RunAnalysisService : IRunAnalysisService
{
    public async Task<Result<RunAnalysisData>> RunAnalysisAsync(
        string filePath,
        List<string>? cases,
        string? units = null)
    {
        if (!File.Exists(filePath))
            return Result.Fail<RunAnalysisData>($"File not found: {filePath}");

        var (_, unitsError) = EtabsUnitPreset.Resolve(units);
        if (unitsError is not null)
            return Result.Fail<RunAnalysisData>(unitsError);

        ETABSApplication? app = null;
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<RunAnalysisData>("Failed to start ETABS hidden instance.");

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            return await RunAnalysisOnAppAsync(app, filePath, cases, units);
        }
        catch (Exception ex)
        {
            return Result.Fail<RunAnalysisData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    public async Task<Result<RunAnalysisData>> RunAnalysisOnAppAsync(
        ETABSApplication app,
        string filePath,
        List<string>? cases,
        string? units = null)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<RunAnalysisData>($"File not found: {filePath}");

        // Resolve units before touching the shared model — fail fast with a clear message.
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(units);
        if (unitsError is not null)
            return Result.Fail<RunAnalysisData>(unitsError);

        try
        {
            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<RunAnalysisData>($"OpenFile failed (ret={openRet})");

            // ── Unit normalisation ──────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

            // ── Selection, analysis and verdict ────────────────────────
            // Shared with analyze-and-extract so the two cannot drift apart.
            return await EtabsSessionHelpers.RunAnalysisOnOpenModelAsync(
                app, filePath, cases, unitSnapshot);
        }
        catch (Exception ex)
        {
            return Result.Fail<RunAnalysisData>($"ETABS COM error: {ex.Message}");
        }
    }
}
