using System.Text.Json;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeCommandLifecycleTests
{
    private const string RecordPath = @"C:\Users\test\AppData\Local\EtabExtension\sidecar\managed-etabs-session.json";

    public static TheoryData<string, ManagedEtabsShutdownState, int?> UnsafeRecoveryStates => new()
    {
        {
            ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            ManagedEtabsShutdownState.IdentityMismatch,
            42
        },
        {
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            1684
        },
        {
            ManagedEtabsShutdownErrorCodes.ApplicationExitFailed,
            ManagedEtabsShutdownState.ApplicationExitFailed,
            null
        }
    };

    [Theory]
    [MemberData(nameof(UnsafeRecoveryStates))]
    public async Task RefusedStartupWritesOneTypedFrameSkipsTheLoopAndKeepsTheRecord(
        string errorCode,
        ManagedEtabsShutdownState state,
        int? ownedPid)
    {
        var recovery = Recovery(errorCode, state, ownedPid);
        var cleaner = new FakeOrphanCleaner(recovery);
        var coordinator = new FakeCoordinator(new(false, recovery.Data, $"{errorCode}: record retained"));
        var loopCalls = 0;
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        var outcome = await ServeCommand.RunLifecycleAsync(
            cleaner,
            coordinator,
            () =>
            {
                loopCalls++;
                return Task.CompletedTask;
            },
            output,
            diagnostics,
            failure => Refusal(failure));

        Assert.Equal(ServeLifecycleOutcome.StartupRefused, outcome);
        Assert.Equal(0, loopCalls);
        Assert.Equal(1, cleaner.CleanCount);
        Assert.Equal(1, coordinator.CallCount);

        var frame = Assert.Single(Frames(output));
        Assert.Equal("etab-cli-serve", frame.GetProperty("protocol").GetString());
        Assert.Equal(1, frame.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("refused", frame.GetProperty("startup").GetString());
        Assert.Equal("0.1.0", frame.GetProperty("version").GetString());
        Assert.Equal("0.1.0+gtest", frame.GetProperty("buildId").GetString());
        Assert.Equal(errorCode, frame.GetProperty("errorCode").GetString());
        Assert.Equal("Orphan identity is unreadable.", frame.GetProperty("error").GetString());
        Assert.True(frame.GetProperty("recordRetained").GetBoolean());
        Assert.False(frame.GetProperty("processExitConfirmed").GetBoolean());
        Assert.Equal(RecordPath, frame.GetProperty("recordPath").GetString());
        Assert.Contains(
            "do not delete it",
            frame.GetProperty("remediation").GetString(),
            StringComparison.Ordinal);

        // A refusal is not a ready daemon: no capabilities are advertised.
        Assert.False(frame.TryGetProperty("capabilities", out _));
        if (ownedPid is null)
        {
            Assert.False(frame.TryGetProperty("ownedPid", out _));
        }
        else
        {
            Assert.Equal(ownedPid, frame.GetProperty("ownedPid").GetInt32());
        }
    }

    // The remediation is a human instruction standing next to a refusal the code made
    // on exact identity. It must not be weaker than that: PID reuse can produce a
    // foreign ETABS with the recorded pid AND the recorded executable path but a
    // different start time — the precise state the sidecar refused to terminate.
    [Fact]
    public void RemediationRequiresAllThreeIdentityFieldsAndForbidsTerminationOnMismatch()
    {
        var remediation = ServeStartupRefusal.RemediationText;

        // All three identity components, never a pid-only instruction.
        Assert.Contains("pid + process start time (UTC) + executable path", remediation, StringComparison.Ordinal);
        Assert.Contains("Do not terminate anything by pid alone", remediation, StringComparison.Ordinal);
        Assert.Contains("only if all three match that record exactly", remediation, StringComparison.Ordinal);
        Assert.Contains("PIDs are reused", remediation, StringComparison.Ordinal);

        // Mismatched or unreadable identity: leave the process alone, keep the evidence.
        Assert.Contains(
            "If any of the three differs, or cannot be read, do not terminate the process",
            remediation,
            StringComparison.Ordinal);
        Assert.Contains("keep the record", remediation, StringComparison.Ordinal);
        Assert.Contains("do not delete it to bypass this refusal", remediation, StringComparison.Ordinal);

        Assert.True(remediation.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
        Assert.DoesNotContain(remediation, char.IsControl);
    }

    [Fact]
    public async Task RefusedStartupCarriesTheExactIdentityRemediationVerbatim()
    {
        var recovery = Recovery(
            ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            ManagedEtabsShutdownState.IdentityMismatch,
            42);
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        await ServeCommand.RunLifecycleAsync(
            new FakeOrphanCleaner(recovery),
            new FakeCoordinator(Result.Ok(recovery.Data)),
            () => Task.CompletedTask,
            output,
            diagnostics,
            failure => Refusal(failure));

        var frame = Assert.Single(Frames(output));
        Assert.Equal(
            ServeStartupRefusal.RemediationText,
            frame.GetProperty("remediation").GetString());
    }

    [Fact]
    public async Task RefusedStartupStateSerializesAsTheCamelCaseTerminalState()
    {
        var recovery = Recovery(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            1684);
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        await ServeCommand.RunLifecycleAsync(
            new FakeOrphanCleaner(recovery),
            new FakeCoordinator(Result.Ok(recovery.Data)),
            () => Task.CompletedTask,
            output,
            diagnostics,
            failure => Refusal(failure));

        var frame = Assert.Single(Frames(output));
        Assert.Equal("processExitUnconfirmed", frame.GetProperty("state").GetString());
    }

    [Fact]
    public async Task RefusedStartupAlsoLogsOneBoundedDiagnosticLine()
    {
        var recovery = Recovery(
            ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            ManagedEtabsShutdownState.IdentityMismatch,
            42);
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        await ServeCommand.RunLifecycleAsync(
            new FakeOrphanCleaner(recovery),
            new FakeCoordinator(Result.Ok(recovery.Data)),
            () => Task.CompletedTask,
            output,
            diagnostics,
            failure => Refusal(failure));

        var lines = diagnostics.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Assert.Single(lines);
        Assert.Contains("startup=refused", line, StringComparison.Ordinal);
        Assert.Contains(ManagedEtabsShutdownErrorCodes.IdentityMismatch, line, StringComparison.Ordinal);
        Assert.Contains("ownedPid=42", line, StringComparison.Ordinal);
        Assert.True(line.Length <= 2048);
    }

    [Fact]
    public async Task RefusalErrorAndRecordPathAreBounded()
    {
        var recovery = new ManagedEtabsShutdownResult(
            false,
            ManagedEtabsShutdownErrorCodes.IdentityMismatch,
            new string('x', 5_000) + "\r\n",
            new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.IdentityMismatch,
                ProcessExitConfirmed: false,
                Forced: false,
                RecordRetained: true,
                ApplicationExitReturnCode: null,
                OwnedPid: 42));
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        await ServeCommand.RunLifecycleAsync(
            new FakeOrphanCleaner(recovery),
            new FakeCoordinator(Result.Ok(recovery.Data)),
            () => Task.CompletedTask,
            output,
            diagnostics,
            failure => ServeStartupRefusal.FromAssembly(
                typeof(ServeHandshake).Assembly,
                4321,
                Path.GetFullPath(Environment.ProcessPath!),
                failure,
                new string('p', 5_000)));

        var frame = Assert.Single(Frames(output));
        Assert.True(frame.GetProperty("error").GetString()!.Length <= 2048);
        Assert.True(frame.GetProperty("recordPath").GetString()!.Length <= 2048);
        Assert.DoesNotContain(frame.GetProperty("error").GetString()!, char.IsControl);
    }

    [Fact]
    public async Task SuccessfulRecoveryRunsTheLoopWithoutWritingAStartupRefusal()
    {
        var cleaner = new FakeOrphanCleaner(new(
            true,
            null,
            null,
            new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.Succeeded,
                ProcessExitConfirmed: true,
                Forced: false,
                RecordRetained: false,
                ApplicationExitReturnCode: null,
                OwnedPid: null)));
        var coordinator = new FakeCoordinator(Result.Ok(new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: false,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: 42)));
        var loopCalls = 0;
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        var outcome = await ServeCommand.RunLifecycleAsync(
            cleaner,
            coordinator,
            () =>
            {
                loopCalls++;
                return Task.CompletedTask;
            },
            output,
            diagnostics,
            failure => throw new InvalidOperationException(
                "The refusal frame must not be built for a successful recovery."));

        Assert.Equal(ServeLifecycleOutcome.Served, outcome);
        Assert.Equal(1, loopCalls);
        Assert.Empty(output.ToString());
        Assert.Equal(1, coordinator.CallCount);
    }

    [Fact]
    public async Task LoopFailureStillConvergesOnCleanupAndPreservesTheOriginalError()
    {
        var cleaner = new FakeOrphanCleaner(new(
            true,
            null,
            null,
            new ManagedEtabsShutdownData(
                ManagedEtabsShutdownState.Succeeded,
                ProcessExitConfirmed: true,
                Forced: false,
                RecordRetained: false,
                ApplicationExitReturnCode: null,
                OwnedPid: null)));
        var coordinator = new FakeCoordinator(Result.Ok(new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: false,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: 42)));
        await using var output = new StringWriter();
        await using var diagnostics = new StringWriter();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ServeCommand.RunLifecycleAsync(
                cleaner,
                coordinator,
                () => throw new IOException("stdout failed"),
                output,
                diagnostics,
                failure => Refusal(failure)));

        Assert.Equal("stdout failed", error.Message);
        Assert.Equal(1, coordinator.CallCount);
    }

    private static ManagedEtabsShutdownResult Recovery(
        string errorCode,
        ManagedEtabsShutdownState state,
        int? ownedPid) => new(
        false,
        errorCode,
        "Orphan identity is unreadable.",
        new ManagedEtabsShutdownData(
            state,
            ProcessExitConfirmed: false,
            Forced: false,
            RecordRetained: true,
            ApplicationExitReturnCode: null,
            OwnedPid: ownedPid));

    private static ServeStartupRefusal Refusal(ManagedEtabsShutdownResult recovery) =>
        ServeStartupRefusal.FromAssembly(
            typeof(ServeHandshake).Assembly,
            4321,
            Path.GetFullPath(Environment.ProcessPath!),
            recovery,
            RecordPath);

    private static List<JsonElement> Frames(StringWriter output) =>
        output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();

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
