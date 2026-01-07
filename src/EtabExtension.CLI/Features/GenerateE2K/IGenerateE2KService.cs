using EtabExtension.CLI.Features.GenerateE2K.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.GenerateE2K;

public interface IGenerateE2KService
{
    /// <summary>
    /// Generate .e2k file from .edb file
    /// </summary>
    /// <param name="inputFilePath">Path to input .edb file</param>
    /// <param name="outputFilePath">Path for output .e2k file (optional)</param>
    /// <param name="overwrite">Whether to overwrite existing output file</param>
    Task<Result<GenerateE2KData>> GenerateE2KAsync(
        string inputFilePath,
        string? outputFilePath = null,
        bool overwrite = false);
}