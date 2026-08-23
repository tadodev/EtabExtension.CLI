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

    /// <summary>
    /// THE deferred-consent property, proven against the real queue rather than a fixture
    /// that keeps the request alive forever.
    ///
    /// <para>The STA worker is deliberately occupied first, so the queued operation cannot
    /// begin until after the request that accepted it has ENDED. That is the ordering the
    /// daemon actually produces - <c>start-operation</c> answers with an id immediately,
    /// the serve loop closes the request scope, and the work begins afterwards - and it is
    /// the ordering under which reading the ambient scope at execution time yields
    /// <c>Unspecified</c> for a cold start the engineer did consent to.</para>
    /// </summary>
    [Fact]
    public async Task AQueuedOperationKeepsTheConsentItWasAcceptedWithAfterItsRequestEnded()
    {
        var declared = new ManagedEtabsStartIntentScope();
        var execution = new EtabsWorkScope();
        var envelope = WorkEnvelopeFixtures.Over(
            new NullVisibilitySession(),
            declared,
            execution);

        var seen = ManagedEtabsStartIntent.VisibleByConsent;
        var worker = new StaExecutionWorker();
        _manager = new OperationManager(
            worker,
            new OperationEventJournalFactory(_directory, memoryCapacity: 4),
            new SystemOperationClock(),
            envelope,
            [new DelegateOperation("deferred", (_, _) =>
            {
                seen = execution.Current.StartIntent;
                return Task.FromResult<object>(Result.Ok());
            })]);

        // Occupy the single STA thread so the operation cannot start yet.
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = worker.ExecuteAsync(async () =>
        {
            occupied.SetResult();
            await released.Task;
            return true;
        });
        await occupied.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Result<StartOperationData> started;
        using (declared.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            started = _manager.Start("deferred", EmptyPayload());
        }

        // The request is over before the work has run a single line.
        Assert.Equal(ManagedEtabsStartIntent.Unspecified, declared.Current);
        released.SetResult();
        _ = await blocker;
        _ = await _manager.WaitAsync(
            started.Data!.OperationId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, seen);
    }

    /// <summary>
    /// And an operation accepted WITHOUT a declaration does not acquire one from a later
    /// request that happens to be in flight while it runs. Polling for status during a long
    /// analysis is exactly this shape.
    /// </summary>
    [Fact]
    public async Task AQueuedOperationCannotBorrowConsentFromALaterRequest()
    {
        var declared = new ManagedEtabsStartIntentScope();
        var execution = new EtabsWorkScope();
        var envelope = WorkEnvelopeFixtures.Over(
            new NullVisibilitySession(),
            declared,
            execution);

        var seen = ManagedEtabsStartIntent.VisibleByConsent;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager = CreateManagerWith(envelope, new DelegateOperation("deferred", async (_, _) =>
        {
            running.SetResult();
            await release.Task;
            seen = execution.Current.StartIntent;
            return Result.Ok();
        }));

        // Accepted with nothing declared.
        var started = _manager.Start("deferred", EmptyPayload());
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // A later request declares consent while the operation is still running.
        using (declared.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            release.SetResult();
            _ = await _manager.WaitAsync(
                started.Data!.OperationId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(ManagedEtabsStartIntent.Unspecified, seen);
    }

    /// <summary>
    /// THE lease-poisoning property.
    ///
    /// <para>A <c>start-operation</c> that omits <c>"payload"</c> arrives here as
    /// <c>default(JsonElement)</c> — an element with no backing document, whose
    /// <c>Clone()</c> throws. That copy used to be taken AFTER the operation lease had been
    /// published, so the throw escaped <c>Start</c> with <c>_activeOperationId</c> set and
    /// nothing left alive to clear it: from then on every synchronous ETABS command
    /// answered "a daemon operation is active" and every later start answered "Operation
    /// already active", until the daemon was restarted.</para>
    ///
    /// <para>The ROUND TRIP is the proof, not the refusal on its own: refuse the malformed
    /// request, then accept the very next valid one.</para>
    /// </summary>
    [Fact]
    public async Task AStartWithNoPayloadIsRefusedWithoutCostingTheDaemonItsOperationLease()
    {
        var runs = 0;
        _manager = CreateManager(new DelegateOperation("payload", (_, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<object>(Result.Ok());
        }));

        var refused = _manager.Start("payload", default);

        Assert.False(refused.Success);
        Assert.Contains("payload", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_manager.HasActiveOperation);
        Assert.Equal(0, Volatile.Read(ref runs));

        var accepted = _manager.Start("payload", EmptyPayload());

        Assert.True(accepted.Success);
        _ = await AwaitOperationAsync(accepted.Data!.OperationId);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    /// <summary>
    /// An explicit <c>"payload": null</c> is refused for the same reason. It is a
    /// well-formed element and copies fine, but it carries no operation request — and every
    /// definition would only discover that after the lease was taken, the STA worker
    /// occupied, and a queued/started/failed trio journalled for work that could never have
    /// succeeded. The protocol requires a payload; this fails closed at the door.
    /// </summary>
    [Fact]
    public void AJsonNullPayloadIsRefusedForTheSameReason()
    {
        _manager = CreateManager(new DelegateOperation("payload", (_, _) =>
            Task.FromResult<object>(Result.Ok())));

        var refused = _manager.Start("payload", JsonSerializer.Deserialize<JsonElement>("null"));

        Assert.False(refused.Success);
        Assert.Contains("payload", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_manager.HasActiveOperation);
    }

    /// <summary>
    /// The other half of the same protocol requirement: <c>start-operation</c> carries a
    /// kind AND a payload. A missing kind used to reach the definition dictionary and come
    /// back out as an <c>ArgumentNullException</c>, which a caller can only read as an
    /// internal fault rather than as its own malformed request.
    /// </summary>
    [Fact]
    public void AStartWithNoKindIsRefusedWithAnExplicitError()
    {
        _manager = CreateManager(new DelegateOperation("payload", (_, _) =>
            Task.FromResult<object>(Result.Ok())));

        var refused = _manager.Start(null!, EmptyPayload());

        Assert.False(refused.Success);
        Assert.Contains("kind", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_manager.HasActiveOperation);
    }

    /// <summary>
    /// And a refused start journals nothing. The old order recorded
    /// <c>operation-queued</c> and only then blew up on the payload, leaving a durable
    /// spill file asserting that work had been accepted which no worker would ever pick up.
    /// </summary>
    [Fact]
    public void ARefusedStartLeavesNoJournalClaimingWorkWasQueued()
    {
        _manager = CreateManager(new DelegateOperation("payload", (_, _) =>
            Task.FromResult<object>(Result.Ok())));

        var refused = _manager.Start("payload", default);

        Assert.False(refused.Success);
        Assert.False(
            Directory.Exists(_directory),
            "a refused start must not have created an operation journal");
    }

    /// <summary>
    /// The ordering rule proven from the other side: when the JOURNAL is the thing that
    /// fails, the operation was never published either. The lease is taken last, once
    /// everything that can fail already has not.
    /// </summary>
    [Fact]
    public void AStartWhoseJournalCannotBeWrittenTakesNoLease()
    {
        _manager = CreateManagerJournalling(new FailingJournalFactory(failFromAppend: 1));

        var refused = _manager.Start("payload", EmptyPayload());

        Assert.False(refused.Success);
        Assert.Contains("journal", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(_manager.HasActiveOperation);
    }

    /// <summary>
    /// And the lease comes back even when the operation's FINAL journal write is what
    /// fails. Anything less means one unlucky disk write costs the daemon every subsequent
    /// command — the same permanent poisoning the payload defect caused, only triggered
    /// later.
    /// </summary>
    [Fact]
    public async Task AnOperationWhoseTerminalJournalWriteFailsStillReleasesTheLease()
    {
        _manager = CreateManagerJournalling(new FailingJournalFactory(failFromAppend: 3));

        var started = _manager.Start("payload", EmptyPayload());
        Assert.True(started.Success);
        _ = await AwaitOperationAsync(started.Data!.OperationId);

        Assert.False(_manager.HasActiveOperation);
        var next = _manager.Start("payload", EmptyPayload());
        Assert.True(next.Success);
        _ = await AwaitOperationAsync(next.Data!.OperationId);
    }

    /// <summary>
    /// Waits for one operation to settle, with a bound. A lease that is never released
    /// leaves the completion unset, and an unbounded wait would turn that failure into a
    /// hung test run instead of a red one.
    /// </summary>
    private async Task<object> AwaitOperationAsync(string operationId) =>
        await _manager!
            .WaitAsync(operationId, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

    private OperationManager CreateManagerJournalling(
        IOperationEventJournalFactory journals) => new(
        new StaExecutionWorker(),
        journals,
        new SystemOperationClock(),
        WorkEnvelopeFixtures.Consented(new NullVisibilitySession()),
        [new DelegateOperation("payload", (_, _) => Task.FromResult<object>(Result.Ok()))]);

    /// <summary>
    /// A journal whose spill write fails from the Nth append onwards — the shape of a
    /// directory that vanished or a disk that filled up mid-operation.
    /// </summary>
    private sealed class FailingJournalFactory(int failFromAppend) : IOperationEventJournalFactory
    {
        public IOperationEventJournal Create(string operationId) =>
            new FailingJournal(failFromAppend);

        private sealed class FailingJournal(int failFromAppend) : IOperationEventJournal
        {
            private long _appends;

            public string FilePath => "<test-journal>";
            public long LastSequence => Interlocked.Read(ref _appends);

            public OperationEvent Append(OperationEvent item)
            {
                var sequence = Interlocked.Increment(ref _appends);
                return sequence < failFromAppend
                    ? item with { Seq = sequence }
                    : throw new IOException("Operation journal spill file is unavailable");
            }

            public IReadOnlyList<OperationEvent> ReadSince(long sinceSequence) => [];
        }
    }

    private OperationManager CreateManagerWith(
        IEtabsWorkEnvelope envelope,
        IOperationDefinition definition) => new(
        new StaExecutionWorker(),
        new OperationEventJournalFactory(_directory, memoryCapacity: 4),
        new SystemOperationClock(),
        envelope,
        [definition]);

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
