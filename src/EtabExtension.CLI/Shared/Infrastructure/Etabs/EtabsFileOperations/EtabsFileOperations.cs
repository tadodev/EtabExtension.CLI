using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;

/// <summary>
/// Implementation of ETABS file operations
/// </summary>
public class EtabsFileOperations : IEtabsFileOperations
{
    private readonly IEtabsConnection _connection;

    public EtabsFileOperations(IEtabsConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Gets ETABS version (requires connection)
    /// </summary>
    public async Task<string?> GetEtabsVersionAsync()
    {
        await Task.CompletedTask;

        try
        {
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected) return null;
            }

            return _connection.GetEtabsApp()?.FullVersion;
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

    /// <summary>
    /// Gets currently open model file path
    /// Returns null if no model is open
    /// </summary>
    public async Task<string?> GetCurrentModelPathAsync()
    {
        await Task.CompletedTask;

        try
        {
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected) return null;
            }

            var app = _connection.GetEtabsApp();
            if (app == null) return null;

            var filepath = app.Model.ModelInfo.GetModelFilepath();
            return string.IsNullOrEmpty(filepath) ? null : filepath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a specific file is currently open
    /// </summary>
    public async Task<bool> IsFileOpenAsync(string filePath)
    {
        var currentPath = await GetCurrentModelPathAsync();

        if (string.IsNullOrEmpty(currentPath))
            return false;

        return string.Equals(
            Path.GetFullPath(currentPath),
            Path.GetFullPath(filePath),
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<OpenModelResult> OpenModelAsync(string filePath)
    {
        await Task.CompletedTask;

        try
        {
            if (!_connection.IsConnected)
            {
                var connected = await _connection.TryConnectAsync();
                if (!connected)
                {
                    return new OpenModelResult(false, "Could not connect to ETABS");
                }
            }

            var app = _connection.GetEtabsApp();
            if (app == null)
            {
                return new OpenModelResult(false, "ETABS connection lost");
            }

            // Check if file is already open
            if (await IsFileOpenAsync(filePath))
            {
                return new OpenModelResult(true, null, true);
            }

            // Open the file
            int ret = app.Model.Files.OpenFile(filePath);

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

    public async Task CloseModelAsync()
    {
        await Task.CompletedTask;

        try
        {
            var app = _connection.GetEtabsApp();
            if (app != null)
            {
                // Note: We typically don't close ETABS when using Connect()
                // as it might be in use by the user
            }
        }
        catch
        {
            // Ignore errors on cleanup
        }
    }
}
