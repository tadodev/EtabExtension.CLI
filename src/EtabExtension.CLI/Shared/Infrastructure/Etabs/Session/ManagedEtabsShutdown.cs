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
    ManagedEtabsShutdownResult ShutdownAfterRecoveryRecordWriteFailure(
        IManagedEtabsApplication owned);
}

public sealed class ManagedEtabsShutdownMachine(
    ISessionRecordStore records) : IManagedEtabsShutdownMachine
{
    public static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    public ManagedEtabsShutdownResult Shutdown(IManagedEtabsApplication owned)
        => ShutdownCore(owned, ShutdownAuthority.RequireMatchingRecoveryRecord);

    public ManagedEtabsShutdownResult ShutdownAfterRecoveryRecordWriteFailure(
        IManagedEtabsApplication owned)
        => ShutdownCore(owned, ShutdownAuthority.AuthoritativeOwnedHandle);

    private ManagedEtabsShutdownResult ShutdownCore(
        IManagedEtabsApplication owned,
        ShutdownAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(owned);

        ManagedEtabsShutdownResult result;
        var record = records.Read();
        var recordMatchesOwned = IdentityMatches(record, owned);

        try
        {
            if (authority == ShutdownAuthority.RequireMatchingRecoveryRecord
                && !recordMatchesOwned)
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
                var recordRetained = ClearMatchingRecord(record, recordMatchesOwned);
                result = Succeeded(
                    owned.Identity.Pid,
                    forced: false,
                    applicationExitReturnCode: null,
                    recordRetained);
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
                    recordRetained: record is not null,
                    applicationExitReturnCode);
                return result;
            }

            var confirmedRecordRetained = ClearMatchingRecord(record, recordMatchesOwned);
            result = applicationExitError is null
                ? Succeeded(
                    owned.Identity.Pid,
                    forced,
                    applicationExitReturnCode,
                    confirmedRecordRetained)
                : Failed(
                    ManagedEtabsShutdownState.ApplicationExitFailed,
                    ManagedEtabsShutdownErrorCodes.ApplicationExitFailed,
                    EtabsApiDiagnosticFormatter.AppendTerminalFacts(
                        applicationExitError,
                        $"forced={forced.ToString().ToLowerInvariant()}; processExitConfirmed=true"),
                    owned.Identity.Pid,
                    processExitConfirmed: true,
                    forced,
                    recordRetained: confirmedRecordRetained,
                    applicationExitReturnCode);
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

    private bool ClearMatchingRecord(
        ManagedEtabsSessionRecord? record,
        bool recordMatchesOwned)
    {
        if (recordMatchesOwned)
        {
            records.Clear();
            return false;
        }

        return record is not null;
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
        int? applicationExitReturnCode,
        bool recordRetained) => new(
        true,
        null,
        null,
        new(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: recordRetained,
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

    private enum ShutdownAuthority
    {
        RequireMatchingRecoveryRecord,
        AuthoritativeOwnedHandle
    }
}
