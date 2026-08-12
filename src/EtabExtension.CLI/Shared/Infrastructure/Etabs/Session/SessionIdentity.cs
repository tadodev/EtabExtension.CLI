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
    void Terminate(int pid);
    bool WaitForExit(int pid, TimeSpan timeout);
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    public EtabsProcessObservation ObserveEtabs()
    {
        var identified = new List<ManagedProcessIdentity>();
        var unidentifiedCount = 0;
        foreach (var process in Process.GetProcessesByName("ETABS"))
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

        return new(identified, unidentifiedCount);
    }

    public ManagedProcessIdentity? Find(int pid)
    {
        try { return TryRead(Process.GetProcessById(pid)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    public void Terminate(int pid)
    {
        // The target may exit between identity verification and this call;
        // an already-gone process is a successful termination, not a crash.
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        try { return Process.GetProcessById(pid).WaitForExit((int)timeout.TotalMilliseconds); }
        catch (ArgumentException) { return true; }
    }

    private static ManagedProcessIdentity? TryRead(Process process)
    {
        using (process)
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

        var live = processes.Find(record.Pid);
        if (live is null)
        {
            records.Clear();
            return Succeeded(record.Pid, forced: false);
        }

        if (!IdentityMatches(record, live))
        {
            return Failed(
                ManagedEtabsShutdownState.IdentityMismatch,
                ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                "Managed ETABS orphan identity tuple did not match; process was not targeted.",
                record.Pid,
                forced: false);
        }

        Console.Error.WriteLine(
            $"⚠ Managed ETABS orphan detected (PID {record.Pid}, launch {record.ManagedLaunchRecordId}). " +
            "Unsaved state is untrusted; terminating it. A clean reopen is required.");
        processes.Terminate(record.Pid);
        var exitConfirmed = processes.WaitForExit(record.Pid, TimeSpan.FromSeconds(10));
        if (!exitConfirmed)
            Console.Error.WriteLine($"⚠ Timed out waiting for managed ETABS orphan PID {record.Pid} to exit.");

        if (!exitConfirmed)
        {
            return Failed(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                "Exact-identity orphan termination did not confirm process exit.",
                record.Pid,
                forced: true);
        }

        records.Clear();
        return Succeeded(record.Pid, forced: true);
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
                        ownedProcess = null; // managed cleanup disposed the transferred handle
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

        StopOwnedProcess(ownedProcess, "ownership failure");

        try
        {
            managed.Dispose();
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                $"⚠ Could not dispose managed ETABS PID {managed.Identity.Pid} after ownership failure: {ex.Message}");
        }
    }

    private void CleanUpOwnedProcess(IOwnedEtabsProcess ownedProcess)
    {
        try
        {
            StopOwnedProcess(ownedProcess, "launch failure");
        }
        finally
        {
            ownedProcess.Dispose();
        }
    }

    private void StopOwnedProcess(IOwnedEtabsProcess ownedProcess, string context)
    {
        try
        {
            if (!ownedProcess.HasExited)
            {
                ownedProcess.Kill();
                if (!ownedProcess.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    _diagnostics.WriteLine(
                        $"⚠ Timed out waiting for owned ETABS PID {ownedProcess.Identity.Pid} to exit after {context}.");
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                $"⚠ Could not stop owned ETABS PID {ownedProcess.Identity.Pid} after {context}: {ex.Message}");
        }
    }
}
