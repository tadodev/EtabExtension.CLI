using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// Builds the REAL <see cref="EtabsWorkEnvelope"/> for tests, over a fake session.
///
/// <para>Deliberately not a fake envelope. The envelope is the seam that carries consent
/// across the deferred-execution boundary and certifies the visibility contract on
/// completion; a test double for it would let every wiring defect this pass repaired come
/// back silently, because the thing under test would be the double.</para>
/// </summary>
internal static class WorkEnvelopeFixtures
{
    /// <summary>An envelope whose request scope has declared visible-start consent.</summary>
    internal static EtabsWorkEnvelope Consented(IEtabsSession session)
    {
        var declared = new ManagedEtabsStartIntentScope();
        _ = declared.Publish(ManagedEtabsStartIntent.VisibleByConsent);
        return new EtabsWorkEnvelope(declared, new EtabsWorkScope(), session);
    }

    /// <summary>An envelope whose request declared nothing — a cold start under it is refused.</summary>
    internal static EtabsWorkEnvelope Undeclared(IEtabsSession session) =>
        new(new ManagedEtabsStartIntentScope(), new EtabsWorkScope(), session);

    /// <summary>
    /// An envelope over a REQUEST scope the test still owns, so it can end that request —
    /// exactly as the serve loop does — while queued work is still running.
    /// </summary>
    internal static EtabsWorkEnvelope Over(
        IEtabsSession session,
        ManagedEtabsStartIntentScope declared,
        IEtabsWorkScope execution) => new(declared, execution, session);
}
