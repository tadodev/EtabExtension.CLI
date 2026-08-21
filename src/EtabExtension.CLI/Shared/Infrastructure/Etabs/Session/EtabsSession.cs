using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public interface IEtabsSession : IDisposable
{
    ETABSApplication GetOrStart();
    IManagedEtabsApplication GetOrStartOwned();
    bool IsStarted { get; }
    int? ProcessId { get; }

    /// <summary>
    /// Shows the managed ETABS window because the USER asked to see it.
    ///
    /// <para>The session is created hidden and stays hidden for every background
    /// command, so this is the only way it ever reaches the screen. Callers must invoke
    /// it only after the requested model is confirmed open — revealing before that is
    /// exactly the blank <c>(Untitled)</c> window CLI #22 exists to remove.</para>
    /// </summary>
    Result RevealForExplicitUserRequest();

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

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _owned is not null && _ready;
            }
        }
    }
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
                Console.Error.WriteLine("ℹ Starting ETABS (shared serve session)...");
                var launched = Launch();
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

                var initializationFailure = Initialize(launched)
                    ?? CompleteReadiness(launched);
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
                Console.Error.WriteLine($"✓ ETABS started (PID {_owned.Identity.Pid})");
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

    /// <summary>
    /// Launches, and caches an unresolved launch cleanup as terminal state.
    ///
    /// <para>When a failed launch could not prove that the process it started is gone,
    /// there is no owned handle and no recovery record to describe it. Without caching,
    /// the next request would relaunch on top of it and a later shutdown would report
    /// success with <c>processExitConfirmed=true</c> — a clean answer about a process
    /// nobody resolved.</para>
    /// </summary>
    private IManagedEtabsApplication Launch()
    {
        try
        {
            return _launcher.Launch();
        }
        catch (EtabsLaunchException failure) when (failure.Cleanup is { Success: false })
        {
            _shutdownResult = failure.Cleanup;
            _launchFailure = failure;
            _ready = false;
            throw;
        }
    }

    /// <summary>
    /// Wraps the same started object with EtabSharp, only after initialization returned
    /// zero. A wrap failure is a launch failure: the session must not be exposed holding a
    /// handle nothing can use.
    /// </summary>
    private static (string Diagnostic, Exception? Exception)? CompleteReadiness(
        IManagedEtabsApplication owned)
    {
        try
        {
            owned.CompleteApiReadiness();
            return null;
        }
        catch (Exception exception)
        {
            return (EtabsApiDiagnosticFormatter.InfrastructureException(
                "ETABSWrapper.WrapExisting",
                exception), exception);
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

    /// <inheritdoc />
    public Result RevealForExplicitUserRequest() =>
        Result.Fail("Managed ETABS reveal is not implemented yet.");

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
