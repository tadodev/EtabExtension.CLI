using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;

/// <summary>
/// Implementation of ETABS connection management
/// </summary>
public class EtabsConnection : IEtabsConnection
{
    private ETABSApplication? _etabsApp;
    private bool _isConnected;

    public bool IsConnected => _isConnected;

    /// <summary>
    /// Check if ETABS is installed (via COM registration)
    /// Does NOT require ETABS to be running
    /// </summary>
    public async Task<bool> IsEtabsInstalledAsync()
    {
        await Task.CompletedTask;

        try
        {
            // ETABS COM ProgID
            const string progId = "CSI.ETABS.API.ETABSObject";
            // Check COM registration without starting ETABS
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            return type != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if ETABS is currently running
    /// </summary>
    public async Task<bool> IsEtabsRunningAsync()
    {
        await Task.CompletedTask;

        try
        {
            return ETABSWrapper.IsRunning();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start ETABS application if not running
    /// Returns true if ETABS is now running (either was already or successfully started)
    /// </summary>
    public async Task<bool> StartEtabsAsync()
    {
        await Task.CompletedTask;

        try
        {
            // Check if already running
            if (ETABSWrapper.IsRunning())
            {
                return true;
            }

            // Create new ETABS instance and start it
            _etabsApp = ETABSWrapper.CreateNew(startApplication: true);
            _isConnected = _etabsApp != null;

            if (_isConnected)
            {
                // Give ETABS a moment to fully initialize
                await Task.Delay(2000);
            }

            return _isConnected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempt to connect to running ETABS instance
    /// Returns true if connection successful, false otherwise
    /// </summary>
    public async Task<bool> TryConnectAsync()
    {
        await Task.CompletedTask;

        try
        {
            if (_isConnected && _etabsApp != null)
            {
                return true; // Already connected
            }

            _etabsApp = ETABSWrapper.Connect();
            _isConnected = _etabsApp != null;

            return _isConnected;
        }
        catch
        {
            _isConnected = false;
            _etabsApp = null;
            return false;
        }
    }

    /// <summary>
    /// Ensure ETABS is running and connected
    /// Will start ETABS if not running, then connect
    /// </summary>
    public async Task<bool> EnsureEtabsAvailableAsync()
    {
        // If already connected, we're good
        if (_isConnected && _etabsApp != null)
        {
            return true;
        }

        // Try to connect to existing instance first
        if (await TryConnectAsync())
        {
            return true;
        }

        // Not running, so start it
        if (!await StartEtabsAsync())
        {
            return false;
        }

        // Now try to connect
        return await TryConnectAsync();
    }

    /// <summary>
    /// Get the current ETABS application instance
    /// </summary>
    public ETABSApplication? GetEtabsApp() => _etabsApp;
}
