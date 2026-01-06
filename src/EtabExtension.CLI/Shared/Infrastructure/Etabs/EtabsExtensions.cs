using EtabExtension.CLI.Shared.Infrastructure.Etabs.ExportResults;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Validation;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsConnection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsFileOperations;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.GenerateE2KFile;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

public static class EtabsExtensions
{
    public static IServiceCollection AddEtabsInfrastructure(this IServiceCollection services)
    {
        // Register core ETABS connection management
        services.AddSingleton<IEtabsConnection, EtabsConnection.EtabsConnection>();
        
        // Register file operations
        services.AddSingleton<IEtabsFileOperations, EtabsFileOperations.EtabsFileOperations>();
        
        // Register feature-specific implementations
        services.AddSingleton<IEtabsApiValidation, EtabsApiValidation>();
        services.AddSingleton<IEtabsApiGenerateE2KFile, EtabsApiGenerateE2KFile>();
        services.AddSingleton<IEtabsApiExportResults, EtabsApiExportResults>();
        
        return services;
    }
}