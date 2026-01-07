using EtabExtension.CLI.Features.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.GenerateE2K;

public static class GenerateE2KExtensions
{
    public static IServiceCollection AddGenerateE2KFeature(this IServiceCollection services)
    {
        services.AddScoped<IGenerateE2KService, GenerateE2KService>();
        return services;
    }
}