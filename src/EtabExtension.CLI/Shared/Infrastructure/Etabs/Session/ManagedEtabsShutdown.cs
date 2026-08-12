using EtabExtension.CLI.Shared.Infrastructure.Etabs;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class ManagedEtabsShutdownErrorCodes
{
    public const string ApplicationExitFailed = "ETABS_APPLICATION_EXIT_FAILED";
    public const string IdentityMismatch = "ETABS_SHUTDOWN_IDENTITY_MISMATCH";
    public const string ProcessExitUnconfirmed = "ETABS_PROCESS_EXIT_UNCONFIRMED";
}

public enum ManagedEtabsShutdownState
{
    Succeeded,
    ApplicationExitFailed,
    IdentityMismatch,
    ProcessExitUnconfirmed
}

public sealed record ManagedEtabsShutdownData(
    ManagedEtabsShutdownState State,
    bool ProcessExitConfirmed,
    bool Forced,
    bool RecordRetained,
    int? ApplicationExitReturnCode,
    int? OwnedPid);

public sealed record ManagedEtabsShutdownResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    ManagedEtabsShutdownData Data);

public interface IManagedEtabsShutdownMachine
{
    ManagedEtabsShutdownResult Shutdown(IManagedEtabsApplication owned);
}

public sealed class ManagedEtabsShutdownMachine(
    ISessionRecordStore records) : IManagedEtabsShutdownMachine
{
    public static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    public ManagedEtabsShutdownResult Shutdown(IManagedEtabsApplication owned)
    {
        ArgumentNullException.ThrowIfNull(owned);

        ManagedEtabsShutdownResult result;
        var record = records.Read();

        try
        {
            if (!IdentityMatches(record, owned))
            {
                return Failed(
                    ManagedEtabsShutdownState.IdentityMismatch,
                    ManagedEtabsShutdownErrorCodes.IdentityMismatch,
                    "Managed ETABS recovery record does not match the authoritative owned process handle.",
                    owned.Identity.Pid,
                    processExitConfirmed: false,
                    forced: false,
                    recordRetained: record is not null,
                    applicationExitReturnCode: null);
            }

            if (owned.HasExited)
            {
                result = Succeeded(owned.Identity.Pid, forced: false, applicationExitReturnCode: null);
                records.Clear();
                return result;
            }

            int? applicationExitReturnCode = null;
            string? applicationExitError = null;
            try
            {
                applicationExitReturnCode = owned.ExitWithoutSaving();
                if (applicationExitReturnCode != 0)
                {
                    applicationExitError = EtabsApiDiagnosticFormatter.ApiReturn(
                        "cOAPI.ApplicationExit(false)",
                        applicationExitReturnCode.Value);
                }
            }
            catch (Exception exception)
            {
                applicationExitError = EtabsApiDiagnosticFormatter.Exception(
                    "cOAPI.ApplicationExit(false)",
                    exception);
            }

            var processExitConfirmed = TryWait(owned, GracefulExitTimeout);
            var forced = false;
            string? cleanupError = null;
            if (!processExitConfirmed)
            {
                forced = true;
                try
                {
                    owned.Kill();
                }
                catch (Exception exception)
                {
                    cleanupError = EtabsApiDiagnosticFormatter.Exception(
                        "Process.Kill(authoritative-owned-handle)",
                        exception);
                }

                processExitConfirmed = TryWait(owned, ForcedExitTimeout);
            }

            if (!processExitConfirmed)
            {
                result = Failed(
                    ManagedEtabsShutdownState.ProcessExitUnconfirmed,
                    ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
                    cleanupError ?? applicationExitError
                        ?? "The exact owned ETABS process did not confirm exit after graceful and forced cleanup.",
                    owned.Identity.Pid,
                    processExitConfirmed: false,
                    forced,
                    recordRetained: true,
                    applicationExitReturnCode);
                return result;
            }

            result = applicationExitError is null
                ? Succeeded(owned.Identity.Pid, forced, applicationExitReturnCode)
                : Failed(
                    ManagedEtabsShutdownState.ApplicationExitFailed,
                    ManagedEtabsShutdownErrorCodes.ApplicationExitFailed,
                    applicationExitError,
                    owned.Identity.Pid,
                    processExitConfirmed: true,
                    forced,
                    recordRetained: false,
                    applicationExitReturnCode);
            records.Clear();
            return result;
        }
        finally
        {
            try
            {
                owned.Dispose();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(EtabsApiDiagnosticFormatter.Exception(
                    "ManagedEtabsApplication.Dispose",
                    exception));
            }
        }
    }

    private static bool IdentityMatches(
        ManagedEtabsSessionRecord? record,
        IManagedEtabsApplication owned) =>
        record is not null
        && record.ManagedLaunchRecordId == owned.ManagedLaunchRecordId
        && OrphanSessionCleaner.IdentityMatches(record, owned.Identity);

    private static bool TryWait(IManagedEtabsApplication owned, TimeSpan timeout)
    {
        try
        {
            return owned.WaitForExit(timeout);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(EtabsApiDiagnosticFormatter.Exception(
                "Process.WaitForExit(authoritative-owned-handle)",
                exception));
            return false;
        }
    }

    private static ManagedEtabsShutdownResult Succeeded(
        int ownedPid,
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

    private static ManagedEtabsShutdownResult Failed(
        ManagedEtabsShutdownState state,
        string errorCode,
        string error,
        int? ownedPid,
        bool processExitConfirmed,
        bool forced,
        bool recordRetained,
        int? applicationExitReturnCode) => new(
        false,
        errorCode,
        error,
        new(
            state,
            processExitConfirmed,
            forced,
            recordRetained,
            applicationExitReturnCode,
            ownedPid));
}
