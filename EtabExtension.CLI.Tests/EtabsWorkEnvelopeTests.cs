using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The seam where the visibility contract crosses an execution boundary.
///
/// <para>Everything CLI #22/#24/#25 promises is easy to get right while one thread runs one
/// request start to finish. It stops being easy the moment work is queued: the request that
/// carried the engineer's consent has already been answered, another request may be in
/// flight, and the work that actually starts ETABS runs on a different thread minutes
/// later. These tests are about that gap and nothing else.</para>
/// </summary>
public sealed class EtabsWorkEnvelopeTests
{
    /// <summary>
    /// THE deferred-consent property. Capture happens while the request is alive; the
    /// request then ends, exactly as the serve loop ends it; the work runs afterwards and
    /// must still be operating under the consent the engineer actually gave.
    /// </summary>
    [Fact]
    public async Task WorkCapturedUnderConsentStillHasItAfterTheRequestHasEnded()
    {
        var declared = new ManagedEtabsStartIntentScope();
        var execution = new EtabsWorkScope();
        var session = new RecordingSession();
        var envelope = WorkEnvelopeFixtures.Over(session, declared, execution);

        EtabsWorkContext captured;
        using (declared.Publish(ManagedEtabsStartIntent.VisibleByConsent))
        {
            captured = envelope.Capture("analyze-and-extract");
        }

        // The request is over. This is where the old ambient field had already decayed.
        Assert.Equal(ManagedEtabsStartIntent.Unspecified, declared.Current);

        ManagedEtabsStartIntent seenByTheWork = ManagedEtabsStartIntent.Unspecified;
        await envelope.RunAsync(captured, () =>
        {
            seenByTheWork = execution.Current.StartIntent;
            return Task.FromResult<object>(Result.Ok());
        });

        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, seenByTheWork);
    }

    /// <summary>
    /// And the reverse, which is the dangerous direction: a request that declared NOTHING
    /// must not be able to inherit consent from work that is already running. Polling
    /// <c>get-operation-status</c> during a long analysis is precisely this shape.
    /// </summary>
    [Fact]
    public async Task AConcurrentUndeclaredRequestCannotStealTheRunningWorkersConsent()
    {
        var declared = new ManagedEtabsStartIntentScope();
        var execution = new EtabsWorkScope();
        var envelope = WorkEnvelopeFixtures.Over(new RecordingSession(), declared, execution);

        using var consented = declared.Publish(ManagedEtabsStartIntent.VisibleByConsent);
        var running = envelope.Capture("analyze-and-extract");

        // A second request arrives mid-operation, declaring nothing.
        var polling = envelope.Capture("get-operation-status");

        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, running.StartIntent);
        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, polling.StartIntent);

        // The captures are values, so running work cannot be edited by a later one.
        ManagedEtabsStartIntent duringWork = ManagedEtabsStartIntent.Unspecified;
        await envelope.RunAsync(running, () =>
        {
            _ = envelope.Capture("get-operation-status");
            duringWork = execution.Current.StartIntent;
            return Task.FromResult<object>(Result.Ok());
        });

        Assert.Equal(ManagedEtabsStartIntent.VisibleByConsent, duringWork);
        Assert.Equal(EtabsWorkContext.None, execution.Current);
    }

    /// <summary>An undeclared request captures no consent. The gate is not simply always-on.</summary>
    [Fact]
    public async Task WorkCapturedWithoutADeclarationCarriesNoConsent()
    {
        var declared = new ManagedEtabsStartIntentScope();
        var execution = new EtabsWorkScope();
        var envelope = WorkEnvelopeFixtures.Over(new RecordingSession(), declared, execution);

        var captured = envelope.Capture("snapshot-export");

        ManagedEtabsStartIntent seen = ManagedEtabsStartIntent.VisibleByConsent;
        await envelope.RunAsync(captured, () =>
        {
            seen = execution.Current.StartIntent;
            return Task.FromResult<object>(Result.Ok());
        });

        Assert.Equal(ManagedEtabsStartIntent.Unspecified, seen);
    }

    /// <summary>The interval is labelled before the work runs, not after it.</summary>
    [Fact]
    public async Task TheStageIsAppliedBeforeTheWorkStarts()
    {
        var session = new RecordingSession();
        var envelope = WorkEnvelopeFixtures.Consented(session);

        string[] stagesWhenWorkRan = [];
        await envelope.RunAsync(envelope.Capture("run-analysis"), () =>
        {
            stagesWhenWorkRan = [.. session.Stages];
            return Task.FromResult<object>(Result.Ok());
        });

        Assert.Equal(["run-analysis"], stagesWhenWorkRan);
    }

    /// <summary>
    /// CLI #24 at the wire: a clean session keeps its own result, a breached one does not.
    /// </summary>
    [Fact]
    public async Task ACleanSessionKeepsTheWorksOwnResult()
    {
        var session = new RecordingSession();
        var envelope = WorkEnvelopeFixtures.Consented(session);
        var expected = Result.Ok();

        var returned = await envelope.RunAsync(
            envelope.Capture("snapshot-export"),
            () => Task.FromResult<object>(expected));

        Assert.Same(expected, returned);
        Assert.Equal(1, session.CertifyCalls);
    }

    [Fact]
    public async Task ABreachedSessionReplacesASuccessfulResult()
    {
        var session = new RecordingSession
        {
            Certification = Result.Fail("ETABS_WINDOW_UNCONSENTED_EXPOSURE; observations=2")
        };
        var envelope = WorkEnvelopeFixtures.Consented(session);

        var returned = await envelope.RunAsync(
            envelope.Capture("snapshot-export"),
            () => Task.FromResult<object>(Result.Ok()));

        var result = Assert.IsType<Result>(returned, exactMatch: false);
        Assert.False(result.Success);
        Assert.Contains(
            "ETABS_WINDOW_UNCONSENTED_EXPOSURE",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The exception-safe half. Work that THROWS is work that may have thrown because
    /// something appeared on screen; skipping the certification there leaves the session
    /// ready and unwatched, which is the lifecycle hole rather than the wire hole.
    ///
    /// <para>The command's own exception is preserved exactly. Callers upstream branch on
    /// its type, and replacing it with a visibility diagnostic would be a lie when
    /// visibility held.</para>
    /// </summary>
    [Fact]
    public async Task WorkThatThrowsIsStillCertifiedAndItsExceptionIsPreserved()
    {
        var session = new RecordingSession();
        var envelope = WorkEnvelopeFixtures.Consented(session);
        var thrown = new InvalidOperationException("cSapModel.File.OpenFile returned 1");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            envelope.RunAsync(
                envelope.Capture("open-model"),
                () => Task.FromException<object>(thrown)));

        Assert.Same(thrown, caught);
        Assert.Equal(1, session.CertifyCalls);
    }

    /// <summary>
    /// When BOTH contracts broke, neither fact may be dropped. The command error is what
    /// the engineer asked about; the visibility breach is why the session is now gone.
    /// </summary>
    [Fact]
    public async Task WhenTheWorkThrewAndVisibilityBrokeBothFactsSurvive()
    {
        var session = new RecordingSession
        {
            Certification = Result.Fail("ETABS_WINDOW_UNCONSENTED_EXPOSURE; observations=4")
        };
        var envelope = WorkEnvelopeFixtures.Consented(session);

        var returned = await envelope.RunAsync(
            envelope.Capture("extract-results"),
            () => Task.FromException<object>(
                new InvalidOperationException("cSapModel.Results.BaseReact returned 1")));

        var result = Assert.IsType<Result>(returned, exactMatch: false);
        Assert.False(result.Success);
        Assert.Contains(
            "ETABS_WINDOW_UNCONSENTED_EXPOSURE",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("BaseReact returned 1", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A session under a captured context is left as it was found.</summary>
    [Fact]
    public async Task TheExecutionScopeIsRestoredAfterTheWork()
    {
        var execution = new EtabsWorkScope();
        var envelope = WorkEnvelopeFixtures.Over(
            new RecordingSession(),
            new ManagedEtabsStartIntentScope(),
            execution);

        await envelope.RunAsync(
            new EtabsWorkContext(ManagedEtabsStartIntent.VisibleByConsent, "outer"),
            async () =>
            {
                await envelope.RunAsync(
                    new EtabsWorkContext(ManagedEtabsStartIntent.Unspecified, "inner"),
                    () => Task.FromResult<object>(Result.Ok()));

                // The inner unit ended; the outer one is still the work in flight.
                Assert.Equal("outer", execution.Current.Stage);
                return Result.Ok();
            });

        Assert.Equal(EtabsWorkContext.None, execution.Current);
    }

    private sealed class RecordingSession : IEtabsSession
    {
        public Result Certification { get; set; } = Result.Ok();
        public List<string> Stages { get; } = [];
        public int CertifyCalls { get; private set; }

        public bool IsStarted => true;
        public int? ProcessId => 42;
        public ETABSApplication GetOrStart() => null!;
        public IManagedEtabsApplication GetOrStartOwned() => null!;
        public Result RevealForExplicitUserRequest() => Result.Ok();

        public Result CertifyNoUnconsentedExposure()
        {
            CertifyCalls++;
            return Certification;
        }

        public void MarkVisibilityStage(string stage) => Stages.Add(stage);
        public ManagedEtabsShutdownResult Shutdown() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
