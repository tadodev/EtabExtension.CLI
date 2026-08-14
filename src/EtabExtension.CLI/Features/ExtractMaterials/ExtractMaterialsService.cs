// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.ExtractMaterials.Models;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using System.Diagnostics;

namespace EtabExtension.CLI.Features.ExtractMaterials;

/// <summary>
/// Extracts one ETABS database table to a .parquet file via a hidden ETABS instance (Mode B).
///
/// ALIGNED WITH ExtractResultsService:
///   • Takes an ExtractMaterialsRequest (not loose parameters).
///   • Output dir, not output file — filename is {tableSlug}.parquet derived from the table key.
///   • Unit preset via request.Units — not hardcoded.
///   • LoadCases / LoadCombos intentionally null — material/geometry tables have no load dependency.
///   • 0-row result is Result.Ok (not an error), OutputFile is null in that case.
///   • Progress to Console.Error; JSON result to stdout via ExitWithResult().
/// </summary>
public class ExtractMaterialsService : IExtractMaterialsService
{
    private const string DefaultMaterialTableKey = "Material List by Story";
    private const string DefaultMaterialTableSlug = "material_list_by_story";

    private readonly IParquetService _parquet;
    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly IExtractResultsService _extractResultsService;

    public ExtractMaterialsService(
        IParquetService parquet,
        IEtabsTableServicesFactory tableFactory,
        IExtractResultsService extractResultsService)
    {
        _parquet = parquet;
        _tableFactory = tableFactory;
        _extractResultsService = extractResultsService;
    }

    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsAsync(
        ExtractMaterialsRequest request)
    {
        var tableKey = string.IsNullOrWhiteSpace(request.TableKey)
            ? DefaultMaterialTableKey
            : request.TableKey;

        if (string.Equals(tableKey, DefaultMaterialTableKey, StringComparison.OrdinalIgnoreCase))
            return await ExtractViaCombinedResultsPathAsync(request, tableKey);

        return await ExtractViaLegacyPathAsync(request, tableKey);
    }

    public async Task<Result<ExtractMaterialsData>> ExtractMaterialsOnAppAsync(
        ETABSApplication app, ExtractMaterialsRequest request)
    {
        var tableKey = string.IsNullOrWhiteSpace(request.TableKey) ? DefaultMaterialTableKey : request.TableKey;
        if (string.Equals(tableKey, DefaultMaterialTableKey, StringComparison.OrdinalIgnoreCase))
        {
            var combined = new ExtractResultsRequest
            {
                FilePath = request.FilePath,
                OutputDir = request.OutputDir,
                Units = request.Units,
                Tables = new TableSelections { MaterialListByStory = new TableFilter { FieldKeys = request.FieldKeys } }
            };
            var result = await _extractResultsService.ExtractOnAppAsync(app, combined);
            if (!result.Success || result.Data is null) return Result.Fail<ExtractMaterialsData>(result.Error ?? $"Failed to load table '{tableKey}'.");
            if (!result.Data.Tables.TryGetValue(DefaultMaterialTableSlug, out var outcome) || !outcome.Success)
                return Result.Fail<ExtractMaterialsData>(outcome?.Error ?? $"Combined extraction did not return '{DefaultMaterialTableSlug}'.");
            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = request.FilePath, OutputFile = outcome.OutputFile, TableKey = tableKey,
                RowCount = outcome.RowCount, DiscardedRowCount = outcome.DiscardedRowCount,
                Columns = outcome.Columns, Units = result.Data.Units, ExtractionTimeMs = outcome.ExtractionTimeMs
            });
        }

        if (!File.Exists(request.FilePath)) return Result.Fail<ExtractMaterialsData>($"File not found: {request.FilePath}");
        if (string.IsNullOrWhiteSpace(request.OutputDir)) return Result.Fail<ExtractMaterialsData>("OutputDir cannot be empty");
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null) return Result.Fail<ExtractMaterialsData>(unitsError);
        Directory.CreateDirectory(request.OutputDir);
        var outputFile = Path.Combine(request.OutputDir, $"{ToSlug(tableKey)}.parquet");
        var sw = Stopwatch.StartNew();
        try
        {
            var openRet = app.Model.Files.OpenFile(request.FilePath);
            if (openRet != 0) return Result.Fail<ExtractMaterialsData>($"OpenFile failed (ret={openRet})");
            var units = await new EtabsUnitService(app).ReadAndNormaliseAsync(targetUnits!);
            var query = await _tableFactory.CreateQueryService(app).QueryAsync(
                new TableQueryRequest(tableKey) { FieldKeys = request.FieldKeys });
            if (!query.IsSuccess) return Result.Fail<ExtractMaterialsData>($"Failed to load table '{tableKey}': {query.ErrorMessage}");
            if (query.FieldKeys.Count == 0) return Result.Fail<ExtractMaterialsData>($"Table '{tableKey}' returned no fields — check that the table key is correct.");
            if (query.RowCount == 0) return Result.Ok(new ExtractMaterialsData { FilePath = request.FilePath, TableKey = tableKey, Units = units.Active, DiscardedRowCount = query.DiscardedRowCount, ExtractionTimeMs = sw.ElapsedMilliseconds });
            var flat = query.Rows.SelectMany(row => query.FieldKeys.Select(f => row.TryGetValue(f, out var value) ? value : string.Empty)).ToList();
            var write = await _parquet.WriteAsync(outputFile, query.FieldKeys, flat);
            if (!write.Success) return Result.Fail<ExtractMaterialsData>($"Parquet write failed: {write.Error}");
            sw.Stop();
            return Result.Ok(new ExtractMaterialsData { FilePath = request.FilePath, OutputFile = outputFile, TableKey = tableKey, RowCount = write.RowCount, DiscardedRowCount = query.DiscardedRowCount, Columns = write.Columns, Units = units.Active, ExtractionTimeMs = sw.ElapsedMilliseconds });
        }
        catch (Exception ex) { return Result.Fail<ExtractMaterialsData>($"Fatal error: {ex.Message}"); }
    }

    private async Task<Result<ExtractMaterialsData>> ExtractViaCombinedResultsPathAsync(
        ExtractMaterialsRequest request,
        string tableKey)
    {
        var combinedRequest = new ExtractResultsRequest
        {
            FilePath = request.FilePath,
            OutputDir = request.OutputDir,
            Units = request.Units,
            Tables = new TableSelections
            {
                MaterialListByStory = new TableFilter
                {
                    FieldKeys = request.FieldKeys,
                }
            }
        };

        var result = await _extractResultsService.ExtractAsync(combinedRequest);
        if (!result.Success || result.Data is null)
            return Result.Fail<ExtractMaterialsData>(result.Error ?? $"Failed to load table '{tableKey}'.");

        if (!result.Data.Tables.TryGetValue(DefaultMaterialTableSlug, out var outcome))
            return Result.Fail<ExtractMaterialsData>(
                $"Combined extraction did not return '{DefaultMaterialTableSlug}'.");

        if (!outcome.Success)
            return Result.Fail<ExtractMaterialsData>(
                $"Failed to load table '{tableKey}': {outcome.Error}");

        return Result.Ok(new ExtractMaterialsData
        {
            FilePath = request.FilePath,
            OutputFile = outcome.OutputFile,
            TableKey = tableKey,
            RowCount = outcome.RowCount,
            DiscardedRowCount = outcome.DiscardedRowCount,
            Columns = outcome.Columns,
            Units = result.Data.Units,
            ExtractionTimeMs = outcome.ExtractionTimeMs
        });
    }

    private async Task<Result<ExtractMaterialsData>> ExtractViaLegacyPathAsync(
        ExtractMaterialsRequest request,
        string tableKey)
    {
        // ── Pre-flight ────────────────────────────────────────────────────────
        if (!File.Exists(request.FilePath))
            return Result.Fail<ExtractMaterialsData>($"File not found: {request.FilePath}");

        var pathError = PathSafe.GetErrorIfInvalidPath(request.OutputDir, "OutputDir");
        if (pathError is not null)
            return Result.Fail<ExtractMaterialsData>(pathError);

        // ── Resolve units (fail fast before starting ETABS) ───────────────────
        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
            return Result.Fail<ExtractMaterialsData>(unitsError);

        var tableSlug = PathSafe.ToSafeSlug(tableKey);
        var outputFile = Path.Combine(request.OutputDir, $"{tableSlug}.parquet");

        Directory.CreateDirectory(request.OutputDir);

        Console.Error.WriteLine(
            $"ℹ extract-materials: '{tableKey}' → {outputFile}");

        ETABSApplication? app = null;
        var sw = Stopwatch.StartNew();

        try
        {
            // ── Start ETABS ───────────────────────────────────────────────────
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<ExtractMaterialsData>("Failed to start ETABS hidden instance.");

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            // ── Open file ─────────────────────────────────────────────────────
            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(request.FilePath)}");
            int openRet = app.Model.Files.OpenFile(request.FilePath);
            if (openRet != 0)
                return Result.Fail<ExtractMaterialsData>($"OpenFile failed (ret={openRet})");

            // ── Normalise units ───────────────────────────────────────────────
            var unitService = new EtabsUnitService(app);
            var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
            Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));

            // ── Fetch table ───────────────────────────────────────────────────
            // Material / geometry tables have no load case or combo dependency.
            // LoadCases and LoadCombos are null → EtabsTableQueryService does NOT
            // call SetLoadCasesSelectedForDisplay / SetLoadCombinationsSelectedForDisplay.
            Console.Error.WriteLine($"ℹ Fetching table: '{tableKey}'");

            var queryService = _tableFactory.CreateQueryService(app);
            var queryResult = await queryService.QueryAsync(
                new TableQueryRequest(tableKey)
                {
                    FieldKeys = request.FieldKeys,
                    // LoadCases / LoadCombos intentionally omitted (null)
                });

            if (!queryResult.IsSuccess)
                return Result.Fail<ExtractMaterialsData>(
                    $"Failed to load table '{tableKey}': {queryResult.ErrorMessage}");

            if (queryResult.FieldKeys.Count == 0)
                return Result.Fail<ExtractMaterialsData>(
                    $"Table '{tableKey}' returned no fields — check that the table key is correct.");

            Console.Error.WriteLine(
                $"ℹ Table: {queryResult.RowCount} rows × {queryResult.FieldKeys.Count} cols" +
                $" [{string.Join(", ", queryResult.FieldKeys)}]");

            // Preview first 3 rows to help with debugging
            foreach (var (row, idx) in queryResult.Rows.Take(3).Select((r, i) => (r, i)))
                Console.Error.WriteLine(
                    $"  Row {idx + 1}: {string.Join(" | ", row.Select(kv => $"{kv.Key}={kv.Value}"))}");

            // ── 0-row is valid — empty model or filter returned nothing ────────
            if (queryResult.RowCount == 0)
            {
                Console.Error.WriteLine("⚠ Table returned 0 rows — parquet file not written");
                sw.Stop();
                return Result.Ok(new ExtractMaterialsData
                {
                    FilePath = request.FilePath,
                    OutputFile = null,
                    TableKey = tableKey,
                    RowCount = 0,
                    DiscardedRowCount = queryResult.DiscardedRowCount,
                    Units = unitSnapshot.Active,
                    ExtractionTimeMs = sw.ElapsedMilliseconds
                });
            }

            // ── Write parquet ─────────────────────────────────────────────────
            var flatData = queryResult.Rows
                .SelectMany(row => queryResult.FieldKeys.Select(f =>
                    row.TryGetValue(f, out var v) ? v : string.Empty))
                .ToList();

            var writeResult = await _parquet.WriteAsync(outputFile, queryResult.FieldKeys, flatData);
            if (!writeResult.Success)
                return Result.Fail<ExtractMaterialsData>($"Parquet write failed: {writeResult.Error}");

            sw.Stop();
            Console.Error.WriteLine(
                $"✓ {writeResult.RowCount} rows → {outputFile} ({sw.ElapsedMilliseconds} ms)");

            return Result.Ok(new ExtractMaterialsData
            {
                FilePath = request.FilePath,
                OutputFile = outputFile,
                TableKey = tableKey,
                RowCount = writeResult.RowCount,
                DiscardedRowCount = queryResult.DiscardedRowCount,
                Columns = writeResult.Columns,
                Units = unitSnapshot.Active,
                ExtractionTimeMs = sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<ExtractMaterialsData>($"Fatal error: {ex.Message}");
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

}
