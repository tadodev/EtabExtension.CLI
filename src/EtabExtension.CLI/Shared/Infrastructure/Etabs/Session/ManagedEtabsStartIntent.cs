// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// The caller's declared intent for a COLD ETABS start.
///
/// <para>CLI #22 established, over seven supervised live runs, that a strictly invisible
/// cold start is not achievable on the supported ETABS 23.3 path. ETABS puts its splash
/// and then its main window on screen during <c>ApplicationStart</c>, the API cannot be
/// used to hide it before that call returns, and hiding it from outside the process kills
/// ETABS. The measured exposure was 8.76 s and 13.42 s in the two runs that completed
/// startup.</para>
///
/// <para>So the product stops pretending. Startup visibility becomes an explicit, declared
/// state rather than a surprise — and because the CLI cannot retroactively turn an
/// unexpected window into consent, the declaration has to arrive from the caller that
/// spoke to the engineer.</para>
/// </summary>
public enum ManagedEtabsStartIntent
{
    /// <summary>
    /// Nothing was declared. A cold start is REFUSED. This is the default on purpose: an
    /// absent field must never be readable as agreement.
    /// </summary>
    Unspecified,

    /// <summary>
    /// The caller told the engineer that ETABS will appear on screen while it starts, and
    /// the engineer agreed to continue. Only this value permits process creation.
    /// </summary>
    VisibleByConsent
}

/// <summary>
/// The wire spelling of <see cref="ManagedEtabsStartIntent"/>, and the parser for it.
/// </summary>
public static class ManagedEtabsStartIntents
{
    /// <summary>
    /// The one accepted wire value. Deliberately verbose: a caller cannot type this by
    /// accident, and a reviewer reading a request line can see what was promised.
    /// </summary>
    public const string VisibleByConsent = "visible-start-consented";

    /// <summary>
    /// Parses a wire value. Anything unrecognised — absent, empty, misspelled, or a value
    /// from a future protocol this build does not understand — is
    /// <see cref="ManagedEtabsStartIntent.Unspecified"/> and therefore refused. Failing
    /// closed on an unknown value is the point: a newer desktop must not be able to
    /// cold-start an older sidecar by sending a token it has never heard of.
    /// </summary>
    public static ManagedEtabsStartIntent Parse(string? wireValue) =>
        string.Equals(wireValue, VisibleByConsent, StringComparison.Ordinal)
            ? ManagedEtabsStartIntent.VisibleByConsent
            : ManagedEtabsStartIntent.Unspecified;
}

/// <summary>
/// Carries the current request's declared start intent to the one place that can act on
/// it — the session, at the moment it would otherwise create a process.
///
/// <para><b>Why an ambient scope rather than a parameter on every command.</b> The intent
/// belongs to the REQUEST, not to any one command's payload. Threading it through all
/// twenty command signatures would duplicate the field twenty times, and each new command
/// would be one more place to forget it — which is the failure mode this gate exists to
/// prevent. The serve loop dispatches strictly one request at a time (ETABS COM is
/// single-threaded and the loop is serial by construction), so a single ambient value is
/// unambiguous for the request in flight.</para>
///
/// <para>It is cleared after every request so an earlier consent can never be reused by a
/// later one that did not declare it.</para>
/// </summary>
public interface IManagedEtabsStartIntentScope
{
    /// <summary>The intent declared by the request currently being dispatched.</summary>
    ManagedEtabsStartIntent Current { get; }

    /// <summary>
    /// Publishes the intent for one request and returns a scope that clears it. Always use
    /// with <c>using</c>: a leaked value would let the NEXT request inherit consent.
    /// </summary>
    IDisposable Publish(ManagedEtabsStartIntent intent);
}

/// <inheritdoc />
public sealed class ManagedEtabsStartIntentScope : IManagedEtabsStartIntentScope
{
    private ManagedEtabsStartIntent _current = ManagedEtabsStartIntent.Unspecified;

    /// <inheritdoc />
    public ManagedEtabsStartIntent Current => _current;

    /// <inheritdoc />
    public IDisposable Publish(ManagedEtabsStartIntent intent)
    {
        _current = intent;
        return new Reset(this);
    }

    private sealed class Reset(ManagedEtabsStartIntentScope owner) : IDisposable
    {
        public void Dispose() => owner._current = ManagedEtabsStartIntent.Unspecified;
    }
}
