using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.ExportResults;

/// <summary>
/// Implementation of results export using ETABS API
/// </summary>
public class EtabsApiExportResults : IEtabsApiExportResults
{
    private readonly IEtabsConnection _connection;
    private readonly IEtabsFileOperations _fileOperations;

    public EtabsApiExportResults(IEtabsConnection connection, IEtabsFileOperations fileOperations)
    {
        _connection = connection;
        _fileOperations = fileOperations;
    }

    public async Task<ExportResultsResponse> ExportResultsAsync(string filePath, string outputPath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected)
                {
                    return new ExportResultsResponse(false, null, "Could not connect to ETABS");
                }
            }

            var app = _connection.GetEtabsApp();
            if (app == null)
            {
                return new ExportResultsResponse(false, null, "ETABS connection lost");
            }

            // Ensure file is open
            var isOpen = await _fileOperations.IsFileOpenAsync(filePath);

            if (!isOpen)
            {
                var openResult = await _fileOperations.OpenModelAsync(filePath);
                if (!openResult.Success)
                {
                    return new ExportResultsResponse(false, null, $"Failed to open file: {openResult.ErrorMessage}");
                }
            }

            // TODO: Implement results export based on specific requirements
            return new ExportResultsResponse(false, null, "Export results not yet implemented");
        }
        catch (Exception ex)
        {
            return new ExportResultsResponse(false, null, ex.Message);
        }
    }
}
