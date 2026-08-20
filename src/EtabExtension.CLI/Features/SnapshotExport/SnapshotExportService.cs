using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metrics;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.System.Models;
using ETABSv1;
using System.Diagnostics;
using System.Text.Json;

namespace EtabExtension.CLI.Features.SnapshotExport;

/// <summary>
/// <c>snapshot-export</c> — the command the desktop Commit path actually calls.
///
/// <para>Stages, in order, each failing explicitly with a bounded diagnostic before
/// the next one runs: open the exact requested EDB (through the canonical
/// <see cref="IEtabsModelOpener"/>, the same boundary <c>open-model</c> uses),
/// normalize units, export a non-empty E2K, extract the snapshot tables, collect and
/// write metadata, then write run metrics.</para>
/// </summary>
public class SnapshotExportService : ISnapshotExportService
{
    private readonly IEtabsTableServicesFactory _tableFactory;
    private readonly TableExtractorRegistry _registry;
    private readonly IParquetService _parquet;
    private readonly IEtabsModelOpener _opener;

    public SnapshotExportService(
        IEtabsTableServicesFactory tableFactory,
        TableExtractorRegistry registry,
        IParquetService parquet,
        IEtabsModelOpener opener)
    {
        _tableFactory = tableFactory;
        _registry = registry;
        _parquet = parquet;
        _opener = opener;
    }

    /// <summary>The export stages, in execution order, as they appear in diagnostics.</summary>
    internal static class Stages
    {
        public const string OpenModel = "openModel";
        public const string NormaliseUnits = "normaliseUnits";
        public const string ExportE2K = "exportE2k";
        public const string ExtractTables = "extractTables";
        public const string CollectMetadata = "collectMetadata";
        public const string WriteMetrics = "writeMetrics";
    }

    // One-shot: start a hidden ETABS, export, dispose it. Unchanged behavior.
    public async Task<Result<SnapshotExportData>> SnapshotExportAsync(
        string filePath,
        string outputDir,
        SnapshotExportRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<SnapshotExportData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ snapshot-export: {filePath}");
        var metricsBuilder = new RunMetricsBuilder("snapshot-export", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        ETABSApplication? app = null;
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = metricsBuilder.Measure("startEtabs", () => ETABSWrapper.CreateNew());
            if (app is null)
            {
                return Result.Fail<SnapshotExportData>("Failed to start ETABS hidden instance.");
            }

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            return await ExecuteAsync(app, filePath, outputDir, prep, metricsBuilder, totalSw);
        }
        catch (Exception ex)
        {
            return Result.Fail<SnapshotExportData>(
                EtabsApiDiagnosticFormatter.Exception("snapshot-export.startEtabs", ex));
        }
        finally
        {
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    // Daemon: run against the shared serve-session ETABS (no create/dispose).
    public async Task<Result<SnapshotExportData>> SnapshotExportOnAppAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        SnapshotExportRequest request)
    {
        var prep = Prepare(filePath, outputDir, request);
        if (prep.Error is not null)
        {
            return Result.Fail<SnapshotExportData>(prep.Error);
        }

        Console.Error.WriteLine($"ℹ snapshot-export (shared session): {filePath}");
        var metricsBuilder = new RunMetricsBuilder("snapshot-export", filePath, outputDir);
        var totalSw = Stopwatch.StartNew();
        return await ExecuteAsync(app, filePath, outputDir, prep, metricsBuilder, totalSw);
    }

    internal readonly record struct Preparation(
        string? Error,
        TableSelections? Tables,
        Units? TargetUnits,
        string E2kFile,
        string MaterialsDir,
        string MetadataPath,
        string MetricsPath);

    /// <summary>
    /// Resolves everything the export needs before a single COM call is made: the
    /// model path, the unit preset, the table selection, and the four artifact paths
    /// — all of which are built under the caller's requested output directory.
    /// </summary>
    internal static Preparation Prepare(string filePath, string outputDir, SnapshotExportRequest request)
    {
        var pathError = EtabsModelOpen.ValidateModelPath(filePath);
        if (pathError is not null)
        {
            return new Preparation(pathError, null, default, "", "", "", "");
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return new Preparation("OutputDir cannot be empty", null, default, "", "", "", "");
        }

        var (targetUnits, unitsError) = EtabsUnitPreset.Resolve(request.Units);
        if (unitsError is not null)
        {
            return new Preparation(unitsError, null, default, "", "", "", "");
        }

        var tables = ExtractionProfiles.Resolve(
            request.Tables,
            request.ExtractionProfile,
            ExtractionProfiles.Snapshot);

        Directory.CreateDirectory(outputDir);
        var e2kFile = Path.Combine(outputDir, SafeFileName(request.E2KFileName, "model.e2k"));
        var materialsDir = Path.Combine(outputDir, SafeFileName(request.MaterialsDirName, "materials"));
        var metadataPath = Path.Combine(outputDir, SafeFileName(request.MetadataFileName, "model-metadata.json"));
        var metricsPath = Path.Combine(outputDir, SafeFileName(request.MetricsFileName, "run-metrics.json"));
        Directory.CreateDirectory(materialsDir);

        return new Preparation(null, tables, targetUnits, e2kFile, materialsDir, metadataPath, metricsPath);
    }

    private async Task<Result<SnapshotExportData>> ExecuteAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        Preparation prep,
        RunMetricsBuilder metricsBuilder,
        Stopwatch totalSw)
    {
        var stage = Stages.OpenModel;
        try
        {
            return await RunStagesAsync(
                app, filePath, outputDir, prep, metricsBuilder, totalSw,
                current => stage = current);
        }
        catch (Exception ex)
        {
            // Anything the stages did not already turn into an explicit failure is
            // still attributed to the stage that was running when it escaped.
            return Result.Fail<SnapshotExportData>(
                EtabsApiDiagnosticFormatter.Exception($"snapshot-export.{stage}", ex));
        }
    }

    private async Task<Result<SnapshotExportData>> RunStagesAsync(
        ETABSApplication app,
        string filePath,
        string outputDir,
        Preparation prep,
        RunMetricsBuilder metricsBuilder,
        Stopwatch totalSw,
        Action<string> enterStage)
    {
        enterStage(Stages.OpenModel);
        var openResult = await metricsBuilder.MeasureAsync(
            Stages.OpenModel,
            () => Task.FromResult(_opener.Open(app, filePath, save: false)));
        if (!openResult.Success)
        {
            return Result.Fail<SnapshotExportData>(openResult.Error ?? "OpenFile failed");
        }

        enterStage(Stages.NormaliseUnits);
        var unitSnapshot = await metricsBuilder.MeasureAsync(
            Stages.NormaliseUnits,
            () => EtabsSessionHelpers.NormaliseUnitsAsync(app, prep.TargetUnits!));

        enterStage(Stages.ExportE2K);
        Console.Error.WriteLine("ℹ Exporting to .e2k...");
        var exportRet = metricsBuilder.Measure(
            Stages.ExportE2K,
            () => app.Model.Files.ExportFile(prep.E2kFile, eFileTypeIO.TextFile));
        var exported = ValidateExportedE2K(exportRet, prep.E2kFile);
        if (!exported.Success)
        {
            return Result.Fail<SnapshotExportData>(exported.Error!);
        }

        var e2kSize = exported.Data;
        Console.Error.WriteLine($"✓ Exported ({e2kSize / 1024.0:F1} KB)");

        enterStage(Stages.ExtractTables);
        var isAnalyzed = app.Model.Analyze.GetCaseStatus().Any(cs => cs.IsFinished);
        var isLocked = app.Model.ModelInfo.IsLocked();
        var outcomes = await metricsBuilder.MeasureAsync(
            Stages.ExtractTables,
            () => EtabsSessionHelpers.ExtractTablesOnOpenModelAsync(
                app,
                prep.Tables!,
                prep.MaterialsDir,
                isAnalyzed,
                isLocked,
                _tableFactory,
                _registry,
                _parquet));

        // Metadata is part of the Commit contract, not a nicety: a success response
        // promises a metadata path, so a collection or write failure fails the stage.
        enterStage(Stages.CollectMetadata);
        var metadata = await metricsBuilder.MeasureAsync(
            Stages.CollectMetadata,
            () => EtabsSessionHelpers.CollectModelMetadataAsync(app, filePath, unitSnapshot));

        Console.Error.WriteLine("ℹ Writing model-metadata.json");
        var metadataJson = JsonSerializer.Serialize(
            metadata,
            AnalyzeAndExtractService.MetadataJsonOptions);
        await metricsBuilder.MeasureAsync(
            "writeMetadata",
            () => File.WriteAllTextAsync(prep.MetadataPath, metadataJson));
        var writtenMetadataPath = prep.MetadataPath;

        enterStage(Stages.WriteMetrics);
        totalSw.Stop();
        var metrics = metricsBuilder.Build(totalSw.ElapsedMilliseconds);
        Console.Error.WriteLine("ℹ Writing run-metrics.json");
        var metricsJson = JsonSerializer.Serialize(metrics, AnalyzeAndExtractService.MetadataJsonOptions);
        await File.WriteAllTextAsync(prep.MetricsPath, metricsJson);

        var succeeded = outcomes.Values.Count(o => o.Success);
        var failed = outcomes.Values.Count(o => !o.Success);
        var totalRows = outcomes.Values.Sum(o => o.RowCount);

        Console.Error.WriteLine(
            $"✓ Done: E2K + {succeeded}/{outcomes.Count} tables, {totalRows} rows ({totalSw.ElapsedMilliseconds} ms)");

        return Result.Ok(new SnapshotExportData
        {
            FilePath = filePath,
            OutputDir = outputDir,
            E2KFile = prep.E2kFile,
            E2KSizeBytes = e2kSize,
            MaterialsDir = prep.MaterialsDir,
            Tables = outcomes,
            TotalRowCount = totalRows,
            SucceededCount = succeeded,
            FailedCount = failed,
            Metadata = metadata,
            MetadataPath = writtenMetadataPath,
            Metrics = metrics,
            MetricsPath = prep.MetricsPath,
            Units = unitSnapshot.Active,
            TotalElapsedMs = totalSw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// A zero return from <c>cFile.ExportFile</c> is not proof of an export: the
    /// Commit contract needs a file on disk with content in it. Returns the byte
    /// count on success.
    /// </summary>
    internal static Result<long> ValidateExportedE2K(int returnCode, string e2kFile)
    {
        if (returnCode != 0)
        {
            return Result.Fail<long>(
                EtabsApiDiagnosticFormatter.ApiReturn("cFile.ExportFile", returnCode));
        }

        if (!File.Exists(e2kFile))
        {
            return Result.Fail<long>(EtabsApiDiagnosticFormatter.Bounded(
                $"cFile.ExportFile reported success but wrote no file at '{e2kFile}'"));
        }

        var size = new FileInfo(e2kFile).Length;
        return size > 0
            ? Result.Ok(size)
            : Result.Fail<long>(EtabsApiDiagnosticFormatter.Bounded(
                $"cFile.ExportFile wrote an empty E2K at '{e2kFile}'"));
    }

    private static string SafeFileName(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
