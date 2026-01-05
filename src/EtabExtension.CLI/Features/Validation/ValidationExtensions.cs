using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Validation;

public static class ValidationExtensions
{
    public static IServiceCollection AddValidationFeature(this IServiceCollection services)
    {
        services.AddScoped<IValidationService, ValidationService>();
        return services;
    }
}