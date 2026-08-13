using System.Diagnostics;
using System.Reflection;

namespace EtabExtension.CLI.Bootstrap;

internal sealed record EtabsFileVersion(
    int FileMajor,
    int ProductMajor,
    string? FileVersion,
    string? ProductVersion);

internal interface IEtabsApiAssemblyEnvironment
{
    string? GetEnvironmentVariable(string name);
    string GetProgramFilesDirectory();
    bool FileExists(string path);
    EtabsFileVersion GetFileVersion(string path);
    AssemblyName GetAssemblyName(string path);
}

internal interface IEtabsApiAssemblyLocator
{
    string Locate();
}

internal sealed class EtabsApiAssemblyResolutionException : InvalidOperationException
{
    internal const string Code = "ETABS_API_ASSEMBLY_UNAVAILABLE";

    internal EtabsApiAssemblyResolutionException(string detail, Exception? inner = null)
        : base(
            $"{Code}: {detail} Install supported ETABS 23 or set ETABS_INSTALL_DIR " +
            "to its installation directory.",
            inner)
    {
    }
}

internal sealed class EtabsApiAssemblyLocator(IEtabsApiAssemblyEnvironment environment)
    : IEtabsApiAssemblyLocator
{
    private const string OverrideVariable = "ETABS_INSTALL_DIR";
    private const string ApiAssemblyName = "ETABSv1";
    private const string ExpectedPublicKeyToken = "453d728ef24c6f5e";
    private static readonly Version ExpectedAssemblyVersion = new(1, 0, 0, 0);
    private static readonly byte[] ExpectedToken = Convert.FromHexString(ExpectedPublicKeyToken);

    public string Locate()
    {
        var overrideDirectory = environment.GetEnvironmentVariable(OverrideVariable);
        var hasOverride = !string.IsNullOrWhiteSpace(overrideDirectory);

        try
        {
            var directory = hasOverride
                ? overrideDirectory!.Trim()
                : ResolveDefaultInstallDirectory();
            var fullDirectory = Path.GetFullPath(directory);
            var executablePath = Path.Combine(fullDirectory, "ETABS.exe");
            var apiAssemblyPath = Path.Combine(fullDirectory, "ETABSv1.dll");
            var source = hasOverride
                ? $"the authoritative {OverrideVariable} directory '{fullDirectory}'"
                : $"the default ETABS 23 directory '{fullDirectory}'";

            RequireFile(executablePath, source);
            RequireFile(apiAssemblyPath, source);
            ValidateEtabsVersion(executablePath);
            ValidateApiVersion(apiAssemblyPath);
            ValidateManagedIdentity(apiAssemblyPath);
            return apiAssemblyPath;
        }
        catch (EtabsApiAssemblyResolutionException)
        {
            throw;
        }
        catch (Exception error)
        {
            var source = hasOverride
                ? $"the authoritative {OverrideVariable} value '{overrideDirectory}'"
                : "the default ETABS 23 installation";
            throw new EtabsApiAssemblyResolutionException(
                $"Could not inspect {source}: {error.Message}",
                error);
        }
    }

    private string ResolveDefaultInstallDirectory()
    {
        var programFiles = environment.GetProgramFilesDirectory();
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new EtabsApiAssemblyResolutionException(
                "Windows Program Files could not be resolved for the default ETABS 23 search.");
        }

        return Path.Combine(programFiles, "Computers and Structures", "ETABS 23");
    }

    private void RequireFile(string path, string source)
    {
        if (!environment.FileExists(path))
        {
            throw new EtabsApiAssemblyResolutionException(
                $"Required file '{Path.GetFileName(path)}' was not found in {source}.");
        }
    }

    private void ValidateEtabsVersion(string executablePath)
    {
        var version = environment.GetFileVersion(executablePath);
        if (version.ProductMajor != 23 || version.FileMajor != 23)
        {
            throw new EtabsApiAssemblyResolutionException(
                $"'{executablePath}' must have ETABS product/file major version 23, but reported " +
                $"product '{version.ProductVersion ?? "unknown"}' and file " +
                $"'{version.FileVersion ?? "unknown"}'.");
        }
    }

    private void ValidateApiVersion(string apiAssemblyPath)
    {
        var version = environment.GetFileVersion(apiAssemblyPath);
        if (version.FileMajor != 2)
        {
            throw new EtabsApiAssemblyResolutionException(
                $"'{apiAssemblyPath}' must have ETABS API file major version 2, but reported " +
                $"'{version.FileVersion ?? "unknown"}'.");
        }
    }

    private void ValidateManagedIdentity(string apiAssemblyPath)
    {
        var identity = environment.GetAssemblyName(apiAssemblyPath);
        if (!string.Equals(identity.Name, ApiAssemblyName, StringComparison.Ordinal))
        {
            throw new EtabsApiAssemblyResolutionException(
                $"'{apiAssemblyPath}' has managed assembly name '{identity.Name ?? "unknown"}', " +
                $"expected '{ApiAssemblyName}'.");
        }

        if (identity.Version != ExpectedAssemblyVersion)
        {
            throw new EtabsApiAssemblyResolutionException(
                $"'{apiAssemblyPath}' must have assembly version {ExpectedAssemblyVersion}, but " +
                $"reported '{identity.Version?.ToString() ?? "unknown"}'.");
        }

        var token = identity.GetPublicKeyToken() ?? [];
        if (!token.SequenceEqual(ExpectedToken))
        {
            throw new EtabsApiAssemblyResolutionException(
                $"'{apiAssemblyPath}' has public key token '{Convert.ToHexString(token).ToLowerInvariant()}', " +
                $"expected '{ExpectedPublicKeyToken}'.");
        }
    }
}

internal sealed class SystemEtabsApiAssemblyEnvironment : IEtabsApiAssemblyEnvironment
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string GetProgramFilesDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public bool FileExists(string path) => File.Exists(path);

    public EtabsFileVersion GetFileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new EtabsFileVersion(
            version.FileMajorPart,
            version.ProductMajorPart,
            version.FileVersion,
            version.ProductVersion);
    }

    public AssemblyName GetAssemblyName(string path) => AssemblyName.GetAssemblyName(path);
}
