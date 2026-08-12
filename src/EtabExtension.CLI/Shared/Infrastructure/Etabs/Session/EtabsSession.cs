using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public interface IEtabsSession : IDisposable
{
    ETABSApplication GetOrStart();
    IManagedEtabsApplication GetOrStartOwned();
    bool IsStarted { get; }
    int? ProcessId { get; }
    ManagedEtabsShutdownResult Shutdown();
}

public sealed class EtabsSession : IEtabsSession
{
    private readonly IManagedEtabsLauncher _launcher;
    private readonly IProcessInspector _processes;
    private readonly ISessionRecordStore _records;
    private readonly IManagedEtabsShutdownMachine _shutdownMachine;
    private readonly object _gate = new();
    private IManagedEtabsApplication? _owned;
    private bool _ready;
    private EtabsLaunchException? _launchFailure;
    private ManagedEtabsShutdownResult? _shutdownResult;

    public EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records)
        : this(launcher, processes, records, new ManagedEtabsShutdownMachine(records))
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine)
    {
        _launcher = launcher;
        _processes = processes;
        _records = records;
        _shutdownMachine = shutdownMachine;
    }

    public bool IsStarted { get { lock (_gate) return _owned is not null && _ready; } }
    public int? ProcessId { get { lock (_gate) return _owned?.Identity.Pid; } }

    public ETABSApplication GetOrStart() => GetOrStartOwned().Application;

    public IManagedEtabsApplication GetOrStartOwned()
    {
        lock (_gate)
        {
            if (_launchFailure is not null)
            {
                throw _launchFailure;
            }

            if (_shutdownResult is not null)
            {
                throw new InvalidOperationException(
                    "Managed ETABS session has reached a terminal shutdown state.");
            }

            if (_owned is null)
            {
                Console.Error.WriteLine("ℹ Starting ETABS (hidden, shared serve session)...");
                var launched = _launcher.Launch();
                _owned = launched;
                try
                {
                    _records.Write(ToRecord(launched));
                }
                catch (Exception recordWriteException)
                {
                    var cleanup = _shutdownMachine
                        .ShutdownAfterRecoveryRecordWriteFailure(launched);
                    _shutdownResult = cleanup;
                    if (cleanup.Data.ProcessExitConfirmed)
                    {
                        _owned = null;
                    }
                    _ready = false;
                    _launchFailure = new EtabsLaunchException(
                        EtabsLaunchErrorCodes.RecoveryRecordWriteFailed,
                        WithTerminalFacts(
                            EtabsApiDiagnosticFormatter.InfrastructureException(
                                "ManagedEtabsSessionRecord.Write",
                                recordWriteException),
                            cleanup),
                        recordWriteException);
                    throw _launchFailure;
                }

                var initializationFailure = Initialize(launched);
                if (initializationFailure is not null)
                {
                    var cleanup = _shutdownMachine.Shutdown(launched);
                    _shutdownResult = cleanup;
                    if (cleanup.Data.ProcessExitConfirmed)
                    {
                        _owned = null;
                    }
                    _ready = false;
                    _launchFailure = new EtabsLaunchException(
                        EtabsLaunchErrorCodes.ModelInitializationFailed,
                        WithTerminalFacts(
                            initializationFailure.Value.Diagnostic,
                            cleanup),
                        initializationFailure.Value.Exception);
                    throw _launchFailure;
                }

                _ready = true;
                Console.Error.WriteLine($"✓ ETABS started hidden (PID {_owned.Identity.Pid})");
            }

            try
            {
                if (!_ready)
                {
                    throw new InvalidOperationException(
                        "Managed ETABS session is owned but not API-ready.");
                }
                Verify(_owned);
                return _owned;
            }
            catch
            {
                _shutdownResult = _shutdownMachine.Shutdown(_owned);
                if (_shutdownResult.Data.ProcessExitConfirmed)
                {
                    _owned = null;
                }
                _ready = false;
                throw;
            }
        }
    }

    private static (string Diagnostic, Exception? Exception)? Initialize(
        IManagedEtabsApplication owned)
    {
        try
        {
            var returnCode = owned.InitializeNewModel();
            return returnCode == 0
                ? null
                : (EtabsApiDiagnosticFormatter.ApiReturn(
                    "cSapModel.InitializeNewModel",
                    returnCode), null);
        }
        catch (Exception exception)
        {
            return (EtabsApiDiagnosticFormatter.Exception(
                "cSapModel.InitializeNewModel",
                exception), exception);
        }
    }

    private static string WithTerminalFacts(
        string diagnostic,
        ManagedEtabsShutdownResult cleanup) =>
        EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            diagnostic,
            $"state={cleanup.Data.State}; " +
            $"processExitConfirmed={cleanup.Data.ProcessExitConfirmed}; " +
            $"forced={cleanup.Data.Forced}; " +
            $"recordRetained={cleanup.Data.RecordRetained}");

    private void Verify(IManagedEtabsApplication owned)
    {
        var record = _records.Read();
        var live = _processes.Find(owned.Identity.Pid);
        if (record is null || live is null
            || record.ManagedLaunchRecordId != owned.ManagedLaunchRecordId
            || !OrphanSessionCleaner.IdentityMatches(record, live)
            || live != owned.Identity)
        {
            throw new InvalidOperationException(
                "Managed ETABS identity verification failed; a clean reopen is required.");
        }
    }

    public ManagedEtabsShutdownResult Shutdown()
    {
        lock (_gate)
        {
            if (_shutdownResult is not null)
            {
                return _shutdownResult;
            }

            if (_owned is null)
            {
                var record = _records.Read();
                _shutdownResult = record is null
                    ? NoOwnedProcessSuccess()
                    : NoOwnedProcessFailure(record);
                return _shutdownResult;
            }

            _shutdownResult = _shutdownMachine.Shutdown(_owned);
            if (_shutdownResult.Data.ProcessExitConfirmed)
            {
                _owned = null;
            }
            _ready = false;
            Console.Error.WriteLine("ℹ Shared ETABS session shut down.");
            return _shutdownResult;
        }
    }

    public void Dispose()
    {
        _ = Shutdown();
    }

    private static ManagedEtabsShutdownResult NoOwnedProcessSuccess() => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: false,
            RecordRetained: false,
            ApplicationExitReturnCode: null,
            OwnedPid: null));

    private static ManagedEtabsShutdownResult NoOwnedProcessFailure(
        ManagedEtabsSessionRecord record) => new(
        false,
        ManagedEtabsShutdownErrorCodes.IdentityMismatch,
        "A managed ETABS recovery record exists, but this session has no authoritative process handle.",
        new(
            ManagedEtabsShutdownState.IdentityMismatch,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: record.Pid));

    private static ManagedEtabsSessionRecord ToRecord(IManagedEtabsApplication owned) => new(
        1,
        owned.Identity.Pid,
        owned.Identity.ProcessStartTimeUtc,
        Path.GetFullPath(owned.Identity.ExecutablePath),
        owned.ManagedLaunchRecordId,
        DateTimeOffset.UtcNow);
}
