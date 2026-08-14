using System.Diagnostics;
using EtabExtension.CLI.Features.GenerateE2K.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.GenerateE2K;

public class GenerateE2KService : IGenerateE2KService
{
    public async Task<Result<GenerateE2KData>> GenerateE2KAsync(
        string inputFilePath,
        string outputFilePath,
        bool overwrite)
    {
        await Task.CompletedTask;

        // Input validation — before touching COM
        if (!File.Exists(inputFilePath))
            return Result.Fail<GenerateE2KData>($"Input file not found: {inputFilePath}");

        if (!inputFilePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<GenerateE2KData>("Input must be an .edb file");

        if (File.Exists(outputFilePath) && !overwrite)
            return Result.Fail<GenerateE2KData>(
                $"Output file already exists: {outputFilePath}. Use --overwrite to replace.");

        var pathError = PathSafe.GetErrorIfInvalidPath(outputFilePath, "OutputFile");
        if (pathError is not null)
            return Result.Fail<GenerateE2KData>(pathError);

        var outputDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        ETABSApplication? app = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine("ℹ Starting ETABS (hidden)...");
            app = ETABSWrapper.CreateNew();
            if (app is null)
                return Result.Fail<GenerateE2KData>("Failed to start ETABS hidden instance.");

            EtabsSessionHelpers.HideIfVisible(app);
            Console.Error.WriteLine($"✓ ETABS started hidden (v{app.FullVersion})");

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(inputFilePath)}");
            int openRet = app.Model.Files.OpenFile(inputFilePath);
            if (openRet != 0)
                return Result.Fail<GenerateE2KData>($"OpenFile failed (ret={openRet})");

            Console.Error.WriteLine("ℹ Exporting to .e2k...");
            int exportRet = app.Model.Files.ExportFile(outputFilePath, eFileTypeIO.TextFile);
            stopwatch.Stop();

            if (exportRet != 0 || !File.Exists(outputFilePath))
                return Result.Fail<GenerateE2KData>(
                    $"ExportFile failed (ret={exportRet})");

            var fileSize = new FileInfo(outputFilePath).Length;
            Console.Error.WriteLine($"✓ Exported ({fileSize / 1024.0:F1} KB, {stopwatch.ElapsedMilliseconds} ms)");

            return Result.Ok(new GenerateE2KData
            {
                InputFile = inputFilePath,
                OutputFile = outputFilePath,
                FileSizeBytes = fileSize,
                GenerationTimeMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<GenerateE2KData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            // Mode B: always exit hidden instance
            app?.Application.ApplicationExit(false);
            app?.Dispose();
        }
    }

    public async Task<Result<GenerateE2KData>> GenerateE2KOnAppAsync(
        ETABSApplication app, string inputFilePath, string outputFilePath, bool overwrite)
    {
        await Task.CompletedTask;
        if (!File.Exists(inputFilePath)) return Result.Fail<GenerateE2KData>($"Input file not found: {inputFilePath}");
        if (!inputFilePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase)) return Result.Fail<GenerateE2KData>("Input must be an .edb file");
        if (File.Exists(outputFilePath) && !overwrite) return Result.Fail<GenerateE2KData>($"Output file already exists: {outputFilePath}. Use --overwrite to replace.");
        var outputDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            int openRet = app.Model.Files.OpenFile(inputFilePath);
            if (openRet != 0) return Result.Fail<GenerateE2KData>($"OpenFile failed (ret={openRet})");
            int exportRet = app.Model.Files.ExportFile(outputFilePath, eFileTypeIO.TextFile);
            stopwatch.Stop();
            if (exportRet != 0 || !File.Exists(outputFilePath)) return Result.Fail<GenerateE2KData>($"ExportFile failed (ret={exportRet})");
            return Result.Ok(new GenerateE2KData { InputFile = inputFilePath, OutputFile = outputFilePath, FileSizeBytes = new FileInfo(outputFilePath).Length, GenerationTimeMs = stopwatch.ElapsedMilliseconds });
        }
        catch (Exception ex) { return Result.Fail<GenerateE2KData>($"ETABS COM error: {ex.Message}"); }
    }
}
