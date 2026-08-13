using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using EtabSharp.Core;
using ETABSv1;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class EtabsLaunchErrorCodes
{
    public const string ExecutableNotFound = "ETABS_EXECUTABLE_NOT_FOUND";
    public const string ExecutableUnresolved = "ETABS_EXECUTABLE_UNRESOLVED";
    public const string ApiObjectCreationFailed = "ETABS_API_OBJECT_CREATION_FAILED";
    public const string ApplicationStartFailed = "ETABS_APPLICATION_START_FAILED";
    public const string ApiModelUnavailable = "ETABS_API_MODEL_UNAVAILABLE";
    public const string ProcessIdentityFailed = "ETABS_PROCESS_IDENTITY_FAILED";
    public const string ExternalOrAmbiguousInstance = "ETABS_EXTERNAL_OR_AMBIGUOUS_INSTANCE";
    public const string ModelInitializationFailed = "ETABS_MODEL_INITIALIZATION_FAILED";
    public const string RecoveryRecordWriteFailed = "ETABS_RECOVERY_RECORD_WRITE_FAILED";
}

public sealed class EtabsLaunchException : InvalidOperationException
{
    public EtabsLaunchException(string code, string message, Exception? innerException = null)
        : base($"[{code}] {message}", innerException) => Code = code;

    public string Code { get; }
}

public interface IEtabsExecutableResolver
{
    string Resolve();
}

public interface IEtabsInstallDiscovery
{
    IReadOnlyList<string> RegistryCandidates();
    IReadOnlyList<string> DefaultInstallCandidates();
}

public sealed class EtabsExecutableResolver(
    IConfiguration configuration,
    IEtabsInstallDiscovery discovery) : IEtabsExecutableResolver
{
    public const string ConfigurationKey = "EtabsExePath";

    public string Resolve()
    {
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Normalize(configured);
            if (!File.Exists(configuredPath))
            {
                throw new EtabsLaunchException(
                    EtabsLaunchErrorCodes.ExecutableNotFound,
                    $"Configured {ConfigurationKey} does not exist: '{configuredPath}'.");
            }

            return configuredPath;
        }

        var discovered = discovery.RegistryCandidates()
            .Concat(discovery.DefaultInstallCandidates())
            .Select(Normalize)
            .FirstOrDefault(File.Exists);
        if (discovered is not null)
        {
            return discovered;
        }

        throw new EtabsLaunchException(
            EtabsLaunchErrorCodes.ExecutableUnresolved,
            $"Could not resolve ETABS.exe. Configure {ConfigurationKey}, install ETABS with an uninstall registry entry, or use a standard 'Computers and Structures\\ETABS <version>' install directory.");
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExecutableNotFound,
                $"ETABS executable path is invalid: '{path}'.",
                ex);
        }
    }
}

public sealed class WindowsEtabsInstallDiscovery : IEtabsInstallDiscovery
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<string> RegistryCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var candidates = new List<RegistryCandidate>();
        ReadUninstallCandidates(RegistryHive.LocalMachine, RegistryView.Registry64, candidates);
        ReadUninstallCandidates(RegistryHive.LocalMachine, RegistryView.Registry32, candidates);
        ReadUninstallCandidates(RegistryHive.CurrentUser, RegistryView.Default, candidates);
        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> DefaultInstallCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.Combine(programFiles, "Computers and Structures");
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root, "ETABS *", SearchOption.TopDirectoryOnly)
                .Select(directory => new RegistryCandidate(
                    Path.Combine(directory, "ETABS.exe"),
                    ParseVersion(Path.GetFileName(directory)["ETABS ".Length..])))
                .OrderByDescending(candidate => candidate.Version)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Path)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"⚠ ETABS default-install discovery failed: {ex.Message}");
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadUninstallCandidates(
        RegistryHive hive,
        RegistryView view,
        ICollection<RegistryCandidate> candidates)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subkeyName in uninstall.GetSubKeyNames())
            {
                using var product = uninstall.OpenSubKey(subkeyName);
                var displayName = product?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)
                    || !displayName.StartsWith("ETABS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = ExecutableFromRegistry(product!);
                if (path is not null)
                {
                    candidates.Add(new(
                        path,
                        ParseVersion(product!.GetValue("DisplayVersion") as string)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine(
                $"⚠ ETABS registry discovery failed for {hive}/{view}: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ExecutableFromRegistry(RegistryKey product)
    {
        if (product.GetValue("DisplayIcon") is string displayIcon)
        {
            var iconPath = displayIcon.Split(',')[0].Trim().Trim('"');
            if (string.Equals(
                    Path.GetFileName(iconPath),
                    "ETABS.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return iconPath;
            }
        }

        return product.GetValue("InstallLocation") is string installLocation
            && !string.IsNullOrWhiteSpace(installLocation)
                ? Path.Combine(installLocation, "ETABS.exe")
                : null;
    }

    private static Version ParseVersion(string? value) =>
        Version.TryParse(value, out var version) ? version : new Version();

    private sealed record RegistryCandidate(string Path, Version Version);
}

public interface IOwnedEtabsProcess : IDisposable
{
    ManagedProcessIdentity Identity { get; }
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

/// <summary>
/// The raw <c>ETABSv1</c> boundary for one managed application, kept behind an
/// interface so the whole lifecycle is exercisable without ETABS or COM.
///
/// <para>Per the Cardex ETABS 23.3 contract: <c>cHelper.CreateObject(path)</c>
/// starts the program and returns nothing on failure, <c>cOAPI.ApplicationStart()</c>
/// returns zero on success, <c>cSapModel.InitializeNewModel()</c> returns zero on
/// success, and <c>cOAPI.ApplicationExit(false)</c> returns zero on success with the
/// <c>cSapModel</c> reference dropped afterwards.</para>
/// </summary>
public interface IEtabsRawApi
{
    /// <summary>Raw <c>cOAPI.ApplicationStart()</c>. Zero means started.</summary>
    int ApplicationStart();

    /// <summary>Raw <c>cHelper.GetOAPIVersionNumber()</c>, used only as wrap metadata.</summary>
    double GetOapiVersionNumber();

    /// <summary>Whether <c>cOAPI.SapModel</c> is available on this started object.</summary>
    bool HasSapModel { get; }

    /// <summary>Raw <c>cSapModel.InitializeNewModel(kip_in_F)</c>. Zero means initialized.</summary>
    int InitializeNewModel();

    /// <summary>Raw <c>cOAPI.ApplicationExit(fileSave)</c>. Zero means exited.</summary>
    int ApplicationExit(bool fileSave);

    /// <summary>
    /// Wraps this exact started object with EtabSharp — no create, start, attach or
    /// ROT lookup. Called only after initialization returned zero.
    /// </summary>
    void CompleteApiReadiness(int majorVersion, double apiVersion, string fullVersion);

    /// <summary>The wrapper from <see cref="CompleteApiReadiness"/>; throws before it.</summary>
    ETABSApplication Application { get; }

    /// <summary>
    /// Passive COM-reference cleanup for the wrapper. Never an exit: only valid after
    /// the authoritative <c>ApplicationExit(false)</c> and confirmed process exit.
    /// </summary>
    void ReleaseApiReferences();
}

public interface IEtabsRawApiFactory
{
    /// <summary>
    /// Raw <c>cHelper.CreateObject(executablePath)</c>, which starts that program. A
    /// null return is the documented failure mode and becomes a typed launch failure.
    /// </summary>
    IEtabsRawApi CreateFromExecutable(string executablePath);
}

/// <summary>Reads ETABS version metadata from the executable — no COM, no model.</summary>
public interface IEtabsVersionProbe
{
    (int MajorVersion, string FullVersion) Read(string executablePath);
}

public sealed class FileEtabsVersionProbe : IEtabsVersionProbe
{
    public (int MajorVersion, string FullVersion) Read(string executablePath)
    {
        var info = FileVersionInfo.GetVersionInfo(executablePath);
        return (
            info.FileMajorPart,
            $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}");
    }
}

public sealed class EtabsRawApiFactory : IEtabsRawApiFactory
{
    public IEtabsRawApi CreateFromExecutable(string executablePath)
    {
        cOAPI api;
        cHelper helper = new Helper();
        try
        {
            api = helper.CreateObject(executablePath);
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiObjectCreationFailed,
                EtabsApiDiagnosticFormatter.Exception("cHelper.CreateObject", ex),
                ex);
        }

        // Documented failure mode: "An instance of cOAPI if successful, nothing otherwise."
        return api is null
            ? throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiObjectCreationFailed,
                $"cHelper.CreateObject returned no API object for '{executablePath}'.")
            : new EtabsRawApi(api, helper);
    }
}

public sealed class EtabsRawApi(cOAPI api, cHelper helper) : IEtabsRawApi
{
    private ETABSApplication? _wrapper;

    public int ApplicationStart() => api.ApplicationStart();

    public double GetOapiVersionNumber() => helper.GetOAPIVersionNumber();

    public bool HasSapModel => api.SapModel is not null;

    public int InitializeNewModel() => api.SapModel.InitializeNewModel(eUnits.kip_in_F);

    public int ApplicationExit(bool fileSave) => api.ApplicationExit(fileSave);

    public void CompleteApiReadiness(int majorVersion, double apiVersion, string fullVersion) =>
        _wrapper = ETABSWrapper.WrapExisting(api, majorVersion, apiVersion, fullVersion);

    public ETABSApplication Application => _wrapper
        ?? throw new InvalidOperationException(
            "Managed ETABS API is not ready: the raw handle has not been wrapped yet.");

    public void ReleaseApiReferences()
    {
        _wrapper?.Dispose();
        _wrapper = null;
    }
}

public interface IManagedEtabsApplication
{
    ETABSApplication Application { get; }
    ManagedProcessIdentity Identity { get; }
    Guid ManagedLaunchRecordId { get; }
    int InitializeNewModel();
    int ExitWithoutSaving();
    void CompleteApiReadiness();
    bool HasExited { get; }
    bool WaitForExit(TimeSpan timeout);
    void Kill();
    void ReleaseOwnedProcessHandle();
    void ReleaseApiReferences();
}

/// <summary>Version metadata captured at launch and used only to wrap the started object.</summary>
public sealed record ManagedEtabsApiVersion(
    int MajorVersion,
    double ApiVersion,
    string FullVersion);

public sealed class ManagedEtabsApplication(
    IEtabsRawApi rawApi,
    ManagedProcessIdentity identity,
    Guid launchRecordId,
    IOwnedEtabsProcess ownedProcess,
    ManagedEtabsApiVersion version) : IManagedEtabsApplication
{
    public ETABSApplication Application => rawApi.Application;
    public ManagedProcessIdentity Identity { get; } = identity;
    public Guid ManagedLaunchRecordId { get; } = launchRecordId;
    public bool HasExited => ownedProcess.HasExited;

    public int InitializeNewModel() => rawApi.InitializeNewModel();

    public int ExitWithoutSaving() => rawApi.ApplicationExit(false);

    /// <summary>Wraps the same started object once initialization has returned zero.</summary>
    public void CompleteApiReadiness() => rawApi.CompleteApiReadiness(
        version.MajorVersion,
        version.ApiVersion,
        version.FullVersion);

    public bool WaitForExit(TimeSpan timeout) => ownedProcess.WaitForExit(timeout);

    public void Kill() => ownedProcess.Kill();

    public void ReleaseOwnedProcessHandle() => ownedProcess.Dispose();

    public void ReleaseApiReferences() => rawApi.ReleaseApiReferences();
}

/// <summary>
/// An authoritative handle on a process this daemon proved it owns, opened by exact
/// identity rather than by PID alone.
/// </summary>
public sealed class WindowsOwnedEtabsProcess : IOwnedEtabsProcess
{
    private readonly Process _process;

    internal WindowsOwnedEtabsProcess(Process process, ManagedProcessIdentity identity)
    {
        _process = process;
        Identity = identity;
    }

    public ManagedProcessIdentity Identity { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Kill()
    {
        if (!HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public bool WaitForExit(TimeSpan timeout) =>
        _process.WaitForExit(checked((int)timeout.TotalMilliseconds));

    public void Dispose() => _process.Dispose();
}
