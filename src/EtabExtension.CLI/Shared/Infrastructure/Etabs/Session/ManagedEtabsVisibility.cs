// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

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
public sealed record ManagedEtabsVisibilityOutcome(
    ManagedEtabsVisibilityIntent Intent,
    bool Confirmed,
    bool Changed,
    string? Diagnostic);

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
/// calls <c>Hide()</c> would manufacture a failure every time it was already right.
/// Every transition therefore reads <c>cOAPI.Visible()</c> first and calls nothing when
/// the application is already in the requested state.</para>
///
/// <para><b>Why re-read afterwards.</b> A zero return proves the call was accepted, not
/// that the state changed. The same primitive re-reads <c>Visible()</c> and reports
/// <see cref="ManagedEtabsVisibilityOutcome.Confirmed"/> from the observed state, so
/// "hidden" is something this process saw rather than something it asked for.</para>
///
/// <para><b>What Cardex does not say.</b> ETABS 23.3 documents exactly one overload,
/// <c>int ApplicationStart()</c> — no visibility argument and no second overload — and
/// it does not state what the application's visibility is after it returns. That is why
/// nothing here assumes a starting state: the observed RC1 behaviour (a window becoming
/// visible ~8.5 s into a background run) is evidence, not documentation, and the policy
/// is written to be correct either way.</para>
/// </summary>
public static class ManagedEtabsVisibility
{
    public const string ReadOperation = "cOAPI.Visible";
    public const string HideOperation = "cOAPI.Hide";
    public const string UnhideOperation = "cOAPI.Unhide";

    /// <summary>
    /// Background-work state: ETABS must not appear on screen or in the Windows taskbar.
    /// </summary>
    public static ManagedEtabsVisibilityOutcome EnsureHidden(IEtabsVisibilityApi api) =>
        Ensure(api, ManagedEtabsVisibilityIntent.Hidden);

    /// <summary>
    /// Explicit-user-action state: ETABS is shown normally. Only ever called once the
    /// requested model has been confirmed open — an empty window is the symptom, not the
    /// goal.
    /// </summary>
    public static ManagedEtabsVisibilityOutcome EnsureVisible(IEtabsVisibilityApi api) =>
        Ensure(api, ManagedEtabsVisibilityIntent.Visible);

    private static ManagedEtabsVisibilityOutcome Ensure(
        IEtabsVisibilityApi api,
        ManagedEtabsVisibilityIntent intent)
    {
        ArgumentNullException.ThrowIfNull(api);
        var wantVisible = intent == ManagedEtabsVisibilityIntent.Visible;

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
                EtabsApiDiagnosticFormatter.Exception(ReadOperation, exception));
        }

        if (before == wantVisible)
        {
            // Already right. Calling the transition anyway would return an error for the
            // state we wanted, per the Cardex remarks on Hide and Unhide.
            return new(intent, Confirmed: true, Changed: false, Diagnostic: null);
        }

        var operation = wantVisible ? UnhideOperation : HideOperation;
        int returnCode;
        try
        {
            returnCode = wantVisible ? api.Unhide() : api.Hide();
        }
        catch (Exception exception)
        {
            return NotConfirmed(
                intent,
                changed: true,
                EtabsApiDiagnosticFormatter.Exception(operation, exception));
        }

        if (returnCode != 0)
        {
            return NotConfirmed(
                intent,
                changed: true,
                EtabsApiDiagnosticFormatter.ApiReturn(operation, returnCode));
        }

        bool after;
        try
        {
            after = api.Visible();
        }
        catch (Exception exception)
        {
            return NotConfirmed(
                intent,
                changed: true,
                EtabsApiDiagnosticFormatter.Exception(ReadOperation, exception));
        }

        return after == wantVisible
            ? new(intent, Confirmed: true, Changed: true, Diagnostic: null)
            : NotConfirmed(intent, changed: true, Contradicted(operation, wantVisible, after));
    }

    private static ManagedEtabsVisibilityOutcome NotConfirmed(
        ManagedEtabsVisibilityIntent intent,
        bool changed,
        string diagnostic) => new(intent, Confirmed: false, changed, diagnostic);

    private static string Contradicted(string operation, bool wantVisible, bool observed) =>
        EtabsApiDiagnosticFormatter.Bounded(string.Join(
            "; ",
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            $"operation={operation}",
            $"requested={Describe(wantVisible)}",
            $"observed={Describe(observed)}",
            "ETABS returned success but still reports the opposite application visibility."));

    private static string Describe(bool visible) => visible ? "visible" : "hidden";
}
