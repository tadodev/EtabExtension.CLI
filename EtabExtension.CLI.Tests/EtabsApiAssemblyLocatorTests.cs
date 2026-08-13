using System.Reflection;
using EtabExtension.CLI.Bootstrap;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class EtabsApiAssemblyLocatorTests
{
    private const string DefaultInstallDirectory =
        @"C:\Program Files\Computers and Structures\ETABS 23";

    [Fact]
    public void Override_is_authoritative_and_wins_over_default_install()
    {
        var environment = new FakeEnvironment();
        environment.EnvironmentVariables["ETABS_INSTALL_DIR"] = @"D:\Custom\ETABS 23";
        environment.AddValidEtabs23(@"D:\Custom\ETABS 23");
        environment.AddValidEtabs23(DefaultInstallDirectory);

        var path = new EtabsApiAssemblyLocator(environment).Locate();

        Assert.Equal(
            Path.GetFullPath(@"D:\Custom\ETABS 23\ETABSv1.dll"),
            path);
        Assert.DoesNotContain(
            environment.FileExistenceChecks,
            item => item.StartsWith(DefaultInstallDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Invalid_override_does_not_fall_back()
    {
        var environment = new FakeEnvironment();
        environment.EnvironmentVariables["ETABS_INSTALL_DIR"] = @"D:\Missing";
        environment.AddValidEtabs23(DefaultInstallDirectory);

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("ETABS_INSTALL_DIR", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            environment.FileExistenceChecks,
            item => item.StartsWith(DefaultInstallDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_candidate_is_etabs_23_under_program_files()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);

        var path = new EtabsApiAssemblyLocator(environment).Locate();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(DefaultInstallDirectory, "ETABSv1.dll")),
            path);
    }

    [Fact]
    public void Missing_etabs_executable_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.Files.Remove(Path.Combine(DefaultInstallDirectory, "ETABS.exe"));

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("ETABS.exe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_api_dll_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.Files.Remove(Path.Combine(DefaultInstallDirectory, "ETABSv1.dll"));

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("ETABSv1.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_etabs_product_major_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.FileVersions[Path.Combine(DefaultInstallDirectory, "ETABS.exe")] =
            new EtabsFileVersion(24, 24, "24.0.0.0", "24.0.0.0");

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("major version 23", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_api_file_major_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.FileVersions[Path.Combine(DefaultInstallDirectory, "ETABSv1.dll")] =
            new EtabsFileVersion(3, 3, "3.0.0.0", "3.0.0.0");

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("API file major version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_managed_assembly_name_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.AssemblyIdentities[Path.Combine(DefaultInstallDirectory, "ETABSv1.dll")] =
            CreateAssemblyIdentity("NotEtabs", new Version(1, 0, 0, 0), ValidToken);

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("managed assembly name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_managed_assembly_version_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.AssemblyIdentities[Path.Combine(DefaultInstallDirectory, "ETABSv1.dll")] =
            CreateAssemblyIdentity("ETABSv1", new Version(2, 0, 0, 0), ValidToken);

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("assembly version 1.0.0.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_public_key_token_fails_closed()
    {
        var environment = new FakeEnvironment();
        environment.AddValidEtabs23(DefaultInstallDirectory);
        environment.AssemblyIdentities[Path.Combine(DefaultInstallDirectory, "ETABSv1.dll")] =
            CreateAssemblyIdentity(
                "ETABSv1",
                new Version(1, 0, 0, 0),
                Convert.FromHexString("0000000000000000"));

        var error = Assert.Throws<EtabsApiAssemblyResolutionException>(
            () => new EtabsApiAssemblyLocator(environment).Locate());

        AssertStable(error);
        Assert.Contains("public key token", error.Message, StringComparison.Ordinal);
    }

    private static readonly byte[] ValidToken = Convert.FromHexString("453d728ef24c6f5e");

    private static AssemblyName CreateAssemblyIdentity(
        string name,
        Version version,
        byte[] publicKeyToken)
    {
        var identity = new AssemblyName
        {
            Name = name,
            Version = version,
        };
        identity.SetPublicKeyToken(publicKeyToken);
        return identity;
    }

    private static void AssertStable(EtabsApiAssemblyResolutionException error)
    {
        Assert.StartsWith(
            $"{EtabsApiAssemblyResolutionException.Code}:",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("Install supported ETABS 23", error.Message, StringComparison.Ordinal);
        Assert.Contains("ETABS_INSTALL_DIR", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeEnvironment : IEtabsApiAssemblyEnvironment
    {
        internal Dictionary<string, string?> EnvironmentVariables { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, EtabsFileVersion> FileVersions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, AssemblyName> AssemblyIdentities { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal List<string> FileExistenceChecks { get; } = [];

        public string? GetEnvironmentVariable(string name) =>
            EnvironmentVariables.GetValueOrDefault(name);

        public string GetProgramFilesDirectory() => @"C:\Program Files";

        public bool FileExists(string path)
        {
            FileExistenceChecks.Add(path);
            return Files.Contains(path);
        }

        public EtabsFileVersion GetFileVersion(string path) => FileVersions[path];

        public AssemblyName GetAssemblyName(string path) => AssemblyIdentities[path];

        internal void AddValidEtabs23(string directory)
        {
            var executable = Path.Combine(directory, "ETABS.exe");
            var api = Path.Combine(directory, "ETABSv1.dll");
            Files.Add(executable);
            Files.Add(api);
            FileVersions[executable] = new EtabsFileVersion(23, 23, "23.3.0.4545", "23.3.0.4545");
            FileVersions[api] = new EtabsFileVersion(2, 2, "2.16.0.0", "2.16.0.0");
            AssemblyIdentities[api] = CreateAssemblyIdentity(
                "ETABSv1",
                new Version(1, 0, 0, 0),
                ValidToken);
        }
    }
}
