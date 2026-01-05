using EtabExtension.CLI.Features.Validation.Models;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Features.Validation;

public interface IValidationService
{
    /// <summary>
    /// Validate ETABS installation, file validity, and optionally analysis status
    /// </summary>
    Task<Result<ValidationData>> ValidateAsync(string? filePath = null);
}