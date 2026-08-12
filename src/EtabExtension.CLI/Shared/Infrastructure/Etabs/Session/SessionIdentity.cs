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

public sealed class ManagedEtabsLauncher : IManagedEtabsLauncher
{
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AttachRetryInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProcessInspector _processes;
    private readonly IEtabsExecutableResolver _executableResolver;
    private readonly IEtabsProcessStarter _processStarter;
    private readonly IManagedEtabsConnector _connector;
    private readonly IEtabsLaunchClock _clock;
    private readonly TextWriter _diagnostics;

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IConfiguration configuration) : this(
            processes,
            new EtabsExecutableResolver(configuration, new WindowsEtabsInstallDiscovery()),
            new WindowsEtabsProcessStarter(),
            new EtabSharpManagedEtabsConnector(),
            new SystemEtabsLaunchClock(),
            Console.Error)
    {
    }

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IEtabsExecutableResolver executableResolver,
        IEtabsProcessStarter processStarter,
        IManagedEtabsConnector connector,
        IEtabsLaunchClock clock,
        TextWriter diagnostics)
    {
        _processes = processes;
        _executableResolver = executableResolver;
        _processStarter = processStarter;
        _connector = connector;
        _clock = clock;
        _diagnostics = diagnostics;
    }

    public IManagedEtabsApplication Launch()
    {
        CaptureCrossCheckBaseline();
        var executablePath = _executableResolver.Resolve();
        IOwnedEtabsProcess? ownedProcess = null;
        try
        {
            ownedProcess = _processStarter.Start(executablePath);
            var launchRecordId = Guid.NewGuid();
            var deadline = _clock.UtcNow + AttachTimeout;
            string? lastError = null;

            // TODO(issue #238 live certification): Verify that a plainly started ETABS.exe
            // accepts ConnectToProcess before any model is open and measure readiness latency.
            while (_clock.UtcNow < deadline)
            {
                var managed = _connector.TryConnect(ownedProcess, launchRecordId, out lastError);
                if (managed is not null)
                {
                    var transferredProcess = ownedProcess
                        ?? throw new InvalidOperationException(
                            "Managed ETABS connector returned without an owned process handle");
                    try
                    {
                        VerifyPostLaunchOwnership(managed.Identity.Pid);
                        ownedProcess = null; // ownership transferred after verification
                        return managed;
                    }
                    catch
                    {
                        CleanUpManagedApplication(managed, transferredProcess);
                        ownedProcess = null; // managed cleanup retains or releases the transferred handle
                        throw;
                    }
                }

                if (ownedProcess.HasExited)
                {
                    lastError = "The owned ETABS process exited before COM attach succeeded.";
                    break;
                }

                _clock.Sleep(AttachRetryInterval);
            }

            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.AttachTimeout,
                $"ETABS process PID {ownedProcess.Identity.Pid} did not accept ConnectToProcess within {AttachTimeout.TotalSeconds:0} seconds. Last error: {lastError ?? "none"}");
        }
        catch
        {
            if (ownedProcess is not null)
            {
                CleanUpOwnedProcess(ownedProcess);
            }

            throw;
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

    private void VerifyPostLaunchOwnership(int ownedPid)
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
                $"Could not verify exclusive ownership after starting managed ETABS PID {ownedPid}: {ex.Message}",
                ex);
        }

        if (observation.UnidentifiedCount == 0
            && observation.Identified.Count == 1
            && observation.Identified[0].Pid == ownedPid)
        {
            return;
        }

        var pids = observation.Identified
            .Select(identity => identity.Pid)
            .Distinct()
            .Order()
            .ToList();
        throw new EtabsLaunchException(
            EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance,
            "Managed ETABS lost exclusive ownership after launch: " +
            $"ownedPid={ownedPid}, observedPids=[{string.Join(", ", pids)}], " +
            $"unidentifiedCount={observation.UnidentifiedCount}.");
    }

    private void CleanUpManagedApplication(
        IManagedEtabsApplication managed,
        IOwnedEtabsProcess ownedProcess)
    {
        try
        {
            var exitReturnCode = managed.ExitWithoutSaving();
            if (exitReturnCode != 0)
            {
                _diagnostics.WriteLine(
                    $"⚠ ApplicationExit(false) returned {exitReturnCode} for managed ETABS PID {managed.Identity.Pid} after ownership failure.");
            }
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                $"⚠ Could not exit managed ETABS PID {managed.Identity.Pid} after ownership failure: {ex.Message}");
        }

        if (StopOwnedProcess(ownedProcess, "ownership failure"))
        {
            managed.ReleaseOwnedProcessHandle();
        }
    }

    private void CleanUpOwnedProcess(IOwnedEtabsProcess ownedProcess)
    {
        if (StopOwnedProcess(ownedProcess, "launch failure"))
        {
            ownedProcess.Dispose();
        }
    }

    private bool StopOwnedProcess(IOwnedEtabsProcess ownedProcess, string context)
    {
        try
        {
            if (ownedProcess.HasExited)
            {
                return true;
            }

            ownedProcess.Kill();
            var confirmed = ownedProcess.WaitForExit(TimeSpan.FromSeconds(10));
            if (!confirmed)
            {
                _diagnostics.WriteLine(
                    $"⚠ Timed out waiting for owned ETABS PID {ownedProcess.Identity.Pid} to exit after {context}.");
            }
            return confirmed;
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                $"⚠ Could not stop owned ETABS PID {ownedProcess.Identity.Pid} after {context}: {ex.Message}");
            return false;
        }
    }
}
