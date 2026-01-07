using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.GenerateE2KFile;
using System.Diagnostics;
using EtabExtension.CLI.Features.GenerateE2K.Models;

namespace EtabExtension.CLI.Features.GenerateE2K;

public class GenerateE2KService : IGenerateE2KService
{
    private readonly IEtabsConnection _connection;
    private readonly IEtabsFileOperations _fileOperations;
    private readonly IEtabsApiGenerateE2KFile _generateE2KApi;

    public GenerateE2KService(
        IEtabsConnection connection,
        IEtabsFileOperations fileOperations,
        IEtabsApiGenerateE2KFile generateE2KApi)
    {
        _connection = connection;
        _fileOperations = fileOperations;
        _generateE2KApi = generateE2KApi;
    }

    public async Task<Result<GenerateE2KData>> GenerateE2KAsync(
        string inputFilePath,
        string? outputFilePath = null,
        bool overwrite = false)
    {
        var messages = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Step 1: Validate input file exists
            if (!File.Exists(inputFilePath))
            {
                messages.Add($"✗ Input file not found: {inputFilePath}");

                var data = new GenerateE2KData
                {
                    InputFile = inputFilePath,
                    FileExists = false,
                    Messages = messages
                };

                return Result<GenerateE2KData>.Fail("Input file not found") with { Data = data };
            }

            messages.Add($"✓ Input file exists: {Path.GetFileName(inputFilePath)}");

            // Step 2: Validate input file type
            var fileExtension = Path.GetExtension(inputFilePath).ToLowerInvariant();
            if (fileExtension != ".edb")
            {
                messages.Add($"✗ Invalid input file type: {fileExtension}");
                messages.Add("Expected: .edb file");

                var data = new GenerateE2KData
                {
                    InputFile = inputFilePath,
                    FileExists = true,
                    FileExtension = fileExtension,
                    Messages = messages
                };

                return Result<GenerateE2KData>.Fail("Invalid input file type. Only .edb files can be converted to .e2k")
                    with
                { Data = data };
            }

            messages.Add($"✓ Valid input file type: {fileExtension}");

            // Step 3: Determine output file path
            var actualOutputPath = outputFilePath;
            if (string.IsNullOrEmpty(actualOutputPath))
            {
                // Default: same directory and name as input, but with .e2k extension
                var inputDirectory = Path.GetDirectoryName(inputFilePath) ?? string.Empty;
                var inputFileNameWithoutExt = Path.GetFileNameWithoutExtension(inputFilePath);
                actualOutputPath = Path.Combine(inputDirectory, $"{inputFileNameWithoutExt}.e2k");
                messages.Add($"ℹ Using default output path: {Path.GetFileName(actualOutputPath)}");
            }

            // Step 4: Check if output file already exists
            var outputExists = File.Exists(actualOutputPath);
            if (outputExists && !overwrite)
            {
                messages.Add($"✗ Output file already exists: {actualOutputPath}");
                messages.Add("Use --overwrite flag to replace existing file");

                var data = new GenerateE2KData
                {
                    InputFile = inputFilePath,
                    FileExists = true,
                    FileExtension = fileExtension,
                    OutputFile = actualOutputPath,
                    OutputExists = true,
                    Messages = messages
                };

                return Result<GenerateE2KData>.Fail("Output file already exists") with { Data = data };
            }

            if (outputExists)
            {
                messages.Add($"⚠ Output file will be overwritten: {Path.GetFileName(actualOutputPath)}");
            }

            // Step 5: Ensure output directory exists
            var outputDirectory = Path.GetDirectoryName(actualOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    messages.Add($"✓ Created output directory: {outputDirectory}");
                }
                catch (Exception ex)
                {
                    messages.Add($"✗ Failed to create output directory: {ex.Message}");

                    var data = new GenerateE2KData
                    {
                        InputFile = inputFilePath,
                        FileExists = true,
                        FileExtension = fileExtension,
                        OutputFile = actualOutputPath,
                        Messages = messages
                    };

                    return Result<GenerateE2KData>.Fail("Failed to create output directory") with { Data = data };
                }
            }

            // Step 6: Check ETABS connection
            if (!_connection.IsConnected)
            {
                messages.Add("ℹ Connecting to ETABS...");
                var connected = await _connection.EnsureEtabsAvailableAsync();

                if (!connected)
                {
                    messages.Add("✗ Could not connect to ETABS");
                    messages.Add("Please ensure ETABS is running");

                    var data = new GenerateE2KData
                    {
                        InputFile = inputFilePath,
                        FileExists = true,
                        FileExtension = fileExtension,
                        OutputFile = actualOutputPath,
                        OutputExists = outputExists,
                        Messages = messages
                    };

                    return Result<GenerateE2KData>.Fail("Could not connect to ETABS") with { Data = data };
                }

                messages.Add("✓ Connected to ETABS");
            }

            // Step 7: Get currently open file to restore later
            var originalOpenFilePath = await _fileOperations.GetCurrentModelPathAsync();
            var isOriginalFileOpen = !string.IsNullOrEmpty(originalOpenFilePath);

            if (isOriginalFileOpen)
            {
                messages.Add($"ℹ Currently open file: {Path.GetFileName(originalOpenFilePath)}");
            }

            // Step 8: Check if input file is already open
            var isInputFileAlreadyOpen = await _fileOperations.IsFileOpenAsync(inputFilePath);

            // Step 9: Generate E2K file
            messages.Add($"ℹ Generating E2K file...");

            var generateSuccess = await _generateE2KApi.GenerateE2KFileAsync(inputFilePath, actualOutputPath);

            stopwatch.Stop();

            if (!generateSuccess)
            {
                messages.Add("✗ Failed to generate E2K file");

                var data = new GenerateE2KData
                {
                    InputFile = inputFilePath,
                    FileExists = true,
                    FileExtension = fileExtension,
                    OutputFile = actualOutputPath,
                    OutputExists = outputExists,
                    GenerationSuccessful = false,
                    GenerationTimeMs = stopwatch.ElapsedMilliseconds,
                    Messages = messages
                };

                return Result<GenerateE2KData>.Fail("E2K generation failed") with { Data = data };
            }

            messages.Add("✓ E2K file generated successfully");

            // Step 10: Get output file size
            long? fileSize = null;
            if (File.Exists(actualOutputPath))
            {
                try
                {
                    var fileInfo = new FileInfo(actualOutputPath);
                    fileSize = fileInfo.Length;
                    messages.Add($"ℹ Output file size: {FormatFileSize(fileSize.Value)}");
                }
                catch
                {
                    // Ignore file size retrieval errors
                }
            }

            // Step 11: Restore original file if different file was open
            if (isOriginalFileOpen && !isInputFileAlreadyOpen && originalOpenFilePath != inputFilePath)
            {
                messages.Add($"ℹ Restoring previously open file: {Path.GetFileName(originalOpenFilePath)}");
                var restoreResult = await _fileOperations.OpenModelAsync(originalOpenFilePath);

                if (!restoreResult.Success)
                {
                    messages.Add($"⚠ Warning: Could not restore original file. User may need to manually reopen: {originalOpenFilePath}");
                }
            }

            // Success
            var successData = new GenerateE2KData
            {
                InputFile = inputFilePath,
                FileExists = true,
                FileExtension = fileExtension,
                OutputFile = actualOutputPath,
                OutputExists = outputExists,
                GenerationSuccessful = true,
                FileSizeBytes = fileSize,
                GenerationTimeMs = stopwatch.ElapsedMilliseconds,
                Messages = messages
            };

            return Result<GenerateE2KData>.Ok(successData);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            messages.Add($"✗ Unexpected error: {ex.Message}");

            var errorData = new GenerateE2KData
            {
                InputFile = inputFilePath,
                FileExists = File.Exists(inputFilePath),
                FileExtension = Path.GetExtension(inputFilePath).ToLowerInvariant(),
                OutputFile = outputFilePath,
                GenerationSuccessful = false,
                GenerationTimeMs = stopwatch.ElapsedMilliseconds,
                Messages = messages
            };

            return Result<GenerateE2KData>.Fail($"E2K generation failed: {ex.Message}") with { Data = errorData };
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}