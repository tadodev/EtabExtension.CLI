namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Models;

public record ExportResultsResponse(bool Success, string? OutputPath = null, string? ErrorMessage = null);