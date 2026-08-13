using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace EtabExtension.CLI.Bootstrap;

internal sealed class EtabsApiAssemblyResolver(
    IEtabsApiAssemblyLocator locator,
    Func<string, Assembly> load)
{
    private const string ApiAssemblyName = "ETABSv1";

    internal Assembly? Resolve(AssemblyLoadContext _, AssemblyName requested)
    {
        if (!string.Equals(requested.Name, ApiAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = locator.Locate();
        try
        {
            return load(path);
        }
        catch (EtabsApiAssemblyResolutionException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new EtabsApiAssemblyResolutionException(
                $"Failed to load the validated ETABS API assembly at '{path}'.",
                error);
        }
    }
}

internal static class EtabsApiAssemblyBootstrap
{
    private static readonly EtabsApiAssemblyResolver Resolver = new(
        new EtabsApiAssemblyLocator(new SystemEtabsApiAssemblyEnvironment()),
        path => AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
    private static int registrationCount;

    internal static AssemblyLoadContext? RegisteredContext { get; private set; }

    internal static int RegistrationCount => Volatile.Read(ref registrationCount);

    [ModuleInitializer]
    internal static void Register()
    {
        if (Interlocked.CompareExchange(ref registrationCount, 1, 0) != 0)
        {
            return;
        }

        RegisteredContext = AssemblyLoadContext.Default;
        RegisteredContext.Resolving += Resolver.Resolve;
    }
}
