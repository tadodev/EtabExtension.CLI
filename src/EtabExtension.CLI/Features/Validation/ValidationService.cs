using EtabExtension.CLI.Features.Validation.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Validation;

namespace EtabExtension.CLI.Features.Validation;

public class ValidationService: IValidationService
{
    private readonly IEtabsApiValidation _etabsApi;
    private readonly IEtabsConnection _connection;
    private readonly IEtabsFileOperations _fileOperations;

    public ValidationService(
        IEtabsApiValidation etabsApi, 
        IEtabsConnection connection,
        IEtabsFileOperations fileOperations)
    {
        _etabsApi = etabsApi;
        _connection = connection;
        _fileOperations = fileOperations;
    }

    public async Task<Result<ValidationData>> ValidateAsync(string? filePath = null)
    {
        var messages = new List<string>();

        try
        {
            // Step 1: Check ETABS installation
            var isInstalled = await _etabsApi.IsEtabsInstalledAsync();

            if (!isInstalled)
            {
                messages.Add("ETABS is not running or not installed");
                messages.Add("Please start ETABS before running this command");

                var data = new ValidationData
                {
                    EtabsInstalled = false,
                    ValidationMessages = messages
                };

                return Result<ValidationData>.Fail("ETABS installation not found or not running") with { Data = data };
            }

            messages.Add("✓ ETABS is running");

            // Step 2: Get ETABS version
            var version = await _etabsApi.GetEtabsVersionAsync();
            messages.Add($"✓ ETABS version: {version ?? "Unknown"}");

            // If no file path provided, we're done
            if (string.IsNullOrEmpty(filePath))
            {
                var data = new ValidationData
                {
                    EtabsInstalled = true,
                    EtabsVersion = version,
                    ValidationMessages = messages
                };

                return Result<ValidationData>.Ok(data);
            }

            // Step 3: Check file exists
            var fileExists = File.Exists(filePath);

            if (!fileExists)
            {
                messages.Add($"✗ File not found: {filePath}");

                var data = new ValidationData
                {
                    EtabsInstalled = true,
                    EtabsVersion = version,
                    FileExists = false,
                    FilePath = filePath,
                    ValidationMessages = messages
                };

                return Result<ValidationData>.Fail("File not found") with { Data = data };
            }

            messages.Add($"✓ File exists: {Path.GetFileName(filePath)}");

            // Step 4: Validate file type
            var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
            var isValidType = _etabsApi.IsValidEtabsFile(filePath);

            if (!isValidType)
            {
                messages.Add($"✗ Invalid file type: {fileExtension}");
                messages.Add("Expected: .edb or .e2k");

                var data = new ValidationData
                {
                    EtabsInstalled = true,
                    EtabsVersion = version,
                    FileExists = true,
                    FilePath = filePath,
                    FileExtension = fileExtension,
                    FileValid = false,
                    ValidationMessages = messages
                };

                return Result<ValidationData>.Fail("Invalid file type") with { Data = data };
            }

            messages.Add($"✓ Valid ETABS file type: {fileExtension}");

            // Step 5: Get the currently open file before validation (to restore later)
            var originalOpenFilePath = await _fileOperations.GetCurrentModelPathAsync();
            var isOriginalFileOpen = !string.IsNullOrEmpty(originalOpenFilePath);

            if (isOriginalFileOpen)
            {
                messages.Add($"ℹ Currently open file: {Path.GetFileName(originalOpenFilePath)}");
            }

            // Step 6: Check if the input file is already open
            var isUserFileAlreadyOpen = await _fileOperations.IsFileOpenAsync(filePath);

            if (!isUserFileAlreadyOpen)
            {
                messages.Add($"ℹ Opening file for validation: {Path.GetFileName(filePath)}");

                // Open the user's file if not already open
                var openResult = await _fileOperations.OpenModelAsync(filePath);
                if (!openResult.Success)
                {
                    messages.Add($"✗ Failed to open file for validation: {openResult.ErrorMessage}");

                    var data = new ValidationData
                    {
                        EtabsInstalled = true,
                        EtabsVersion = version,
                        FileExists = true,
                        FilePath = filePath,
                        FileExtension = fileExtension,
                        FileValid = false,
                        ValidationMessages = messages
                    };

                    return Result<ValidationData>.Fail("Could not open file for validation") with { Data = data };
                }
            }
            else
            {
                messages.Add("✓ File is already open in ETABS");
            }

            // Step 7: Check analysis status
            var isAnalyzed = await _etabsApi.IsModelAnalyzedAsync(filePath);

            if (isAnalyzed)
            {
                messages.Add("✓ Model has been analyzed");
            }
            else
            {
                messages.Add("⚠ Model has not been analyzed");
            }

            // Step 8: Restore original file if different file was open before
            // This is important to avoid disrupting user's current work
            if (isOriginalFileOpen && !isUserFileAlreadyOpen && originalOpenFilePath != filePath)
            {
                messages.Add($"ℹ Restoring previously open file: {Path.GetFileName(originalOpenFilePath)}");
                var restoreResult = await _fileOperations.OpenModelAsync(originalOpenFilePath);
                if (!restoreResult.Success)
                {
                    messages.Add($"⚠ Warning: Could not restore original file. User may need to manually reopen: {originalOpenFilePath}");
                }
            }

            // Success - all validations passed
            var successData = new ValidationData
            {
                EtabsInstalled = true,
                EtabsVersion = version,
                FileExists = true,
                FilePath = filePath,
                FileExtension = fileExtension,
                FileValid = true,
                IsAnalyzed = isAnalyzed,
                ValidationMessages = messages
            };

            return Result<ValidationData>.Ok(successData);
        }
        catch (Exception ex)
        {
            messages.Add($"✗ Validation error: {ex.Message}");

            var errorData = new ValidationData
            {
                EtabsInstalled = false,
                ValidationMessages = messages
            };

            return Result<ValidationData>.Fail($"Validation failed: {ex.Message}") with { Data = errorData };
        }
    }
}