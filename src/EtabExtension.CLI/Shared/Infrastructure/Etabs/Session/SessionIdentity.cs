using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtabExtension.CLI.Features.GetStatus.Models;
using Microsoft.Extensions.Configuration;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public sealed record ManagedProcessIdentity(
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath);

public sealed record EtabsProcessObservation(
    IReadOnlyList<ManagedProcessIdentity> Identified,
    int UnidentifiedCount);

public static class EtabsOwnershipResolver
{
    public static EtabsInstanceOwnership Resolve(
        EtabsProcessObservation observation,
        int? managedPid)
    {
        if (observation.UnidentifiedCount > 0 || observation.Identified.Count > 1)
        {
            return EtabsInstanceOwnership.Ambiguous;
        }

        if (observation.Identified.Count == 0)
        {
            return EtabsInstanceOwnership.None;
        }

        if (!managedPid.HasValue)
        {
            return EtabsInstanceOwnership.External;
        }

        return observation.Identified[0].Pid == managedPid.Value
            ? EtabsInstanceOwnership.Managed
            : EtabsInstanceOwnership.Ambiguous;
    }
}

public sealed record ManagedEtabsSessionRecord(
    int SchemaVersion,
    int Pid,
    DateTimeOffset ProcessStartTimeUtc,
    string ExecutablePath,
    Guid ManagedLaunchRecordId,
    DateTimeOffset CreatedAtUtc);

public interface ISessionRecordStore
{
    /// <summary>
    /// Where the recovery record lives. Reported in the startup refusal frame so a
    /// consumer can name the retained evidence instead of guessing at it.
    /// </summary>
    string FilePath { get; }

    ManagedEtabsSessionRecord? Read();
    void Write(ManagedEtabsSessionRecord record);
    void Clear();
}

public sealed class JsonSessionRecordStore : ISessionRecordStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EtabExtension", "sidecar", "managed-etabs-session.json");

    public JsonSessionRecordStore(string? path = null) => FilePath = path ?? DefaultPath;

    public string FilePath { get; }

    public ManagedEtabsSessionRecord? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ManagedEtabsSessionRecord>(
                File.ReadAllText(FilePath), Options);
            return record is { SchemaVersion: 1 }
                && record.ManagedLaunchRecordId != Guid.Empty
                && record.Pid > 0
                && !string.IsNullOrWhiteSpace(record.ExecutablePath)
                ? record
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"⚠ Invalid managed ETABS session record: {ex.Message}");
            return null;
        }
    }

    public void Write(ManagedEtabsSessionRecord record)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
        File.Move(temp, FilePath, true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            var temp = FilePath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"⚠ Could not clear managed ETABS session record: {ex.Message}");
        }
    }
}

public interface IProcessInspector
{
    EtabsProcessObservation ObserveEtabs();
    ManagedProcessIdentity? Find(int pid);

    /// <summary>
    /// Opens an authoritative handle on the process matching <paramref name="expected"/>
    /// exactly — pid, start time and executable path. Returns null when the live process
    /// does not match, so ownership is never assumed from a pid alone.
    /// </summary>
    IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected);

    ExactProcessTerminationResult TerminateExact(
        ManagedProcessIdentity expected,
        TimeSpan timeout);
}

public enum ExactProcessTerminationState
{
    NotFound,
    ConfirmedGone,
    IdentityMismatchOrUnidentified,
    ExitUnconfirmed
}

public sealed record ExactProcessTerminationResult(
    ExactProcessTerminationState State,
    ManagedProcessIdentity? ObservedIdentity);

public sealed class WindowsProcessInspector : IProcessInspector
{
    public EtabsProcessObservation ObserveEtabs()
    {
        var identified = new List<ManagedProcessIdentity>();
        var unidentifiedCount = 0;
        foreach (var process in Process.GetProcessesByName("ETABS"))
        {
            using (process)
            {
                var identity = TryRead(process);
                if (identity is null)
                {
                    unidentifiedCount++;
                }
                else
                {
                    identified.Add(identity);
                }
            }
        }

        return new(identified, unidentifiedCount);
    }

    public ManagedProcessIdentity? Find(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return TryRead(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    public IOwnedEtabsProcess? OpenExact(ManagedProcessIdentity expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        Process process;
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }

        var observed = TryRead(process);
        if (observed is null || !IdentityMatches(expected, observed))
        {
            process.Dispose();
            return null;
        }

        return new WindowsOwnedEtabsProcess(process, observed);
    }

    public ExactProcessTerminationResult TerminateExact(
        ManagedProcessIdentity expected,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(expected);

        Process process;
        try
        {
            process = Process.GetProcessById(expected.Pid);
        }
        catch (ArgumentException)
        {
            return new(ExactProcessTerminationState.NotFound, ObservedIdentity: null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new(
                ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                ObservedIdentity: null);
        }

        using (process)
        {
            var observed = TryRead(process);
            if (observed is null || !IdentityMatches(expected, observed))
            {
                return new(
                    ExactProcessTerminationState.IdentityMismatchOrUnidentified,
                    observed);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                return process.HasExited
                    || process.WaitForExit(checked((int)timeout.TotalMilliseconds))
                    ? new(ExactProcessTerminationState.ConfirmedGone, observed)
                    : new(ExactProcessTerminationState.ExitUnconfirmed, observed);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return HasExited(process)
                    ? new(ExactProcessTerminationState.ConfirmedGone, observed)
                    : new(ExactProcessTerminationState.ExitUnconfirmed, observed);
            }
        }
    }

    private static ManagedProcessIdentity? TryRead(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path)
                ? null
                : new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        { return null; }
    }

    private static bool IdentityMatches(
        ManagedProcessIdentity expected,
        ManagedProcessIdentity observed) =>
        expected.Pid == observed.Pid
        && expected.ProcessStartTimeUtc.ToUniversalTime()
            == observed.ProcessStartTimeUtc.ToUniversalTime()
        && string.Equals(
            Path.GetFullPath(expected.ExecutablePath),
            Path.GetFullPath(observed.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        { return false; }
    }
}

public interface IOrphanSessionCleaner
{
    ManagedEtabsShutdownResult Clean();
}

public sealed class OrphanSessionCleaner(
    ISessionRecordStore records,
    IProcessInspector processes) : IOrphanSessionCleaner
{
    public ManagedEtabsShutdownResult Clean()
    {
        var record = records.Read();
        if (record is null)
        {
            records.Clear();
            return Succeeded(ownedPid: null, forced: false);
        }

        var expected = new ManagedProcessIdentity(
            record.Pid,
            record.ProcessStartTimeUtc,
            record.ExecutablePath);
        var termination = processes.TerminateExact(expected, TimeSpan.FromSeconds(10));
        switch (termination.State)
        {
            case ExactProcessTerminationState.NotFound:
                records.Clear();
                return Succeeded(record.Pid, forced: false);
            case ExactProcessTerminationState.ConfirmedGone:
                records.Clear();
                return Succeeded(record.Pid, forced: true);
            case ExactProcessTerminationState.IdentityMismatchOrUnidentified:
                return Failed(
                    ManagedEtabsShutdownState.IdentityMismatch,
                    ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                    "Managed ETABS orphan identity was mismatched or unreadable; process was not targeted.",
                    record.Pid,
                    forced: false);
            case ExactProcessTerminationState.ExitUnconfirmed:
                return Failed(
                    ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                    ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                    "Exact-handle orphan termination did not confirm process exit.",
                    record.Pid,
                    forced: true);
            default:
                throw new InvalidOperationException(
                    $"Unknown exact process termination state: {termination.State}.");
        }
    }

    private static ManagedEtabsShutdownResult Succeeded(int? ownedPid, bool forced) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    private static ManagedEtabsShutdownResult Failed(
        ManagedEtabsShutdownState state,
        string errorCode,
        string error,
        int ownedPid,
        bool forced) => new(
        false,
        errorCode,
        error,
        new(
            state,
            ProcessExitConfirmed: false,
            Forced: forced,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    internal static bool IdentityMatches(ManagedEtabsSessionRecord record, ManagedProcessIdentity live) =>
        record.Pid == live.Pid
        && record.ProcessStartTimeUtc.ToUniversalTime() == live.ProcessStartTimeUtc.ToUniversalTime()
        && string.Equals(Path.GetFullPath(record.ExecutablePath), Path.GetFullPath(live.ExecutablePath), StringComparison.OrdinalIgnoreCase);
}

public interface IManagedEtabsLauncher
{
    IManagedEtabsApplication Launch();
}
/// <summary>
/// Starts and takes exclusive ownership of one managed ETABS instance through the raw
/// CSI lifecycle documented for ETABS 23.3.
///
/// <para>Sequence: preflight census (no external or ambiguous instance) →
/// <c>cHelper.CreateObject(exact ETABS.exe)</c>, which starts the program →
/// <c>cOAPI.ApplicationStart()</c> exactly once, requiring zero → <c>cOAPI.SapModel</c>
/// present → an exact OS census proving exactly one ETABS process, captured as pid +
/// start time UTC + executable path with an authoritative handle.</para>
///
/// <para>The returned application is owned but <b>not yet API-ready</b>: the caller writes
/// the recovery record and calls <c>InitializeNewModel</c> before exposing it. Nothing here
/// starts a process out of band, attaches by pid, falls back to the ROT, hides a window, or
/// sleeps waiting for readiness — the previous path did all four and still could not prove
/// that the object it attached to was usable.</para>
/// </summary>
public sealed class ManagedEtabsLauncher : IManagedEtabsLauncher
{
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessInspector _processes;
    private readonly IEtabsExecutableResolver _executableResolver;
    private readonly IEtabsRawApiFactory _apiFactory;
    private readonly IEtabsVersionProbe _versionProbe;
    private readonly TextWriter _diagnostics;

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IConfiguration configuration) : this(
            processes,
            new EtabsExecutableResolver(configuration, new WindowsEtabsInstallDiscovery()),
            new EtabsRawApiFactory(),
            new FileEtabsVersionProbe(),
            Console.Error)
    {
    }

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IEtabsExecutableResolver executableResolver,
        IEtabsRawApiFactory apiFactory,
        IEtabsVersionProbe versionProbe,
        TextWriter diagnostics)
    {
        _processes = processes;
        _executableResolver = executableResolver;
        _apiFactory = apiFactory;
        _versionProbe = versionProbe;
        _diagnostics = diagnostics;
    }

    public IManagedEtabsApplication Launch()
    {
        CaptureCrossCheckBaseline();
        var executablePath = _executableResolver.Resolve();

        // CreateObject starts the program, so from here a failure may have left a process
        // behind and every exit path must resolve it.
        var rawApi = _apiFactory.CreateFromExecutable(executablePath);
        IOwnedEtabsProcess? ownedProcess = null;
        try
        {
            StartApplication(rawApi);
            RequireSapModel(rawApi);

            var identity = CensusExactlyOneOwnedProcess();
            ownedProcess = _processes.OpenExact(identity)
                ?? throw new EtabsLaunchException(
                    EtabsLaunchErrorCodes.ProcessIdentityFailed,
                    "Could not open an authoritative handle on the started ETABS process " +
                    $"pid={identity.Pid}; its live identity no longer matches the census.");

            var version = ReadVersion(identity.ExecutablePath);
            var managed = new ManagedEtabsApplication(
                rawApi,
                identity,
                Guid.NewGuid(),
                ownedProcess,
                new ManagedEtabsApiVersion(
                    version.MajorVersion,
                    ReadApiVersion(rawApi),
                    version.FullVersion));
            ownedProcess = null; // ownership transferred to the managed application
            return managed;
        }
        catch
        {
            CleanUpFailedStart(rawApi, ownedProcess);
            throw;
        }
    }

    private static void StartApplication(IEtabsRawApi rawApi)
    {
        int returnCode;
        try
        {
            returnCode = rawApi.ApplicationStart();
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApplicationStartFailed,
                EtabsApiDiagnosticFormatter.Exception("cOAPI.ApplicationStart", ex),
                ex);
        }

        if (returnCode != 0)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApplicationStartFailed,
                EtabsApiDiagnosticFormatter.ApiReturn("cOAPI.ApplicationStart", returnCode));
        }
    }

    private static void RequireSapModel(IEtabsRawApi rawApi)
    {
        bool available;
        try
        {
            available = rawApi.HasSapModel;
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiModelUnavailable,
                EtabsApiDiagnosticFormatter.Exception("cOAPI.SapModel", ex),
                ex);
        }

        if (!available)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ApiModelUnavailable,
                "cOAPI.SapModel was unavailable on the started application object.");
        }
    }

    private (int MajorVersion, string FullVersion) ReadVersion(string executablePath)
    {
        try
        {
            return _versionProbe.Read(executablePath);
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ProcessIdentityFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "FileVersionInfo.GetVersionInfo(ETABS.exe)",
                    ex),
                ex);
        }
    }

    private double ReadApiVersion(IEtabsRawApi rawApi)
    {
        // Wrap metadata only. A version that cannot be read is reported as 0 rather than
        // failing a session whose lifecycle is otherwise proven.
        try
        {
            return rawApi.GetOapiVersionNumber();
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.Exception("cHelper.GetOAPIVersionNumber", ex));
            return 0;
        }
    }

    private void CaptureCrossCheckBaseline()
    {
        EtabsProcessObservation observation;
        try
        {
            observation = _processes.ObserveEtabs();
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
                $"Could not verify that ETABS is absent before launch: {ex.Message}",
                ex);
        }

        if (observation.Identified.Count > 0 || observation.UnidentifiedCount > 0)
        {
            var pids = observation.Identified
                .Select(identity => identity.Pid)
                .Distinct()
                .Order()
                .ToList();
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
                "Refusing to start managed ETABS because an external or ambiguous " +
                $"instance already exists: observedPids=[{string.Join(", ", pids)}], " +
                $"unidentifiedCount={observation.UnidentifiedCount}.");
        }
    }

    /// <summary>
    /// Preflight proved zero ETABS processes, so exactly one identified process after
    /// CreateObject is ours by construction. Anything else — none, several, or one that
    /// could not be identified — fails closed.
    /// </summary>
    private ManagedProcessIdentity CensusExactlyOneOwnedProcess()
    {
        EtabsProcessObservation observation;
        try
        {
            observation = _processes.ObserveEtabs();
        }
        catch (Exception ex)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
                $"Could not verify exclusive ownership after starting managed ETABS: {ex.Message}",
                ex);
        }

        if (observation.UnidentifiedCount == 0 && observation.Identified.Count == 1)
        {
            return observation.Identified[0];
        }

        var pids = observation.Identified
            .Select(identity => identity.Pid)
            .Distinct()
            .Order()
            .ToList();
        throw new EtabsLaunchException(
            EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
            "Managed ETABS did not resolve to exactly one owned process: " +
            $"observedPids=[{string.Join(", ", pids)}], " +
            $"unidentifiedCount={observation.UnidentifiedCount}.");
    }

    /// <summary>
    /// Resolves a process that CreateObject may have started. The raw exit is requested
    /// first; termination happens only against an identity this launcher proved, never
    /// against an ambiguous or unreadable one.
    /// </summary>
    private void CleanUpFailedStart(IEtabsRawApi rawApi, IOwnedEtabsProcess? ownedProcess)
    {
        try
        {
            var returnCode = rawApi.ApplicationExit(false);
            if (returnCode != 0)
            {
                _diagnostics.WriteLine(
                    EtabsApiDiagnosticFormatter.ApiReturn("cOAPI.ApplicationExit(false)", returnCode));
            }
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.Exception("cOAPI.ApplicationExit(false)", ex));
        }

        if (ownedProcess is not null)
        {
            StopOwnedProcess(ownedProcess);
            ownedProcess.Dispose();
            return;
        }

        TerminateProvenSurvivor();
    }

    private void TerminateProvenSurvivor()
    {
        EtabsProcessObservation observation;
        try
        {
            observation = _processes.ObserveEtabs();
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "IProcessInspector.ObserveEtabs",
                    ex));
            return;
        }

        if (observation.Identified.Count == 0 && observation.UnidentifiedCount == 0)
        {
            return;
        }

        if (observation.UnidentifiedCount > 0 || observation.Identified.Count != 1)
        {
            // Ownership cannot be proven, so nothing is terminated.
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                "Managed ETABS start failed and ownership of the surviving ETABS process could not be proven; nothing was terminated.",
                $"identifiedCount={observation.Identified.Count}; unidentifiedCount={observation.UnidentifiedCount}"));
            return;
        }

        var termination = _processes.TerminateExact(observation.Identified[0], ForcedExitTimeout);
        if (termination.State is not (ExactProcessTerminationState.ConfirmedGone
            or ExactProcessTerminationState.NotFound))
        {
            _diagnostics.WriteLine(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                "Managed ETABS start failed and the started process could not be confirmed gone.",
                $"state={termination.State}; pid={observation.Identified[0].Pid}"));
        }
    }

    private void StopOwnedProcess(IOwnedEtabsProcess ownedProcess)
    {
        try
        {
            if (ownedProcess.HasExited)
            {
                return;
            }

            ownedProcess.Kill();
            if (!ownedProcess.WaitForExit(ForcedExitTimeout))
            {
                _diagnostics.WriteLine(
                    $"⚠ Timed out waiting for owned ETABS PID {ownedProcess.Identity.Pid} to exit after launch failure.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                $"⚠ Could not stop owned ETABS PID {ownedProcess.Identity.Pid} after launch failure: {ex.Message}");
        }
    }
}
