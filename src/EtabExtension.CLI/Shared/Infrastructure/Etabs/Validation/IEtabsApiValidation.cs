using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Validation;

/// <summary>
/// Abstraction over EtabSharp for testability and maintainability
/// Focuses on validation-specific operations
/// </summary>
public interface IEtabsApiValidation
{
    /// <summary>
    /// Check if ETABS is installed on the system
    /// </summary>
    Task<bool> IsEtabsInstalledAsync();

    /// <summary>
    /// Get the installed ETABS version
    /// </summary>
    Task<string?> GetEtabsVersionAsync();

    /// <summary>
    /// Check if a file exists and is a valid ETABS file (.edb or .e2k)
    /// </summary>
    bool IsValidEtabsFile(string filePath);

    /// <summary>
    /// Check if a model has been analyzed
    /// </summary>
    Task<bool> IsModelAnalyzedAsync(string filePath);

    /// <summary>
    /// Run analysis on an open model
    /// </summary>
    Task<bool> RunAnalysisAsync(string filePath);
}