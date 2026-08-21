// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// Bounded waiting for state this process can only observe, never make happen.
///
/// <para>Injected rather than called statically so every convergence policy in the
/// managed session is exercisable at full speed and with no real sleeping.</para>
/// </summary>
public interface IManagedEtabsClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Yields for one poll interval. Only ever called inside a deadline-bounded loop.</summary>
    void Wait(TimeSpan interval);
}

/// <inheritdoc />
public sealed class SystemManagedEtabsClock : IManagedEtabsClock
{
    public static readonly SystemManagedEtabsClock Instance = new();

    private SystemManagedEtabsClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Wait(TimeSpan interval) => Thread.Sleep(interval);
}

/// <summary>
/// Which way a visibility transition was asked to go. Named rather than a bare bool so
/// the two product intents — background work and an explicit user request — cannot be
/// read as the same thing at a call site.
/// </summary>
public enum ManagedEtabsVisibilityIntent
{
    /// <summary>Background work: ETABS must not appear on screen or in the taskbar.</summary>
    Hidden,

    /// <summary>An explicit user action asked to see ETABS.</summary>
    Visible
}

/// <summary>
/// How long the CSI application is given to actually reach a state it already accepted a
/// transition to.
///
/// <para>The #20 live certification is the evidence: <c>cOAPI.Hide()</c> returned success
/// and the very next <c>cOAPI.Visible()</c> read still said visible, while independent
/// HWND telemetry agreed with <c>Visible()</c>. The oracle was right and the actuator was
/// late — the window went away 5.19 s after it appeared. A policy that declares failure on
/// the first read has no model of a deferred CSI transition at all, which is why the
/// candidate reported "not confirmed" twice for a hide that did, eventually, land.</para>
/// </summary>
/// <param name="ConvergenceDeadline">
/// The ceiling on that wait. Ten seconds is roughly twice the residual the supervised run
/// measured, and it is a ceiling rather than a delay: convergence normally returns on the
/// first read.
/// </param>
/// <param name="PollInterval">How often <c>cOAPI.Visible()</c> is re-read while converging.</param>
/// <param name="Clock">The bounded-wait seam.</param>
public sealed record ManagedEtabsVisibilityPolicy(
    TimeSpan ConvergenceDeadline,
    TimeSpan PollInterval,
    IManagedEtabsClock Clock)
{
    public static ManagedEtabsVisibilityPolicy Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(100),
        SystemManagedEtabsClock.Instance);

    /// <summary>A zero or negative interval would make the convergence loop unbounded.</summary>
    public TimeSpan PollInterval { get; } = PollInterval > TimeSpan.Zero
        ? PollInterval
        : throw new ArgumentOutOfRangeException(nameof(PollInterval));
}

/// <summary>
/// What a visibility transition actually established.
/// </summary>
/// <param name="Intent">The state that was requested.</param>
/// <param name="Confirmed">
/// Whether ETABS was re-read afterwards and agreed. A zero return code is NOT enough:
/// the same "returned success but the state says otherwise" class of defect that
/// <c>cFile.OpenFile</c> shipped with applies here too.
/// </param>
/// <param name="Changed">
/// Whether a CSI transition call was actually issued. False means the application was
/// already in the requested state, which is not an error and must not be one — see
/// <see cref="ManagedEtabsVisibility"/> for why calling anyway would be.
/// </param>
/// <param name="Diagnostic">Bounded failure text when <paramref name="Confirmed"/> is false.</param>
/// <param name="Reads">
/// How many <c>cOAPI.Visible()</c> reads the outcome rests on. Exactly the measurement the
/// #20 run could not reconstruct afterwards: it says whether the hide landed instantly or
/// had to be waited out.
/// </param>
/// <param name="Waited">How long convergence took, or ran for before giving up.</param>
public sealed record ManagedEtabsVisibilityOutcome(
    ManagedEtabsVisibilityIntent Intent,
    bool Confirmed,
    bool Changed,
    string? Diagnostic,
    int Reads = 0,
    TimeSpan Waited = default);

/// <summary>
/// The three CSI application-visibility calls behind one named seam, so the whole
/// policy is exercisable without ETABS or COM.
///
/// <para>Cardex (ETABS 23.3, <c>cOAPI</c>) documents all three: <c>Hide()</c> returns
/// zero when the application is hidden, <c>Unhide()</c> returns zero when it is made
/// visible, and <c>Visible()</c> returns true when the application is on screen.</para>
/// </summary>
public interface IEtabsVisibilityApi
{
    /// <summary>Raw <c>cOAPI.Visible()</c>. True when ETABS is on screen and in the taskbar.</summary>
    bool Visible();

    /// <summary>Raw <c>cOAPI.Hide()</c>. Zero means hidden.</summary>
    int Hide();

    /// <summary>Raw <c>cOAPI.Unhide()</c>. Zero means visible.</summary>
    int Unhide();
}

/// <summary>
/// The one place the managed ETABS session decides whether the application is on
/// screen, and the only place that reasons about the CSI visibility contract.
///
/// <para><b>Why check before acting.</b> Cardex is explicit that these calls are not
/// idempotent: <c>cOAPI.Hide</c> — "If the application is already hidden, calling this
/// function returns an error"; <c>cOAPI.Unhide</c> — "If the application is already
/// visible (not hidden) calling this function returns an error." So a policy that just
/// called <c>Hide()</c> would manufacture a failure every time it was already right.
/// Every transition therefore reads <c>cOAPI.Visible()</c> first and calls nothing when
/// the application is already in the requested state.</para>
///
/// <para><b>Why exactly one transition.</b> The same non-idempotency is why convergence
/// polls the STATE and never re-issues the transition. Hammering <c>Hide()</c> while a
/// first <c>Hide()</c> is still landing would turn a slow success into a manufactured
/// error, which is precisely the failure the #20 candidate reported.</para>
///
/// <para><b>Why re-read afterwards, and more than once.</b> A zero return proves the call
/// was accepted, not that the state changed. The supervised #20 run proved the gap is real
/// and measured in seconds, so the primitive re-reads <c>Visible()</c> to a bounded
/// deadline and reports <see cref="ManagedEtabsVisibilityOutcome.Confirmed"/> from the
/// observed state — "hidden" is something this process saw, not something it asked
/// for.</para>
///
/// <para><b>Why the state outranks the return code.</b> A non-zero return is re-read once
/// rather than trusted: Cardex's own documented cause of a non-zero <c>Hide</c> is "already
/// hidden", which is the state the caller wanted. The observation decides; the return code
/// only supplies the diagnostic when the observation disagrees.</para>
///
/// <para><b>What Cardex does not say.</b> ETABS 23.3 documents exactly one overload,
/// <c>int ApplicationStart()</c> — no visibility argument and no second overload — and
/// it does not state what the application's visibility is after it returns. That is why
/// nothing here assumes a starting state.</para>
/// </summary>
public static class ManagedEtabsVisibility
{
    public const string ReadOperation = "cOAPI.Visible";
    public const string HideOperation = "cOAPI.Hide";
    public const string UnhideOperation = "cOAPI.Unhide";

    /// <summary>
    /// Background-work state: ETABS must not appear on screen or in the Windows taskbar.
    ///
    /// <para>The convergence policy is always passed explicitly. A convenience overload
    /// that supplied the default would call this one, and the whole-assembly wiring guard
    /// reads the call graph — a seam that calls itself is a seam whose callers can no
    /// longer be enumerated.</para>
    /// </summary>
    public static ManagedEtabsVisibilityOutcome EnsureHidden(
        IEtabsVisibilityApi api,
        ManagedEtabsVisibilityPolicy policy) =>
        Ensure(api, ManagedEtabsVisibilityIntent.Hidden, policy);

    /// <summary>
    /// Explicit-user-action state: ETABS is shown normally. Only ever called once the
    /// requested model has been confirmed open — an empty window is the symptom, not the
    /// goal.
    /// </summary>
    public static ManagedEtabsVisibilityOutcome EnsureVisible(
        IEtabsVisibilityApi api,
        ManagedEtabsVisibilityPolicy policy) =>
        Ensure(api, ManagedEtabsVisibilityIntent.Visible, policy);

    private static ManagedEtabsVisibilityOutcome Ensure(
        IEtabsVisibilityApi api,
        ManagedEtabsVisibilityIntent intent,
        ManagedEtabsVisibilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(policy);
        var wantVisible = intent == ManagedEtabsVisibilityIntent.Visible;

        var reads = 1;
        bool before;
        try
        {
            before = api.Visible();
        }
        catch (Exception exception)
        {
            return NotConfirmed(
                intent,
                changed: false,
                EtabsApiDiagnosticFormatter.Exception(ReadOperation, exception),
                reads,
                TimeSpan.Zero);
        }

        if (before == wantVisible)
        {
            // Already right. Calling the transition anyway would return an error for the
            // state we wanted, per the Cardex remarks on Hide and Unhide.
            return new(
                intent,
                Confirmed: true,
                Changed: false,
                Diagnostic: null,
                reads,
                TimeSpan.Zero);
        }

        var operation = wantVisible ? UnhideOperation : HideOperation;
        int returnCode;
        try
        {
            // The ONE and only transition this call issues. Everything below observes.
            returnCode = wantVisible ? api.Unhide() : api.Hide();
        }
        catch (Exception exception)
        {
            return NotConfirmed(
                intent,
                changed: true,
                EtabsApiDiagnosticFormatter.Exception(operation, exception),
                reads,
                TimeSpan.Zero);
        }

        // An accepted transition gets the full convergence budget; a rejected one gets a
        // single confirming read, because a non-zero return is most often "already in the
        // requested state" and waiting on a call ETABS refused would only delay the truth.
        var budget = returnCode == 0 ? policy.ConvergenceDeadline : TimeSpan.Zero;
        var started = policy.Clock.UtcNow;
        while (true)
        {
            bool observed;
            reads++;
            try
            {
                observed = api.Visible();
            }
            catch (Exception exception)
            {
                return NotConfirmed(
                    intent,
                    changed: true,
                    EtabsApiDiagnosticFormatter.Exception(ReadOperation, exception),
                    reads,
                    policy.Clock.UtcNow - started);
            }

            var waited = policy.Clock.UtcNow - started;
            if (observed == wantVisible)
            {
                return new(
                    intent,
                    Confirmed: true,
                    Changed: true,
                    Diagnostic: null,
                    reads,
                    waited);
            }

            if (waited >= budget)
            {
                return NotConfirmed(
                    intent,
                    changed: true,
                    returnCode != 0
                        ? EtabsApiDiagnosticFormatter.ApiReturn(operation, returnCode)
                        : Contradicted(operation, wantVisible, observed, reads, waited),
                    reads,
                    waited);
            }

            policy.Clock.Wait(policy.PollInterval);
        }
    }

    private static ManagedEtabsVisibilityOutcome NotConfirmed(
        ManagedEtabsVisibilityIntent intent,
        bool changed,
        string diagnostic,
        int reads,
        TimeSpan waited) => new(intent, Confirmed: false, changed, diagnostic, reads, waited);

    private static string Contradicted(
        string operation,
        bool wantVisible,
        bool observed,
        int reads,
        TimeSpan waited) =>
        EtabsApiDiagnosticFormatter.Bounded(string.Join(
            "; ",
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            $"operation={operation}",
            $"requested={Describe(wantVisible)}",
            $"observed={Describe(observed)}",
            $"reads={reads}",
            $"waitedMs={(long)waited.TotalMilliseconds}",
            "ETABS accepted the transition but never reached the requested application " +
            "visibility within the bounded convergence deadline."));

    private static string Describe(bool visible) => visible ? "visible" : "hidden";
}
