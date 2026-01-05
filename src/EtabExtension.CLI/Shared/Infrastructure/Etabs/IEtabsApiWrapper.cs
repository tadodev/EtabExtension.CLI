using EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Abstraction over EtabSharp for testability and maintainability
/// </summary>
public interface IEtabsApiWrapper
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
    /// Open an ETABS model file
    /// </summary>
    Task<OpenModelResult> OpenModelAsync(string filePath);

    /// <summary>
    /// Check if a model has been analyzed
    /// </summary>
    Task<bool> IsModelAnalyzedAsync(string filePath);

    /// <summary>
    /// Run analysis on an open model
    /// </summary>
    Task<bool> RunAnalysisAsync(string filePath);

    /// <summary>
    /// Export results from an analyzed model
    /// </summary>
    Task<ExportResultsResponse> ExportResultsAsync(string filePath, string outputPath);

    /// <summary>
    /// Generate .e2k file from .edb
    /// </summary>
    Task<bool> GenerateE2KFileAsync(string edbFilePath, string e2KOutputPath);

    /// <summary>
    /// Close an open model
    /// </summary>
    Task CloseModelAsync();
}