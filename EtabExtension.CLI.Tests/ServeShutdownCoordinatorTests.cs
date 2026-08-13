using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeShutdownCoordinatorTests
{
    [Fact]
    public async Task ConcurrentAndRepeatedShutdownIsOneStaCleanupThenOneWorkerDisposal()
    {
        var events = new List<string>();
        var terminal = Success(forced: true);
        var session = new FakeSession(terminal, events);
        var worker = new ControlledWorker(events);
        var coordinator = new ServeShutdownCoordinator(worker, session);

        var first = coordinator.ShutdownAsync();
        var concurrent = coordinator.ShutdownAsync();

        Assert.Same(first, concurrent);
        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);
        Assert.Equal(["worker-queued"], events);

        await worker.RunQueuedAsync();
        var firstResult = await first;
        var concurrentResult = await concurrent;

        Assert.True(firstResult.Success, firstResult.Error);
        Assert.Same(firstResult, concurrentResult);
        Assert.Same(terminal.Data, firstResult.Data);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Equal(
            [
                "worker-queued",
                "worker-start",
                "session-cleanup",
                "worker-complete",
                "worker-dispose"
            ],
            events);

        var repeated = await coordinator.ShutdownAsync();
        await coordinator.DisposeAsync();

        Assert.Same(firstResult, repeated);
        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task TypedShutdownFailurePreservesTerminalDataAndBoundedStableError()
    {
        var data = new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.ProcessExitUnconfirmed,
            ProcessExitConfirmed: false,
            Forced: true,
            RecordRetained: true,
            ApplicationExitReturnCode: 7,
            OwnedPid: 42);
        var terminal = new ManagedEtabsShutdownResult(
            false,
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            new string('e', 2300) + "\r\n",
            data);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(
            worker,
            new FakeSession(terminal, []));

        var shutdown = coordinator.ShutdownAsync();
        await worker.RunQueuedAsync();
        var result = await shutdown;

        Assert.False(result.Success);
        Assert.Same(data, result.Data);
        Assert.Contains(
            ManagedEtabsShutdownErrorCodes.ProcessExitUnconfirmed,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.Error!, char.IsControl);
        Assert.True(result.Error!.Length <= 2048);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task CallerCancellationOnlyAbandonsThatWaitAndDoesNotCancelSafetyCleanup()
    {
        var terminal = Success(forced: false);
        var session = new FakeSession(terminal, []);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(worker, session);
        var shutdown = coordinator.ShutdownAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => shutdown.WaitAsync(cancellation.Token));
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);

        await worker.RunQueuedAsync();
        var result = await coordinator.ShutdownAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsyncStartsAndAwaitsTheSameShutdownPath()
    {
        var session = new FakeSession(Success(forced: false), []);
        var worker = new ControlledWorker([]);
        var coordinator = new ServeShutdownCoordinator(worker, session);

        var disposal = coordinator.DisposeAsync().AsTask();

        Assert.Equal(1, worker.ExecuteCount);
        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, session.ShutdownCount);
        Assert.Equal(0, worker.DisposeCount);

        await worker.RunQueuedAsync();
        await disposal;
        await coordinator.DisposeAsync();

        Assert.Equal(1, worker.ExecuteCount);
        Assert.Equal(1, session.ShutdownCount);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public void CoordinatorRegistrationIsScoped()
    {
        var services = new ServiceCollection();

        services.AddServeFeature();

        var registration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IServeShutdownCoordinator));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(ServeShutdownCoordinator), registration.ImplementationType);
    }

    private static ManagedEtabsShutdownResult Success(bool forced)
    {
        var data = new ManagedEtabsShutdownData(
            ManagedEtabsShutdownState.Succeeded,
            ProcessExitConfirmed: true,
            Forced: forced,
            RecordRetained: false,
            ApplicationExitReturnCode: 0,
            OwnedPid: 42);
        return new(true, null, null, data);
    }

    private sealed class ControlledWorker(List<string> events) : IStaExecutionWorker
    {
        private IQueuedWork? _queued;

        public int ExecuteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        {
            ExecuteCount++;
            events.Add("worker-queued");
            var queued = new QueuedWork<T>(action, events);
            _queued = queued;
            return queued.Task;
        }

        public Task RunQueuedAsync() =>
            (_queued ?? throw new InvalidOperationException("No work was queued")).RunAsync();

        public void Dispose()
        {
            DisposeCount++;
            events.Add("worker-dispose");
        }

        private interface IQueuedWork
        {
            Task RunAsync();
        }

        private sealed class QueuedWork<T>(
            Func<Task<T>> action,
            List<string> events) : IQueuedWork
        {
            private readonly TaskCompletionSource<T> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<T> Task => _completion.Task;

            public async Task RunAsync()
            {
                events.Add("worker-start");
                try
                {
                    var result = await action();
                    events.Add("worker-complete");
                    _completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
            }
        }
    }

    private sealed class FakeSession(
        ManagedEtabsShutdownResult terminal,
        List<string> events) : IEtabsSession
    {
        public int ShutdownCount { get; private set; }
        public bool IsStarted => true;
        public int? ProcessId => terminal.Data.OwnedPid;
        public ETABSApplication GetOrStart() => throw new NotSupportedException();
        public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();

        public ManagedEtabsShutdownResult Shutdown()
        {
            ShutdownCount++;
            events.Add("session-cleanup");
            return terminal;
        }

        public void Dispose() { }
    }
}
