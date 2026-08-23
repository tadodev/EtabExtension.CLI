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
/// Carries the current request or queued operation's declared start intent to the one place
/// that can act on it — the session, at the moment it would otherwise create a process.
///
/// <para><b>Why an ambient scope rather than a parameter on every command.</b> The intent
/// belongs to the REQUEST, not to any one command's payload. Threading it through all
/// command signatures would duplicate the field and create more places to forget the
/// cold-start gate.</para>
///
/// <para><b>Why the value is execution-context local.</b> A queued operation runs on the
/// dedicated STA while the protocol thread remains free to serve later status requests.
/// Those requests must not overwrite the operation's captured consent, and the operation
/// must not leak its consent back to them. <see cref="AsyncLocal{T}"/> gives each logical
/// execution flow its own scope while still surviving async continuations on that flow.</para>
/// </summary>
public interface IManagedEtabsStartIntentScope
{
    /// <summary>The intent declared by the request or operation currently executing.</summary>
    ManagedEtabsStartIntent Current { get; }

    /// <summary>
    /// Publishes one immutable captured intent for the current logical execution flow and
    /// returns a scope that restores the previous value. Always use with <c>using</c>.
    /// </summary>
    IDisposable Publish(ManagedEtabsStartIntent intent);
}

/// <inheritdoc />
public sealed class ManagedEtabsStartIntentScope : IManagedEtabsStartIntentScope
{
    private readonly AsyncLocal<ManagedEtabsStartIntent?> _current = new();

    /// <inheritdoc />
    public ManagedEtabsStartIntent Current =>
        _current.Value ?? ManagedEtabsStartIntent.Unspecified;

    /// <inheritdoc />
    public IDisposable Publish(ManagedEtabsStartIntent intent)
    {
        var previous = _current.Value;
        _current.Value = intent;
        return new Reset(this, previous);
    }

    private sealed class Reset(
        ManagedEtabsStartIntentScope owner,
        ManagedEtabsStartIntent? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
