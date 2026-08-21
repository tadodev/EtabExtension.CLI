using System.Diagnostics.CodeAnalysis;
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

    // Borrowed, never owned: this is Console.Error in production and a test's buffer under
    // test, so the session must not close it when it disposes.
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The diagnostics writer is owned by the caller, not by the session.")]
    private readonly TextWriter _diagnostics;
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
        : this(launcher, processes, records, shutdownMachine, Console.Error)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        TextWriter diagnostics)
    {
        _launcher = launcher;
        _processes = processes;
        _records = records;
        _shutdownMachine = shutdownMachine;
        _diagnostics = diagnostics;
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
                _diagnostics.WriteLine("ℹ Starting ETABS (shared serve session)...");
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

                // Readiness and hidden state are one gate, not two. The candidate #20
                // rejected treated the hide as advisory and printed a "started hidden"
                // success line straight after failing to confirm it; here an unconfirmed
                // hidden state ends the session on the same path a failed
                // InitializeNewModel does, so that line can only ever follow a proof.
                var creationFailure = Initialize(launched)
                    ?? CompleteReadiness(launched)
                    ?? ConfirmHiddenForBackgroundWork(launched);
                if (creationFailure is not null)
                {
                    var cleanup = _shutdownMachine.Shutdown(launched);
                    _shutdownResult = cleanup;
                    if (cleanup.Data.ProcessExitConfirmed)
                    {
                        _owned = null;
                    }
                    _ready = false;
                    _launchFailure = new EtabsLaunchException(
                        creationFailure.Value.Code,
                        WithTerminalFacts(
                            creationFailure.Value.Diagnostic,
                            cleanup),
                        creationFailure.Value.Exception);
                    throw _launchFailure;
                }

                _ready = true;
                _diagnostics.WriteLine($"✓ ETABS started hidden (PID {_owned.Identity.Pid})");
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
    private static (string Code, string Diagnostic, Exception? Exception)? CompleteReadiness(
        IManagedEtabsApplication owned)
    {
        try
        {
            owned.CompleteApiReadiness();
            return null;
        }
        catch (Exception exception)
        {
            return (
                EtabsLaunchErrorCodes.ModelInitializationFailed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "ETABSWrapper.WrapExisting",
                    exception),
                exception);
        }
    }

    private static (string Code, string Diagnostic, Exception? Exception)? Initialize(
        IManagedEtabsApplication owned)
    {
        try
        {
            var returnCode = owned.InitializeNewModel();
            return returnCode == 0
                ? null
                : (
                    EtabsLaunchErrorCodes.ModelInitializationFailed,
                    EtabsApiDiagnosticFormatter.ApiReturn(
                        "cSapModel.InitializeNewModel",
                        returnCode),
                    (Exception?)null);
        }
        catch (Exception exception)
        {
            return (
                EtabsLaunchErrorCodes.ModelInitializationFailed,
                EtabsApiDiagnosticFormatter.Exception(
                    "cSapModel.InitializeNewModel",
                    exception),
                exception);
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

    /// <summary>
    /// The second and last hide of a session's life, taken once
    /// <c>InitializeNewModel</c> has produced the blank model and the EtabSharp wrap
    /// exists, and still before the application is handed to any command.
    ///
    /// <para>The launcher already hid the application at <c>ApplicationStart</c>. This is
    /// not redundant: ETABS finishes building its UI well after the start call returns —
    /// the #20 timeline shows the window arriving 5.15 s after process creation — and
    /// Cardex documents nothing about when. A second read costs one CSI call and covers
    /// the case where the first hide found nothing to hide.</para>
    ///
    /// <para><b>Failure is terminal.</b> An unconfirmed hidden state returns a launch
    /// failure rather than a warning, and the caller tears the session down through the
    /// same cleanup a failed initialization uses — the authoritative owned process is
    /// exited, not left running with an unproven window. Warning and continuing is exactly
    /// what #20 measured and rejected.</para>
    ///
    /// <para>It runs ONLY while the session is being created. That is the whole reason a
    /// background command can safely reuse a session the user asked to see: nothing on the
    /// command path ever hides anything.</para>
    /// </summary>
    private (string Code, string Diagnostic, Exception? Exception)? ConfirmHiddenForBackgroundWork(
        IManagedEtabsApplication owned)
    {
        var outcome = owned.EnsureHiddenForBackgroundWork();
        if (!outcome.Confirmed)
        {
            return (
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                outcome.Diagnostic
                    ?? "Managed ETABS could not be confirmed hidden before use.",
                (Exception?)null);
        }

        // Paired with the launcher's line, this says which of the two hides did the work
        // and how hard CSI had to be waited on for it.
        _diagnostics.WriteLine(outcome.Changed
            ? "ℹ ETABS hidden before use (a window appeared during model initialization; " +
                $"reads={outcome.Reads}, waitedMs={(long)outcome.Waited.TotalMilliseconds})."
            : "ℹ ETABS was already hidden before use.");
        return null;
    }

    /// <inheritdoc />
    public Result RevealForExplicitUserRequest()
    {
        lock (_gate)
        {
            var owned = GetOrStartOwned();

            // Order is the contract. The caller has already confirmed the requested model
            // is open; the background window suppression is retired FIRST — permanently,
            // and putting back exactly the windows it hid — and only then is the CSI
            // visible transition issued. Releasing after the transition would leave the
            // guard free to take the engineer's window straight back down, and there is no
            // path that re-arms it afterwards.
            owned.ReleaseWindowGuardForExplicitUserAction();

            var outcome = owned.EnsureVisibleForExplicitUserAction();
            if (!outcome.Confirmed)
            {
                return Result.Fail(outcome.Diagnostic
                    ?? "Managed ETABS could not be confirmed visible.");
            }

            if (outcome.Changed)
            {
                _diagnostics.WriteLine($"✓ ETABS shown (PID {owned.Identity.Pid})");
            }

            return Result.Ok();
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
            _diagnostics.WriteLine("ℹ Shared ETABS session shut down.");
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
