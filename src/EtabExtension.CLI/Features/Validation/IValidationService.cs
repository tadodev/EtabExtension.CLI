using EtabExtension.CLI.Features.Validation.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.Validation;

public interface IValidationService
{
    /// <summary>
    /// Validate ETABS installation, file validity, and analysis status
    /// </summary>
    /// <param name="filePath">Path to ETABS file to validate (can be null to check only ETABS)</param>
    Task<Result<ValidationData>> ValidateAsync(string? filePath);
}