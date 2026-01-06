using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;

/// <summary>
/// Manages ETABS application connection and lifecycle
/// </summary>
public interface IEtabsConnection
{
    /// <summary>
    /// Check if ETABS is installed on the system
    /// </summary>
    Task<bool> IsEtabsInstalledAsync();

    /// <summary>
    /// Check if ETABS is currently running
    /// </summary>
    Task<bool> IsEtabsRunningAsync();

    /// <summary>
    /// Start ETABS application if not running
    /// </summary>
    Task<bool> StartEtabsAsync();

    /// <summary>
    /// Attempt to connect to running ETABS instance
    /// </summary>
    Task<bool> TryConnectAsync();

    /// <summary>
    /// Ensure ETABS is running and connected
    /// </summary>
    Task<bool> EnsureEtabsAvailableAsync();

    /// <summary>
    /// Get the current ETABS application instance
    /// </summary>
    ETABSApplication? GetEtabsApp();

    /// <summary>
    /// Check if currently connected to ETABS
    /// </summary>
    bool IsConnected { get; }
}
