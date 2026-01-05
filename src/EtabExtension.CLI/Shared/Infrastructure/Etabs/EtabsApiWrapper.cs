using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Implementation of ETABS API wrapper using EtabSharp
/// </summary>
public class EtabsApiWrapper : IEtabsApiWrapper
{
    private ETABSApplication? _etabsApp;
    private bool _isConnected;

    public async Task<bool> IsEtabsInstalledAsync()
    {
        await Task.CompletedTask;

        try
        {
            // Check if ETABS is running
            return ETABSWrapper.IsRunning();
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetEtabsVersionAsync()
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp != null)
            {
                return _etabsApp.FullVersion;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool IsValidEtabsFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".edb" or ".e2k";
    }

    public async Task<OpenModelResult> OpenModelAsync(string filePath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp == null)
            {
                return new OpenModelResult(false, "Could not connect to ETABS");
            }

            // Open the file
            int ret = _etabsApp.Model.Files.OpenFile(filePath);

            if (ret != 0)
            {
                return new OpenModelResult(false, $"Failed to open file. Error code: {ret}");
            }

            return new OpenModelResult(true);
        }
        catch (Exception ex)
        {
            return new OpenModelResult(false, ex.Message);
        }
    }

    public async Task<bool> IsModelAnalyzedAsync(string filePath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp == null)
            {
                return false;
            }

            // Open file if not already open
            var currentFile = _etabsApp.Model.ModelInfo.GetModelFilepath();
            if (string.IsNullOrEmpty(currentFile) || !currentFile.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                int ret = _etabsApp.Model.Files.OpenFile(filePath);
                if (ret != 0)
                {
                    return false;
                }
            }

            // Get case status to check if analysis has been run
            var caseStatuses = _etabsApp.Model.Analyze.GetCaseStatus();

            // Check if any case has been analyzed (status 4 = Finished)
            return caseStatuses.Any(cs => cs.IsFinished);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RunAnalysisAsync(string filePath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp == null)
            {
                return false;
            }

            // Open file if not already open
            var currentFile = _etabsApp.Model.ModelInfo.GetModelFilepath();
            if (string.IsNullOrEmpty(currentFile) || !currentFile.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                int ret = _etabsApp.Model.Files.OpenFile(filePath);
                if (ret != 0)
                {
                    return false;
                }
            }

            // Run complete analysis
            int result = _etabsApp.Model.Analyze.RunCompleteAnalysis();
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ExportResultsResponse> ExportResultsAsync(string filePath, string outputPath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp == null)
            {
                return new ExportResultsResponse(false, null, "Could not connect to ETABS");
            }

            // Open file if not already open
            var currentFile = _etabsApp.Model.ModelInfo.GetModelFilepath();
            if (string.IsNullOrEmpty(currentFile) || !currentFile.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                int ret = _etabsApp.Model.Files.OpenFile(filePath);
                if (ret != 0)
                {
                    return new ExportResultsResponse(false, null, $"Failed to open file. Error code: {ret}");
                }
            }

            // Export results (implement based on specific requirements)
            // For now, placeholder
            return new ExportResultsResponse(false, null, "Export results not yet implemented");
        }
        catch (Exception ex)
        {
            return new ExportResultsResponse(false, null, ex.Message);
        }
    }

    public async Task<bool> GenerateE2KFileAsync(string edbFilePath, string e2KOutputPath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_isConnected)
            {
                _etabsApp = ETABSWrapper.Connect();
                _isConnected = _etabsApp != null;
            }

            if (_etabsApp == null)
            {
                return false;
            }

            // Open the .edb file
            var currentFile = _etabsApp.Model.ModelInfo.GetModelFilepath();
            if (string.IsNullOrEmpty(currentFile) || !currentFile.Equals(edbFilePath, StringComparison.OrdinalIgnoreCase))
            {
                int openRet = _etabsApp.Model.Files.OpenFile(edbFilePath);
                if (openRet != 0)
                {
                    return false;
                }
            }

            // Export to .e2k format using TextFile type
            // According to ETABS API: eFileTypeIO.TextFile exports to .e2k format
            int exportRet = _etabsApp.Model.Files.ExportFile(e2KOutputPath, eFileTypeIO.TextFile);

            return exportRet == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task CloseModelAsync()
    {
        await Task.CompletedTask;

        try
        {
            if (_etabsApp != null)
            {
                // Note: We typically don't close ETABS when using Connect()
                // as it might be in use by the user
                _isConnected = false;
            }
        }
        catch
        {
            // Ignore errors on cleanup
        }
    }
}