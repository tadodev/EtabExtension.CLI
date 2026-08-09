using System.Reflection;
using System.Runtime.Loader;
using EtabExtension.CLI.Bootstrap;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class EtabsApiAssemblyBootstrapTests
{
    [Fact]
    public void Unrelated_assembly_request_is_ignored_without_discovery()
    {
        var locator = new StubLocator(@"C:\ETABS 23\ETABSv1.dll");
        var resolver = new EtabsApiAssemblyResolver(
            locator,
            _ => typeof(string).Assembly);

        var result = resolver.Resolve(
            AssemblyLoadContext.Default,
            new AssemblyName("Unrelated"));

        Assert.Null(result);
        Assert.Equal(0, locator.CallCount);
    }

    [Fact]
    public void Etabsv1_request_loads_only_the_validated_installed_path()
    {
        var expectedPath = Path.GetFullPath(@"C:\ETABS 23\ETABSv1.dll");
        string? loadedPath = null;
        var resolver = new EtabsApiAssemblyResolver(
            new StubLocator(expectedPath),
            path =>
            {
                loadedPath = path;
                return typeof(string).Assembly;
            });

        var result = resolver.Resolve(
            AssemblyLoadContext.Default,
            new AssemblyName("ETABSv1"));

        Assert.Same(typeof(string).Assembly, result);
        Assert.Equal(expectedPath, loadedPath);
    }

    [Fact]
    public void Etabsv1_name_matching_is_case_insensitive()
    {
        var locator = new StubLocator(@"C:\ETABS 23\ETABSv1.dll");
        var resolver = new EtabsApiAssemblyResolver(
            locator,
            _ => typeof(string).Assembly);

        var result = resolver.Resolve(
            AssemblyLoadContext.Default,
            new AssemblyName("etabsv1"));

        Assert.Same(typeof(string).Assembly, result);
        Assert.Equal(1, locator.CallCount);
    }

    [Fact]
    public void Locator_failure_keeps_the_stable_diagnostic()
    {
        var expected = new EtabsApiAssemblyResolutionException("missing test assembly.");
        var resolver = new EtabsApiAssemblyResolver(
            new ThrowingLocator(expected),
            _ => typeof(string).Assembly);

        var actual = Assert.Throws<EtabsApiAssemblyResolutionException>(() =>
            resolver.Resolve(AssemblyLoadContext.Default, new AssemblyName("ETABSv1")));

        Assert.Same(expected, actual);
        Assert.StartsWith(
            $"{EtabsApiAssemblyResolutionException.Code}:",
            actual.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_failure_is_wrapped_with_the_stable_diagnostic()
    {
        var loadError = new BadImageFormatException("bad image");
        var resolver = new EtabsApiAssemblyResolver(
            new StubLocator(@"C:\ETABS 23\ETABSv1.dll"),
            _ => throw loadError);

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(() =>
            resolver.Resolve(AssemblyLoadContext.Default, new AssemblyName("ETABSv1")));

        Assert.StartsWith(
            $"{EtabsApiAssemblyResolutionException.Code}:",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("Failed to load", error.Message, StringComparison.Ordinal);
        Assert.Same(loadError, error.InnerException);
    }

    [Fact]
    public void Production_module_initializer_is_registered_once_on_default_context()
    {
        Assert.Same(
            AssemblyLoadContext.Default,
            EtabsApiAssemblyBootstrap.RegisteredContext);
        Assert.Equal(1, EtabsApiAssemblyBootstrap.RegistrationCount);

        EtabsApiAssemblyBootstrap.Register();

        Assert.Same(
            AssemblyLoadContext.Default,
            EtabsApiAssemblyBootstrap.RegisteredContext);
        Assert.Equal(1, EtabsApiAssemblyBootstrap.RegistrationCount);
    }

    private sealed class StubLocator(string path) : IEtabsApiAssemblyLocator
    {
        internal int CallCount { get; private set; }

        public string Locate()
        {
            CallCount++;
            return path;
        }
    }

    private sealed class ThrowingLocator(EtabsApiAssemblyResolutionException error)
        : IEtabsApiAssemblyLocator
    {
        public string Locate() => throw error;
    }
}
