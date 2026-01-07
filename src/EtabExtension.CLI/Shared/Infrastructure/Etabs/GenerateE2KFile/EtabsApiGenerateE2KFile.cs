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

    public async Task<bool> GenerateE2KFileAsync(string edbFilePath, string e2kOutputPath)
    {
        await Task.CompletedTask;

        try
        {
            // Ensure connected to ETABS
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

            // Check if file is already open
            var isOpen = await _fileOperations.IsFileOpenAsync(edbFilePath);

            if (!isOpen)
            {
                // Open the file first
                var openResult = await _fileOperations.OpenModelAsync(edbFilePath);
                if (!openResult.Success)
                {
                    return false;
                }
            }

            // Ensure output directory exists
            var outputDirectory = Path.GetDirectoryName(e2kOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // Export to .e2k format using TextFile type
            // According to ETABS API: eFileTypeIO.TextFile exports to .e2k format
            int exportRet = app.Model.Files.ExportFile(e2kOutputPath, eFileTypeIO.TextFile);

            // Verify the file was created
            if (exportRet == 0 && File.Exists(e2kOutputPath))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
