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
/// <c>cOAPI.ApplicationStart()</c> exactly once, requiring zero → hide the started
/// application → <c>cOAPI.SapModel</c> present → an exact OS census proving exactly one
/// ETABS process, captured as pid + start time UTC + executable path with an
/// authoritative handle.</para>
///
/// <para><b>Why the hide is here</b> (CLI #22). Cardex documents exactly one
/// <c>ApplicationStart</c> overload — <c>int ApplicationStart()</c>, no visibility
/// argument — and does not say what the application's visibility is afterwards. The
/// supervised RC1 reproduction observed a window arriving 8.5 s into a background
/// snapshot-export and showing a blank <c>(Untitled)</c> model at 14.8 s, with the
/// requested EDB only opening at 16.9 s. Every session this launcher creates is created
/// for work, not for looking at, so it is hidden here — at the first instant there is
/// anything to hide — and only an explicit user request through
/// <see cref="IEtabsSession.RevealForExplicitUserRequest"/> ever brings it back.</para>
///
/// <para>The returned application is owned but <b>not yet API-ready</b>: the caller writes
/// the recovery record and calls <c>InitializeNewModel</c> before exposing it. Nothing here
/// starts a process out of band, attaches by pid, falls back to the ROT, or sleeps waiting
/// for readiness — the previous path did all four and still could not prove that the
/// object it attached to was usable.</para>
/// </summary>
public sealed class ManagedEtabsLauncher : IManagedEtabsLauncher
{
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the exact-ownership census is given to resolve after
    /// <c>cHelper.CreateObject</c> started the program.
    ///
    /// <para>The census used to run after <c>ApplicationStart</c>, by which point the new
    /// process had long been enumerable. It now runs immediately after creation — the
    /// earliest point at which exact ownership can exist at all — and a process that has
    /// only just been created can briefly refuse to report its main module. That is a
    /// transient, not an ambiguity, so it is waited out rather than trusted or feared:
    /// anything still unresolved at the deadline fails closed exactly as before.</para>
    /// </summary>
    public static readonly TimeSpan OwnershipCensusDeadline = TimeSpan.FromSeconds(5);

    /// <summary>How often the ownership census is retried while it is still unresolved.</summary>
    public static readonly TimeSpan OwnershipCensusPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly IProcessInspector _processes;
    private readonly IEtabsExecutableResolver _executableResolver;
    private readonly IEtabsRawApiFactory _apiFactory;
    private readonly IEtabsVersionProbe _versionProbe;
    private readonly TextWriter _diagnostics;
    private readonly IManagedEtabsWindowGuardFactory _windowGuards;
    private readonly IManagedEtabsClock _clock;

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
        TextWriter diagnostics) : this(
            processes,
            executableResolver,
            apiFactory,
            versionProbe,
            diagnostics,
            WindowsManagedEtabsWindowGuardFactory.Instance,
            SystemManagedEtabsClock.Instance)
    {
    }

    public ManagedEtabsLauncher(
        IProcessInspector processes,
        IEtabsExecutableResolver executableResolver,
        IEtabsRawApiFactory apiFactory,
        IEtabsVersionProbe versionProbe,
        TextWriter diagnostics,
        IManagedEtabsWindowGuardFactory windowGuards,
        IManagedEtabsClock clock)
    {
        _processes = processes;
        _executableResolver = executableResolver;
        _apiFactory = apiFactory;
        _versionProbe = versionProbe;
        _diagnostics = diagnostics;
        _windowGuards = windowGuards;
        _clock = clock;
    }

    public IManagedEtabsApplication Launch()
    {
        CaptureCrossCheckBaseline();
        var executablePath = _executableResolver.Resolve();

        // CreateObject itself starts the program, so it lives inside the cleanup envelope:
        // even a null return or a throw can leave a partially started process behind, and
        // every exit path from here must resolve it.
        IEtabsRawApi? rawApi = null;
        IOwnedEtabsProcess? ownedProcess = null;
        IManagedEtabsWindowGuard? windowGuard = null;
        try
        {
            rawApi = _apiFactory.CreateFromExecutable(executablePath);

            // Ownership first, and as early as CreateObject makes it possible. Preflight
            // proved zero ETABS processes and CreateObject started exactly one, so this
            // census is what turns "a process exists" into "this exact process is ours" —
            // and nothing may be suppressed before that is true.
            var identity = CensusExactlyOneOwnedProcess();
            ownedProcess = _processes.OpenExact(identity)
                ?? throw new EtabsLaunchException(
                    EtabsLaunchErrorCodes.ProcessIdentityFailed,
                    "Could not open an authoritative handle on the started ETABS process " +
                    $"pid={identity.Pid}; its live identity no longer matches the census.");

            // Then, and only then, hold that exact process's windows down — before the
            // blocking startup work the #20 live run measured a visible window through.
            windowGuard = ActivateWindowGuard(ownedProcess);
            windowGuard.MarkStage("cOAPI.ApplicationStart");

            // ApplicationStart() returning is the ONE proven readiness boundary on this
            // API path. Diagnostic #3 measured cOAPI.Hide() throwing a
            // NullReferenceException ~12 ms in when called before it, and the
            // process-control spike measured SapModel non-null while the first real call
            // still threw "the window handle has not been created" - so neither is a
            // substitute, and neither is a sleep.
            StartApplication(rawApi);

            // CSI mutates. Unconditionally, exactly once, and a throw is fatal.
            AskCsiToHide(rawApi);

            // Windows certifies AND the startup-consent interval closes, in one
            // indivisible step. Splitting them left a gap in which a WinEvent could fire,
            // be judged against the still-consented state, and be discarded as expected
            // startup visibility - which is the exposure CLI #24 exists to catch.
            RequireWindowsSuppressed(windowGuard, "after cOAPI.ApplicationStart");

            RequireSapModel(rawApi);

            var version = ReadVersion(identity.ExecutablePath);
            var managed = new ManagedEtabsApplication(
                rawApi,
                identity,
                Guid.NewGuid(),
                ownedProcess,
                windowGuard,
                new ManagedEtabsApiVersion(
                    version.MajorVersion,
                    ReadApiVersion(rawApi),
                    version.FullVersion));
            ownedProcess = null; // ownership transferred to the managed application
            windowGuard = null;  // and with it the guard's lifetime
            return managed;
        }
        catch (Exception failure)
        {
            // The cleanup outcome always travels with the failure. When it is unresolved a
            // process this attempt started may still be alive with no recovery record to
            // describe it, and the session uses that to refuse both a relaunch and a clean
            // shutdown answer later.
            var cleanup = CleanUpFailedStart(rawApi, ownedProcess, windowGuard);
            var code = failure is EtabsLaunchException typed
                ? typed.Code
                : EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance;
            var message = failure is EtabsLaunchException original
                ? StripCode(original)
                : failure.Message;

            throw new EtabsLaunchException(
                code,
                cleanup.Success
                    ? message
                    : EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        message,
                        $"cleanup={cleanup.Data.State}; " +
                        $"processExitConfirmed={cleanup.Data.ProcessExitConfirmed}; " +
                        $"forced={cleanup.Data.Forced}; " +
                        $"ownedPid={cleanup.Data.OwnedPid?.ToString() ?? "unknown"}"),
                failure.InnerException ?? failure)
            {
                Cleanup = cleanup
            };
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

    /// <summary>
    /// Activates Windows-level suppression of the exact owned process's windows, for the
    /// whole of the background life of the session it is about to create.
    ///
    /// <para>This is the layer the #20 certification proved was missing. The CSI hide below
    /// is still the authoritative transition, but it cannot run until
    /// <c>ApplicationStart</c> returns, and the supervised run measured ETABS on screen for
    /// 5.19 s inside that call. Suppression is therefore armed first — against the process
    /// this launcher has already proven it owns, never against "an ETABS window".</para>
    ///
    /// <para>Not defended against failure on purpose: an exception here escapes into the
    /// launch cleanup envelope and fails the session, because a background session that
    /// cannot suppress its own window is the exact condition #20 rejected.</para>
    /// </summary>
    private IManagedEtabsWindowGuard ActivateWindowGuard(IOwnedEtabsProcess ownedProcess)
    {
        IManagedEtabsWindowGuard guard;
        try
        {
            guard = _windowGuards.Activate(ownedProcess);
        }
        catch (Exception exception) when (exception is not EtabsLaunchException)
        {
            // Typed rather than swallowed by the generic launch handler: a subscription that
            // could not be installed is a materially different fact from an ambiguous ETABS
            // instance, and the live gate has to be able to tell them apart.
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.WindowSuppressionUnavailable,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "IManagedEtabsWindowGuardFactory.Activate",
                    exception),
                exception);
        }

        _diagnostics.WriteLine(
            "ℹ ETABS window suppression armed for owned " +
            $"pid={ownedProcess.Identity.Pid} (released only by an explicit open-model).");
        return guard;
    }

    /// <summary>
    /// Asks CSI to take the application it just started off screen. Once, unconditionally.
    ///
    /// <para><b>Why unconditionally.</b> The old policy read <c>cOAPI.Visible()</c> first
    /// and skipped the call when the flag already said "hidden". #20 measured that flag
    /// stuck true for 94 reads across 10.014 s while the windows were in fact hidden — so
    /// a read-first policy is exactly wrong: it declines to hide precisely when the flag
    /// is lying. The call is now always issued and the return code is recorded, not
    /// obeyed.</para>
    ///
    /// <para><b>Why a throw is fatal but a non-zero return is not.</b> A non-zero return
    /// means ETABS considered the request and declined it, most often because it believed
    /// it was already hidden; the census below settles who was right. A throw means the
    /// call never happened, so there is nothing for the census to confirm and continuing
    /// would be pretending.</para>
    /// </summary>
    private void AskCsiToHide(IEtabsRawApi rawApi)
    {
        var outcome = ManagedEtabsVisibility.ApplyHidden(rawApi);
        if (!outcome.Issued)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    outcome.Diagnostic ?? "cOAPI.Hide could not be issued.",
                    "stage=after cOAPI.ApplicationStart; csiTransitionIssued=false"));
        }

        _diagnostics.WriteLine(outcome.Diagnostic is null
            ? $"ℹ cOAPI.Hide issued (returnCode={outcome.ReturnCode})."
            : $"ℹ cOAPI.Hide issued (returnCode={outcome.ReturnCode}); " +
                $"Windows state is the authority. {outcome.Diagnostic}");
    }

    /// <summary>
    /// The authoritative background-readiness gate: the exact-owned Windows census must
    /// report no visible top-level window before startup goes any further.
    ///
    /// <para>A launch failure when it cannot be proven — warning and continuing is what
    /// #20 rejected, and this is deliberately NOT that policy with a new implementation
    /// underneath: the thing being proven is now real Windows state rather than a CSI
    /// flag, and the diagnostic names the offending handles.</para>
    /// </summary>
    private void RequireWindowsSuppressed(IManagedEtabsWindowGuard guard, string stage)
    {
        var confirmation = guard.ConfirmSuppressedAndCloseConsentInterval();
        if (!confirmation.Confirmed)
        {
            throw new EtabsLaunchException(
                EtabsLaunchErrorCodes.HiddenStateNotEstablished,
                EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                    confirmation.Diagnostic
                        ?? "Managed ETABS window suppression could not be confirmed.",
                    $"stage={stage}"));
        }

        _diagnostics.WriteLine(
            $"✓ ETABS background UI suppression confirmed {stage} " +
            $"(observations={confirmation.Observations}, " +
            $"waitedMs={(long)confirmation.Waited.TotalMilliseconds}).");
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
    /// could not be identified — fails closed once the bounded settle deadline is spent.
    ///
    /// <para>The wait exists only because this census moved: it now runs immediately after
    /// <c>CreateObject</c> rather than after <c>ApplicationStart</c>, so that the window
    /// guard can be armed before the blocking startup work. A process seconds younger can
    /// still be mid-initialization and refuse to report its main module, which reads as
    /// "unidentified" and is a transient. It is never resolved OPTIMISTICALLY: only an
    /// observation of exactly one identified process and nothing unidentified ends the
    /// loop, and the deadline raises the same typed refusal this method always did.</para>
    /// </summary>
    private ManagedProcessIdentity CensusExactlyOneOwnedProcess()
    {
        var started = _clock.Timestamp;
        while (true)
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

            if (_clock.ElapsedSince(started) >= OwnershipCensusDeadline)
            {
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

            _clock.Wait(OwnershipCensusPollInterval);
        }
    }

    /// <summary>
    /// Resolves a process that CreateObject may have started. The raw exit is requested
    /// first; termination happens only against an identity this launcher proved, never
    /// against an ambiguous or unreadable one.
    /// </summary>
    /// <summary>
    /// Removes the <c>[CODE] </c> prefix so re-throwing with the same code does not stack
    /// two of them onto one message.
    /// </summary>
    private static string StripCode(EtabsLaunchException failure)
    {
        var prefix = $"[{failure.Code}] ";
        return failure.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? failure.Message[prefix.Length..]
            : failure.Message;
    }

    private ManagedEtabsShutdownResult CleanUpFailedStart(
        IEtabsRawApi? rawApi,
        IOwnedEtabsProcess? ownedProcess,
        IManagedEtabsWindowGuard? windowGuard)
    {
        // Stop suppressing before anything is asked to exit, and restore nothing: a failed
        // start must not put a window on screen on its way out.
        windowGuard?.Dispose();
        var applicationExitReturnCode = RequestRawExit(rawApi);

        return ownedProcess is not null
            ? StopOwnedProcess(ownedProcess, applicationExitReturnCode)
            : ResolveSurvivorWithoutHandle(applicationExitReturnCode);
    }

    /// <summary>Requests the raw exit when an object exists; a missing object is not an error.</summary>
    private int? RequestRawExit(IEtabsRawApi? rawApi)
    {
        if (rawApi is null)
        {
            return null;
        }

        try
        {
            var returnCode = rawApi.ApplicationExit(false);
            if (returnCode != 0)
            {
                _diagnostics.WriteLine(
                    EtabsApiDiagnosticFormatter.ApiReturn("cOAPI.ApplicationExit(false)", returnCode));
            }

            return returnCode;
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine(
                EtabsApiDiagnosticFormatter.Exception("cOAPI.ApplicationExit(false)", ex));
            return null;
        }
    }

    private ManagedEtabsShutdownResult StopOwnedProcess(
        IOwnedEtabsProcess ownedProcess,
        int? applicationExitReturnCode)
    {
        var pid = ownedProcess.Identity.Pid;
        try
        {
            var forced = false;
            if (!ownedProcess.HasExited)
            {
                forced = true;
                ownedProcess.Kill();
                if (!ownedProcess.WaitForExit(ForcedExitTimeout))
                {
                    return Unresolved(
                        ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                        ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                        $"The ETABS process started by this attempt (pid={pid}) did not confirm exit after forced termination.",
                        pid,
                        forced: true,
                        applicationExitReturnCode);
                }
            }

            ownedProcess.Dispose();
            return Resolved(pid, forced, applicationExitReturnCode);
        }
        catch (Exception ex)
        {
            return Unresolved(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "IOwnedEtabsProcess.Kill/WaitForExit",
                    ex),
                pid,
                forced: true,
                applicationExitReturnCode);
        }
    }

    /// <summary>
    /// No authoritative handle was taken, so ownership is re-derived from the census:
    /// nothing left is clean, exactly one identified process is ours by construction and
    /// may be terminated by exact identity, and anything ambiguous is left untouched and
    /// reported as unresolved.
    /// </summary>
    private ManagedEtabsShutdownResult ResolveSurvivorWithoutHandle(int? applicationExitReturnCode)
    {
        EtabsProcessObservation observation;
        try
        {
            observation = _processes.ObserveEtabs();
        }
        catch (Exception ex)
        {
            return Unresolved(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                EtabsApiDiagnosticFormatter.InfrastructureException(
                    "IProcessInspector.ObserveEtabs",
                    ex),
                ownedPid: null,
                forced: false,
                applicationExitReturnCode);
        }

        if (observation.Identified.Count == 0 && observation.UnidentifiedCount == 0)
        {
            return Resolved(ownedPid: null, forced: false, applicationExitReturnCode);
        }

        if (observation.UnidentifiedCount > 0 || observation.Identified.Count != 1)
        {
            return Unresolved(
                ManagedEtabsShutdownState.IdentityMismatch,
                ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                "Managed ETABS start failed and ownership of the surviving ETABS process could not be proven; " +
                $"nothing was terminated. identifiedCount={observation.Identified.Count}; " +
                $"unidentifiedCount={observation.UnidentifiedCount}",
                ownedPid: null,
                forced: false,
                applicationExitReturnCode);
        }

        var survivor = observation.Identified[0];
        var termination = _processes.TerminateExact(survivor, ForcedExitTimeout);
        return termination.State switch
        {
            ExactProcessTerminationState.NotFound =>
                Resolved(survivor.Pid, forced: false, applicationExitReturnCode),
            ExactProcessTerminationState.ConfirmedGone =>
                Resolved(survivor.Pid, forced: true, applicationExitReturnCode),
            ExactProcessTerminationState.IdentityMismatchOrUnidentified => Unresolved(
                ManagedEtabsShutdownState.IdentityMismatch,
                ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                $"The surviving ETABS process (pid={survivor.Pid}) no longer matched the census identity; it was not terminated.",
                survivor.Pid,
                forced: false,
                applicationExitReturnCode),
            _ => Unresolved(
                ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                $"The ETABS process started by this attempt (pid={survivor.Pid}) could not be confirmed gone.",
                survivor.Pid,
                forced: true,
                applicationExitReturnCode)
        };
    }

    private static ManagedEtabsShutdownResult Resolved(
        int? ownedPid,
        bool forced,
        int? applicationExitReturnCode) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: applicationExitReturnCode,
            OwnedPid: ownedPid));

    private ManagedEtabsShutdownResult Unresolved(
        ManagedEtabsShutdownState state,
        string errorCode,
        string diagnostic,
        int? ownedPid,
        bool forced,
        int? applicationExitReturnCode)
    {
        var bounded = EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            diagnostic,
            $"code={errorCode}; state={state}; forced={forced}");
        _diagnostics.WriteLine(bounded);
        return new(
            false,
            errorCode,
            bounded,
            new(
                state,
                ProcessExitConfirmed: false,
                Forced: forced,
                RecordRetained: false,
                ApplicationExitReturnCode: applicationExitReturnCode,
                OwnedPid: ownedPid));
    }
}
