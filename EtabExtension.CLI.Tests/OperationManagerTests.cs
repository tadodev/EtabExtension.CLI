using System.Text.Json;
using EtabSharp.Core;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class OperationManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-operation-manager-tests", Guid.NewGuid().ToString("N"));
    private OperationManager? _manager;

    [Fact]
    public async Task Start_returns_immediately_while_operation_is_still_running()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation("slow", async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.LongCall", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            return "done";
        }));

        var started = _manager.Start("slow", EmptyPayload());
        Assert.True(started.Success);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var status = _manager.GetStatus(started.Data!.OperationId);
        Assert.Equal(OperationPhase.Running, status.Data!.Phase);
        Assert.Equal("Fake.LongCall", status.Data.CurrentCsiOperation);

        release.SetResult();
        Assert.Equal("done", await _manager.WaitAsync(started.Data.OperationId));
        var completedEvents = _manager.GetEvents(started.Data.OperationId, 0).Data!.Events;
        var completed = Assert.Single(completedEvents, item => item.Type == "operation-succeeded");
        Assert.Equal("done", completed.Data!.Value.GetString());
    }

    [Fact]
    public async Task Events_are_visible_during_execution_and_sequences_are_monotonic()
    {
        var between = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation("steps", async (_, context) =>
        {
            await context.RunStepAsync(1, 2, "Fake.First", () => Task.FromResult(true));
            await context.RunStepAsync(2, 2, "Fake.Second", async () =>
            {
                between.SetResult();
                await release.Task;
                return true;
            });
            return "done";
        }));

        var started = _manager.Start("steps", EmptyPayload());
        await between.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstPoll = _manager.GetEvents(started.Data!.OperationId, 0).Data!;
        Assert.Contains(firstPoll.Events, item => item.CsiOperation == "Fake.Second");

        release.SetResult();
        await _manager.WaitAsync(started.Data.OperationId);
        var replay = _manager.GetEvents(started.Data.OperationId, firstPoll.LastSeq).Data!;
        Assert.All(replay.Events, item => Assert.True(item.Seq > firstPoll.LastSeq));
        Assert.True(replay.Events.SequenceEqual(replay.Events.OrderBy(item => item.Seq)));
    }

    [Fact]
    public async Task Cancellation_is_honored_after_the_current_step_not_during_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStepRan = false;
        _manager = CreateManager(new DelegateOperation("cancel", async (_, context) =>
        {
            await context.RunStepAsync(1, 2, "Fake.Blocking", async () =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            });
            await context.RunStepAsync(2, 2, "Fake.Never", () =>
            {
                secondStepRan = true;
                return Task.FromResult(true);
            });
            return "unexpected";
        }));

        var started = _manager.Start("cancel", EmptyPayload());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = _manager.Cancel(started.Data!.OperationId);
        Assert.Equal(OperationCancellationState.Requested, cancel.Data!.CancellationState);
        Assert.False(secondStepRan);

        release.SetResult();
        await _manager.WaitAsync(started.Data.OperationId);
        var status = _manager.GetStatus(started.Data.OperationId).Data!;
        Assert.Equal(OperationPhase.Cancelled, status.Phase);
        Assert.Equal(OperationCancellationState.Honored, status.CancellationState);
        Assert.False(secondStepRan);
    }

    [Fact]
    public async Task Rejects_a_second_active_operation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation("single", async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.Wait", async () =>
            {
                await release.Task;
                return true;
            });
            return "done";
        }));

        var first = _manager.Start("single", EmptyPayload());
        var second = _manager.Start("single", EmptyPayload());

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains(first.Data!.OperationId, second.Error);
        release.SetResult();
        await _manager.WaitAsync(first.Data.OperationId);
    }

    [Fact]
    public async Task Reports_suspected_hang_when_the_step_budget_is_exceeded()
    {
        var clock = new FakeClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManager(new DelegateOperation(
            "budget", async (_, context) =>
            {
                await context.RunStepAsync(1, 1, "Fake.Slow", async () =>
                {
                    entered.SetResult();
                    await release.Task;
                    return true;
                });
                return "done";
            },
            operationBudget: TimeSpan.FromMinutes(1),
            stepBudget: TimeSpan.FromSeconds(1)), clock);

        var started = _manager.Start("budget", EmptyPayload());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(_manager.GetStatus(started.Data!.OperationId).Data!.SuspectedHang);
        release.SetResult();
        await _manager.WaitAsync(started.Data.OperationId);
    }

    [Fact]
    public async Task Async_continuations_stay_on_the_dedicated_sta_thread()
    {
        int? beforeThread = null;
        int? afterThread = null;
        ApartmentState? apartment = null;
        _manager = CreateManager(new DelegateOperation("sta", async (_, context) =>
        {
            await context.RunStepAsync(1, 1, "Fake.Yield", async () =>
            {
                beforeThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                await Task.Yield();
                afterThread = Environment.CurrentManagedThreadId;
                return true;
            });
            return "done";
        }));

        var started = _manager.Start("sta", EmptyPayload());
        await _manager.WaitAsync(started.Data!.OperationId);

        Assert.Equal(beforeThread, afterThread);
        Assert.Equal(ApartmentState.STA, apartment);
    }

    private OperationManager CreateManager(
        IOperationDefinition definition,
        IOperationClock? clock = null,
        IEtabsSession? session = null) => new(
        new StaExecutionWorker(),
        new OperationEventJournalFactory(_directory, memoryCapacity: 4),
        clock ?? new SystemOperationClock(),
        WorkEnvelopeFixtures.Consented(session ?? new NullVisibilitySession()),
        [definition]);

    /// <summary>
    /// A session that is not started at all. These tests are about operation lifecycle -
    /// phases, journals, cancellation - and a session with no ETABS behind it certifies
    /// clean, which keeps the envelope in the path without it deciding anything here.
    /// </summary>
    private sealed class NullVisibilitySession : IEtabsSession
    {
        public bool IsStarted => false;
        public int? ProcessId => null;
        public ETABSApplication GetOrStart() => throw new InvalidOperationException();
        public IManagedEtabsApplication GetOrStartOwned() => throw new InvalidOperationException();
        public Result RevealForExplicitUserRequest() => Result.Ok();
        public Result CertifyNoUnconsentedExposure() => Result.Ok();
        public void MarkVisibilityStage(string stage) { }
        public ManagedEtabsShutdownResult Shutdown() => throw new InvalidOperationException();
        public void Dispose() { }
    }

    private static JsonElement EmptyPayload() => JsonSerializer.Deserialize<JsonElement>("{}");

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class DelegateOperation(
        string kind,
        Func<JsonElement, OperationExecutionContext, Task<object>> execute,
        TimeSpan? operationBudget = null,
        TimeSpan? stepBudget = null) : IOperationDefinition
    {
        public string Kind { get; } = kind;
        public TimeSpan OperationBudget { get; } = operationBudget ?? TimeSpan.FromMinutes(10);
        public TimeSpan StepBudget { get; } = stepBudget ?? TimeSpan.FromMinutes(5);
        public Task<object> ExecuteAsync(JsonElement payload, OperationExecutionContext context) =>
            execute(payload, context);
    }

    private sealed class FakeClock : IOperationClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
    }
}
