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
    public const string ProcessStartFailed = "ETABS_PROCESS_START_FAILED";
    public const string ProcessIdentityFailed = "ETABS_PROCESS_IDENTITY_FAILED";
    public const string AttachTimeout = "ETABS_ATTACH_TIMEOUT";
    public const string ExternalOrAmbiguousInstance = "ETABS_EXTERNAL_OR_AMBIGUOUS_INSTANCE";
    public const string ModelInitializationFailed = "ETABS_MODEL_INITIALIZATION_FAILED";
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

public interface IEtabsProcessStarter
{
    IOwnedEtabsProcess Start(string executablePath);
}

public interface IManagedEtabsApplication : IDisposable
{
    ETABSApplication Application { get; }
    ManagedProcessIdentity Identity { get; }
    Guid ManagedLaunchRecordId { get; }
    int InitializeNewModel();
    int ExitWithoutSaving();
    bool HasExited { get; }
    bool WaitForExit(TimeSpan timeout);
    void Kill();
}

public sealed class ManagedEtabsApplication(
    ETABSApplication application,
    ManagedProcessIdentity identity,
    Guid launchRecordId,
    IOwnedEtabsProcess ownedProcess) : IManagedEtabsApplication
{
    public ETABSApplication Application { get; } = application;
    public ManagedProcessIdentity Identity { get; } = identity;
    public Guid ManagedLaunchRecordId { get; } = launchRecordId;
    public bool HasExited => ownedProcess.HasExited;

    public int InitializeNewModel() =>
        Application.Model.ModelInfo.InitializeNewModel(eUnits.kip_in_F);

    public int ExitWithoutSaving() =>
        Application.Application.ApplicationExit(false);

    public bool WaitForExit(TimeSpan timeout) => ownedProcess.WaitForExit(timeout);

    public void Kill() => ownedProcess.Kill();

    public void Dispose()
    {
        try
        {
            Application.Dispose();
        }
        finally
        {
            ownedProcess.Dispose();
        }
    }
}

public sealed class WindowsEtabsProcessStarter : IEtabsProcessStarter
{
    public IOwnedEtabsProcess Start(string executablePath)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessStartFailed,
                $"Could not start ETABS executable '{executablePath}': {ex.Message}",
                ex);
        }

        try
        {
            return new WindowsOwnedEtabsProcess(process);
        }
        catch (Exception ex)
        {
            TryKillAndDispose(process);
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessIdentityFailed,
                $"Started ETABS but could not capture PID/start time/executable path from the owned process: {ex.Message}",
                ex);
        }
    }

    private static void TryKillAndDispose(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Console.Error.WriteLine($"⚠ Could not clean up ETABS after identity capture failure: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class WindowsOwnedEtabsProcess : IOwnedEtabsProcess
    {
        private readonly Process _process;

        public WindowsOwnedEtabsProcess(Process process)
        {
            _process = process;
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException("Process.MainModule.FileName was unavailable.");
            }

            Identity = new(
                process.Id,
                process.StartTime.ToUniversalTime(),
                Path.GetFullPath(executablePath));
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
}

public interface IManagedEtabsConnector
{
    IManagedEtabsApplication? TryConnect(
        IOwnedEtabsProcess process,
        Guid launchRecordId,
        out string? error);
}

public sealed class EtabSharpManagedEtabsConnector : IManagedEtabsConnector
{
    public IManagedEtabsApplication? TryConnect(
        IOwnedEtabsProcess process,
        Guid launchRecordId,
        out string? error)
    {
        ETABSApplication? application = null;
        try
        {
            application = ETABSWrapper.ConnectToProcess(process.Identity.Pid);
            if (application is null)
            {
                error = "ETABSWrapper.ConnectToProcess returned null.";
                return null;
            }

            if (application.Application.Visible())
            {
                application.Application.Hide();
            }

            error = null;
            var managed = new ManagedEtabsApplication(
                application,
                process.Identity,
                launchRecordId,
                process);
            application = null;
            return managed;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            application?.Dispose();
        }
    }
}

public interface IEtabsLaunchClock
{
    DateTimeOffset UtcNow { get; }
    void Sleep(TimeSpan duration);
}

public sealed class SystemEtabsLaunchClock : IEtabsLaunchClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}
