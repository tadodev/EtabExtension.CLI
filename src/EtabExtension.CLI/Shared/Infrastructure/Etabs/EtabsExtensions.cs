using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

public static class EtabsExtensions
{
    public static IServiceCollection AddEtabsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IEtabsApiWrapper, EtabsApiWrapper>();
        return services;
    }
}