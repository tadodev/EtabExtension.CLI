using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeCommandLifecycleTests
{
    [Fact]
    public async Task StartupOrphanRecoveryFailureFailsClosedBeforeLoopAndStillConvergesCleanup()
    {
        var recoveryData = new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.IdentityMismatch,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: 42);
        var recovery = new ManagedEtabsShutdownResult(
            false,
            ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            "Orphan identity is unreadable.",
            recoveryData);
        var cleaner = new FakeOrphanCleaner(recovery);
        var coordinator = new FakeCoordinator(new(
            false,
            recoveryData,
            $"{ManagedEtabsShutdownErrorCodes.IdentityMismatch}: record retained"));
        var loopCalls = 0;

        var error = await Assert.ThrowsAsync<EtabsLaunchException>(() =>
            ServeCommand.RunLifecycleAsync(
                cleaner,
                coordinator,
                () =>
                {
                    loopCalls++;
                    return Task.CompletedTask;
                },
                TextWriter.Null));

        Assert.Equal(ManagedEtabsShutdownErrorCodes.IdentityMismatch, error.Code);
        Assert.Contains("Orphan identity is unreadable", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, cleaner.CleanCount);
        Assert.Equal(0, loopCalls);
        Assert.Equal(1, coordinator.CallCount);
        Assert.True(recovery.Data.RecordRetained);
    }

    private sealed class FakeOrphanCleaner(
        ManagedEtabsShutdownResult result) : IOrphanSessionCleaner
    {
        public int CleanCount { get; private set; }

        public ManagedEtabsShutdownResult Clean()
        {
            CleanCount++;
            return result;
        }
    }

    private sealed class FakeCoordinator(
        Result<ManagedEtabsShutdownData> result) : IServeShutdownCoordinator
    {
        public int CallCount { get; private set; }

        public Task<Result<ManagedEtabsShutdownData>> ShutdownAsync()
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public async ValueTask DisposeAsync() =>
            _ = await ShutdownAsync();
    }
}
