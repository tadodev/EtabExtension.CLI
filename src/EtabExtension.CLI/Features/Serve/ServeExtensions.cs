using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabExtension.CLI.Features.ReadModelMetadata;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Features.Serve.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace EtabExtension.CLI.Features.Serve;

public static class ServeExtensions
{
    public static IServiceCollection AddServeFeature(this IServiceCollection services)
    {
        // Scoped: the serve command creates one scope for the daemon's lifetime,
        // so the shared ETABS session is a single instance for that run and the
        // dispatcher can depend on the scoped feature services.
        services.AddScoped<ISessionRecordStore, JsonSessionRecordStore>();
        services.AddScoped<IProcessInspector, WindowsProcessInspector>();
        services.AddScoped<IManagedEtabsLauncher, ManagedEtabsLauncher>();
        services.AddScoped<IOrphanSessionCleaner, OrphanSessionCleaner>();
        services.AddScoped<IManagedEtabsStartIntentScope, ManagedEtabsStartIntentScope>();
        services.AddScoped<IEtabsWorkScope, EtabsWorkScope>();
        services.AddScoped<IEtabsWorkEnvelope, EtabsWorkEnvelope>();
        services.AddScoped<IEtabsSession, EtabsSession>();
        services.AddScoped<IStaExecutionWorker, StaExecutionWorker>();
        services.AddScoped<IServeShutdownCoordinator, ServeShutdownCoordinator>();
        services.AddScoped<IOperationEventJournalFactory, OperationEventJournalFactory>();
        services.AddScoped<IOperationClock, SystemOperationClock>();
        services.AddScoped<ICachedSessionStatus, CachedSessionStatus>();
        services.AddScoped<IOperationDefinition, AnalyzeAndExtractOperation>();
        services.AddScoped<IOperationManager, OperationManager>();
        services.AddScoped<IServeInspectionService, ServeInspectionService>();
        services.AddScoped<IEtabsInspectionApiFactory, EtabsInspectionApiFactory>();
        services.AddScoped<IServeDispatcher, ServeDispatcher>();
        services.AddScoped<IReadModelMetadataService, ReadModelMetadataService>();
        return services;
    }
}
