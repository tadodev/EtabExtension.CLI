using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;

/// <summary>
/// Handles file operations with ETABS models
/// </summary>
public interface IEtabsFileOperations
{
    /// <summary>
    /// Get the installed ETABS version
    /// </summary>
    Task<string?> GetEtabsVersionAsync();

    /// <summary>
    /// Check if a file exists and is a valid ETABS file (.edb or .e2k)
    /// </summary>
    bool IsValidEtabsFile(string filePath);

    /// <summary>
    /// Gets currently open model file path
    /// Returns null if no model is open
    /// </summary>
    Task<string?> GetCurrentModelPathAsync();

    /// <summary>
    /// Checks if a specific file is currently open
    /// </summary>
    Task<bool> IsFileOpenAsync(string filePath);

    /// <summary>
    /// Open an ETABS model file
    /// </summary>
    Task<OpenModelResult> OpenModelAsync(string filePath);

    /// <summary>
    /// Close an open model
    /// </summary>
    Task CloseModelAsync();
}
