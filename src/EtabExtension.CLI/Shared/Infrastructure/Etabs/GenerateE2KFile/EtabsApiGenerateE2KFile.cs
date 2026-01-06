using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;
using EtabSharp.Core;
using ETABSv1;


namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.GenerateE2KFile;

/// <summary>
/// Implementation of E2K file generation using ETABS API
/// </summary>
public class EtabsApiGenerateE2KFile : IEtabsApiGenerateE2KFile
{
    private readonly IEtabsConnection _connection;
    private readonly IEtabsFileOperations _fileOperations;

    public EtabsApiGenerateE2KFile(IEtabsConnection connection, IEtabsFileOperations fileOperations)
    {
        _connection = connection;
        _fileOperations = fileOperations;
    }

    public async Task<bool> GenerateE2KFileAsync(string edbFilePath, string e2KOutputPath)
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
            var isOpen = await _fileOperations.IsFileOpenAsync(edbFilePath);

            if (!isOpen)
            {
                var openResult = await _fileOperations.OpenModelAsync(edbFilePath);
                if (!openResult.Success)
                {
                    return false;
                }
            }

            // Export to .e2k format using TextFile type
            // According to ETABS API: eFileTypeIO.TextFile exports to .e2k format
            int exportRet = app.Model.Files.ExportFile(e2KOutputPath, eFileTypeIO.TextFile);

            return exportRet == 0;
        }
        catch
        {
            return false;
        }
    }
}
