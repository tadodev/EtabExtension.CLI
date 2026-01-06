using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Validation;

/// <summary>
/// Implementation of ETABS API wrapper for validation operations
/// </summary>
public class EtabsApiValidation : IEtabsApiValidation
{
    private readonly IEtabsConnection _connection;
    private readonly IEtabsFileOperations _fileOperations;

    public EtabsApiValidation(IEtabsConnection connection, IEtabsFileOperations fileOperations)
    {
        _connection = connection;
        _fileOperations = fileOperations;
    }

    public async Task<bool> IsEtabsInstalledAsync()
    {
        return await _connection.IsEtabsInstalledAsync();
    }

    public async Task<string?> GetEtabsVersionAsync()
    {
        return await _fileOperations.GetEtabsVersionAsync();
    }

    public bool IsValidEtabsFile(string filePath)
    {
        return _fileOperations.IsValidEtabsFile(filePath);
    }

    public async Task<bool> IsModelAnalyzedAsync(string filePath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected) return false;
            }

            var app = _connection.GetEtabsApp();
            if (app == null)
            {
                return false;
            }

            // Check if this file is currently open
            var isOpen = await _fileOperations.IsFileOpenAsync(filePath);

            if (!isOpen)
            {
                // Need to open it to check analysis status
                var openResult = await _fileOperations.OpenModelAsync(filePath);
                if (!openResult.Success)
                {
                    return false;
                }
            }

            // Get case status to check if analysis has been run
            var caseStatuses = app.Model.Analyze.GetCaseStatus();

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
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected) return false;
            }

            var app = _connection.GetEtabsApp();
            if (app == null)
            {
                return false;
            }

            // Ensure file is open
            var isOpen = await _fileOperations.IsFileOpenAsync(filePath);

            if (!isOpen)
            {
                var openResult = await _fileOperations.OpenModelAsync(filePath);
                if (!openResult.Success)
                {
                    return false;
                }
            }

            // Run complete analysis
            int result = app.Model.Analyze.RunCompleteAnalysis();
            return result == 0;
        }
        catch
        {
            return false;
        }
    }
}