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
    private readonly IManagedEtabsStartIntentScope _startIntent;

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
        ISessionRecordStore records,
        IManagedEtabsStartIntentScope startIntent)
        : this(launcher, processes, records, new ManagedEtabsShutdownMachine(records), startIntent)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        IManagedEtabsStartIntentScope startIntent)
        : this(launcher, processes, records, shutdownMachine, startIntent, Console.Error)
    {
    }

    internal EtabsSession(
        IManagedEtabsLauncher launcher,
        IProcessInspector processes,
        ISessionRecordStore records,
        IManagedEtabsShutdownMachine shutdownMachine,
        IManagedEtabsStartIntentScope startIntent,
        TextWriter diagnostics)
    {
        _launcher = launcher;
        _processes = processes;
        _records = records;
        _shutdownMachine = shutdownMachine;
        _startIntent = startIntent;
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
                // THE consent gate. Before CreateObject, before anything is started, and
                // before any diagnostic claims a start is under way.
                //
                // It guards a COLD start only. A session that already exists — hidden, or
                // deliberately shown to the engineer — serves later background work without
                // asking again, because nothing about reusing it puts a new window on
                // screen. It is process CREATION that the engineer had to agree to.
                RequireVisibleStartConsent();

                _diagnostics.WriteLine(
                    "ℹ Starting ETABS (shared serve session; the engineer consented to a " +
                    "visible start)...");
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
                //
                // What is proven changed after #20: the gate is the exact-owned Windows
                // census, not cOAPI.Visible(). InitializeNewModel is precisely when ETABS
                // builds the (Untitled) model behind its frame, so this second census is
                // where a window that surfaced during initialization has to be accounted
                // for — the guard will already have taken it down on the window event.
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
    /// Refuses a cold start that nobody declared an intent for.
    ///
    /// <para>Fails BEFORE <c>cHelper.CreateObject</c>, which is the only useful place: once
    /// that call returns, ETABS is already starting and already heading for the screen.
    /// The refusal is typed so the desktop can tell "you must ask the engineer first" apart
    /// from every other launch failure and show the consent prompt rather than an error.</para>
    ///
    /// <para>The intent is never inferred from the command name. A command called
    /// <c>snapshot-export</c> tells us what work was asked for, not whether anyone warned
    /// the engineer that a window is about to appear — and inferring the second from the
    /// first is exactly how an unconsented exposure would get rationalised as expected.</para>
    /// </summary>
    private void RequireVisibleStartConsent()
    {
        var intent = _startIntent.Current;
        if (intent == ManagedEtabsStartIntent.VisibleByConsent)
        {
            return;
        }

        _launchFailure = new EtabsLaunchException(
            EtabsLaunchErrorCodes.VisibleStartConsentMissing,
            EtabsApiDiagnosticFormatter.Bounded(string.Join(
                "; ",
                $"declaredIntent={intent}",
                $"expected={ManagedEtabsStartIntents.VisibleByConsent}",
                "starting ETABS puts it on screen for several seconds and that cannot be " +
                "prevented on this ETABS build, so a cold start requires the caller to " +
                "declare that the engineer was told and agreed. No process was created.")));
        throw _launchFailure;
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
    /// The second and last background-suppression proof of a session's life, taken once
    /// <c>InitializeNewModel</c> has produced the blank model and the EtabSharp wrap
    /// exists, and still before the application is handed to any command.
    ///
    /// <para>The launcher already proved suppression right after <c>ApplicationStart</c>.
    /// This is not redundant: <c>InitializeNewModel</c> is what puts the blank
    /// <c>(Untitled)</c> model behind the frame, and the #20 timeline shows ETABS building
    /// UI long after the start call returns. A second exact-owned census costs one window
    /// enumeration and covers everything that surfaced in between.</para>
    ///
    /// <para><b>Failure is terminal.</b> An unproven Windows state returns a launch failure
    /// rather than a warning, and the caller tears the session down through the same
    /// cleanup a failed initialization uses — the authoritative owned process is exited,
    /// not left running with a window on screen.</para>
    ///
    /// <para>It runs ONLY while the session is being created. That is the whole reason a
    /// background command can safely reuse a session the user asked to see: nothing on the
    /// command path ever suppresses anything.</para>
    /// </summary>
    private (string Code, string Diagnostic, Exception? Exception)? ConfirmHiddenForBackgroundWork(
        IManagedEtabsApplication owned)
    {
        var confirmation = owned.ConfirmWindowsSuppressed();
        if (!confirmation.Confirmed)
        {
            return (
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    confirmation.Diagnostic
                        ?? "Managed ETABS window suppression could not be confirmed.",
                    "stage=after cSapModel.InitializeNewModel"),
                (Exception?)null);
        }

        // CLI #24. "Hidden now" is not the question the product asks. An earlier candidate
        // logged "started hidden" truthfully seconds after putting a full-screen ETABS
        // window in front of the engineer, because a point-in-time census cannot see what
        // already happened. Readiness therefore needs BOTH: currently hidden, AND never
        // materially on screen since the consent interval closed.
        var exposure = owned.Exposure;
        if (exposure.Observed)
        {
            return (
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    exposure.Describe(),
                    "stage=after cSapModel.InitializeNewModel; " +
                    "a later hidden census cannot clear an exposure that already happened"),
                (Exception?)null);
        }

        _diagnostics.WriteLine(
            "✓ ETABS background UI suppression confirmed before use " +
            $"(observations={confirmation.Observations}, " +
            $"waitedMs={(long)confirmation.Waited.TotalMilliseconds}, " +
            $"state={owned.VisibilityState}, unconsentedExposure=false).");
        return null;
    }

    /// <inheritdoc />
    public Result RevealForExplicitUserRequest()
    {
        lock (_gate)
        {
            var owned = GetOrStartOwned();

            // Order is the contract, and every step of it is load bearing.
            //
            // The caller has already confirmed the requested model is open. The protected
            // interval is retired FIRST so that the observer cannot record the reveal
            // itself as an unconsented exposure, and so nothing re-arms behind it.
            //
            // What changed with CLI #22: this step used to also put our own hidden HWNDs
            // back with ShowWindow(SW_SHOW), and that restore was what actually reached
            // the screen, because the stuck cOAPI.Visible() flag made the CSI policy skip
            // its Unhide entirely. Diagnostic #4 removed both halves of that workaround and
            // measured the result: with ShowWindow impossible in either direction, an
            // unconditional raw Unhide put the window back 14 ms later. Windows observes;
            // CSI acts.
            owned.ReleaseWindowGuardForExplicitUserAction();

            // CSI mutates, unconditionally. A throw means the call never happened, so
            // there is nothing for the census to certify.
            var csi = owned.ApplyCsiUnhideForExplicitUserAction();
            if (!csi.Issued)
            {
                return Result.Fail(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    csi.Diagnostic ?? "cOAPI.Unhide could not be issued.",
                    "csiTransitionIssued=false"));
            }

            // THE gate: Windows itself must report an owned top-level ETABS window
            // materially on screen. "Open in ETABS" that shows nothing has not done what
            // was asked, whatever CSI says about it.
            var windows = owned.ConfirmWindowsRevealed();
            if (!windows.Confirmed)
            {
                return Result.Fail(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    windows.Diagnostic
                        ?? "No owned ETABS window could be confirmed visible.",
                    $"csiReturnCode={csi.ReturnCode}; csiConfirmed={csi.Confirmed}"));
            }

            // Only now is the session UserVisible. Later background work reuses it as-is
            // and must never quietly take the screen back.
            owned.EnterUserVisible();

            _diagnostics.WriteLine(
                $"✓ ETABS shown (PID {owned.Identity.Pid}; " +
                $"visibleOwnedWindows={windows.ObservedWindows.Count}, " +
                $"csiReturnCode={csi.ReturnCode}, state={owned.VisibilityState})");

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
