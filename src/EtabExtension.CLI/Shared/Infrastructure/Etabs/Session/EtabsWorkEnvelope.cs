// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.ExceptionServices;
using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// Everything the visibility contract needs to know about ONE unit of ETABS work: what the
/// caller declared when the work was accepted, and what to call the interval in CLI #24's
/// evidence.
///
/// <para>It is a value, captured once and never mutated, because the alternative is what
/// broke: an ambient request-lifetime field consulted later by a worker whose request had
/// already ended. A queued operation must carry its own consent, not go looking for one.
/// </para>
/// </summary>
public readonly record struct EtabsWorkContext(
    ManagedEtabsStartIntent StartIntent,
    string Stage)
{
    /// <summary>Nothing declared, nothing labelled. A cold start under this is refused.</summary>
    public static readonly EtabsWorkContext None =
        new(ManagedEtabsStartIntent.Unspecified, "unattributed");
}

/// <summary>
/// The ambient context of the work currently EXECUTING - as opposed to the request
/// currently being read.
///
/// <para>These are two different lifetimes and conflating them was defect #1 of this pass.
/// The request scope ends when the dispatcher returns; a queued operation runs after that,
/// on another thread, and a later polling request can arrive while it is still going. So
/// the session reads its consent from HERE, and this is entered only by
/// <see cref="EtabsWorkEnvelope"/>, only on the execution worker, only for the duration of
/// one unit of work.</para>
/// </summary>
public interface IEtabsWorkScope
{
    /// <summary>The work in flight on this worker, or <see cref="EtabsWorkContext.None"/>.</summary>
    EtabsWorkContext Current { get; }

    /// <summary>Enters a captured context; disposing restores whatever was there before.</summary>
    IDisposable Enter(EtabsWorkContext context);
}

/// <inheritdoc />
public sealed class EtabsWorkScope : IEtabsWorkScope
{
    private EtabsWorkContext _current = EtabsWorkContext.None;

    /// <inheritdoc />
    public EtabsWorkContext Current => _current;

    /// <inheritdoc />
    public IDisposable Enter(EtabsWorkContext context)
    {
        var previous = _current;
        _current = context;
        return new Restore(this, previous);
    }

    private sealed class Restore(EtabsWorkScope owner, EtabsWorkContext previous) : IDisposable
    {
        public void Dispose() => owner._current = previous;
    }
}

/// <summary>
/// The single seam every unit of ETABS work passes through, whether it runs inline for one
/// request or is queued and executed minutes later.
///
/// <para>It does three things that were previously spread across handlers and therefore
/// only true for the handlers somebody remembered: it CAPTURES the caller's declared intent
/// while that declaration is still valid, it LABELS the interval for CLI #24 evidence, and
/// it CERTIFIES the visibility contract on completion - on every completion, including the
/// ones that threw.</para>
/// </summary>
public interface IEtabsWorkEnvelope
{
    /// <summary>
    /// Freezes the current request's declaration into a context. MUST be called while the
    /// request is still in flight - on the protocol thread, before anything is queued.
    /// </summary>
    EtabsWorkContext Capture(string stage);

    /// <summary>
    /// Runs one unit of work under a captured context. Call this ON the execution worker.
    ///
    /// <para>The returned value is the work's own result unless the visibility contract was
    /// breached, in which case it is the certification failure instead. If the work threw
    /// and visibility was clean, the original exception is rethrown with its stack intact.
    /// </para>
    /// </summary>
    Task<object> RunAsync(EtabsWorkContext context, Func<Task<object>> work);
}

/// <inheritdoc />
public sealed class EtabsWorkEnvelope(
    IManagedEtabsStartIntentScope declared,
    IEtabsWorkScope scope,
    IEtabsSession session) : IEtabsWorkEnvelope
{
    /// <inheritdoc />
    public EtabsWorkContext Capture(string stage) => new(declared.Current, stage);

    /// <inheritdoc />
    public async Task<object> RunAsync(EtabsWorkContext context, Func<Task<object>> work)
    {
        using var entered = scope.Enter(context);

        // Label BEFORE the work. The session remembers the label even when it is not yet
        // ready, and re-applies it the moment it becomes ready, so a cold start's own
        // stages do not end up owning an exposure that happened during this command.
        session.MarkVisibilityStage(context.Stage);

        object? result = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            result = await work();
        }
        catch (Exception ex)
        {
            // Captured, not handled. Certification still has to run: work that threw is
            // work that may well have thrown BECAUSE something surfaced on screen, and a
            // session left ready after that is a session that will do more background work
            // in front of the engineer.
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        var certified = session.CertifyNoUnconsentedExposure();

        if (failure is null)
        {
            return certified.Success ? result! : certified;
        }

        if (certified.Success)
        {
            // The command failed on its own terms and visibility held. Preserve exactly
            // what happened - the caller's error handling is written against this
            // exception, not against a visibility diagnostic that would be a lie here.
            failure.Throw();
        }

        // Both contracts broke. Neither fact may be dropped: the command error is what the
        // engineer asked about, and the visibility breach is why the session is now gone.
        return Result.Fail(EtabsApiDiagnosticFormatter.AppendTerminalFacts(
            certified.Error ?? "The managed ETABS visibility contract was breached.",
            $"commandFailure={EtabsApiDiagnosticFormatter.Bounded(failure.SourceException.Message)}"));
    }
}
