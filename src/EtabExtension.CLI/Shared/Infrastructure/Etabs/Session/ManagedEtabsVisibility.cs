// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

/// <summary>
/// Bounded waiting for state this process can only observe, never make happen.
///
/// <para><b>Monotonic, not wall clock.</b> The surface is a timestamp and an elapsed-since,
/// deliberately not a "now": a deadline computed by subtracting two wall-clock readings is
/// not a bound at all. An NTP correction or a manual clock change backwards silently
/// extends it — potentially without limit — and one forwards ends it early, which for the
/// hide convergence would mean declaring an unproven visibility failure the moment the
/// machine's clock was adjusted. There is no <c>UtcNow</c> here so that no deadline in the
/// managed session can be written against one by accident.</para>
///
/// <para>Injected rather than called statically so every convergence policy in the
/// managed session is exercisable at full speed and with no real sleeping.</para>
/// </summary>
public interface IManagedEtabsClock
{
    /// <summary>An opaque monotonic tick, meaningful only to <see cref="ElapsedSince"/>.</summary>
    long Timestamp { get; }

    /// <summary>How much monotonic time has passed since a <see cref="Timestamp"/> reading.</summary>
    TimeSpan ElapsedSince(long timestamp);

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

    /// <summary>
    /// <c>Stopwatch</c>, which is backed by the platform's high-resolution performance
    /// counter and is unaffected by wall-clock changes.
    /// </summary>
    public long Timestamp => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public TimeSpan ElapsedSince(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    public void Wait(TimeSpan interval) => Thread.Sleep(interval);
}

/// <summary>
/// Which way a visibility transition was asked to go. Named rather than a bare bool so
/// the two product intents — background work and an explicit user request — cannot be
/// read as the same thing at a call site.
/// </summary>
public enum ManagedEtabsVisibilityIntent
{
    /// <summary>ETABS must not be on screen or in the taskbar.</summary>
    Hidden,

    /// <summary>The engineer asked to see ETABS, and the requested model is open.</summary>
    Visible
}

/// <summary>
/// What a CSI visibility transition reported. Telemetry — never a verdict.
/// </summary>
/// <param name="Intent">The state that was requested.</param>
/// <param name="Issued">
/// Whether the CSI call completed at all. FALSE means it threw, which is an explicit
/// transition failure the caller must treat as fatal: nothing was asked of ETABS and no
/// amount of observing will change that.
/// </param>
/// <param name="Confirmed">
/// Whether the call returned zero. This is NOT a claim about the resulting state. Cardex
/// documents a non-zero return for "already in the requested state", and #20 measured
/// <c>Visible()</c> disagreeing with reality in one direction while Diagnostic&#160;#4
/// measured it disagreeing in the other. The exact-owned Windows census decides.
/// </param>
/// <param name="ReturnCode">The raw CSI return code, recorded verbatim.</param>
/// <param name="CsiVisibleAfter">
/// What <c>cOAPI.Visible()</c> claimed immediately afterwards, or null if the read failed
/// or was not reached. Logged so a build where CSI does track its own state can be
/// recognised later; it gates nothing.
/// </param>
/// <param name="Diagnostic">Bounded text whenever anything was not plainly nominal.</param>
public sealed record ManagedEtabsVisibilityOutcome(
    ManagedEtabsVisibilityIntent Intent,
    bool Issued,
    bool Confirmed,
    int ReturnCode,
    bool? CsiVisibleAfter,
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
    /// <summary>Raw <c>cOAPI.Visible()</c>. Telemetry only — never an authority.</summary>
    bool Visible();

    /// <summary>Raw <c>cOAPI.Hide()</c>. Zero means the call was accepted.</summary>
    int Hide();

    /// <summary>Raw <c>cOAPI.Unhide()</c>. Zero means the call was accepted.</summary>
    int Unhide();
}

/// <summary>
/// The one place the managed session ASKS ETABS to change its visibility — and, after
/// CLI&#160;#22, the only thing anywhere that mutates it at all.
///
/// <para><b>Why the transition is always issued.</b> The previous policy read
/// <c>Visible()</c> first and skipped the call when the flag already matched the intent.
/// That was defensible while the flag was believed: Cardex says calling <c>Hide</c> on an
/// already-hidden application returns an error. It is not defensible now. #20 measured the
/// flag stuck true for 94 reads across 10.014&#160;s while the windows were in fact
/// hidden, so a read-first policy issues NO hide exactly when the hide is most needed; and
/// on the reveal path the same stuck flag made the policy issue no <c>Unhide</c> at all,
/// which is why the old design had to put the windows back with <c>ShowWindow</c> instead.
/// Both transitions are therefore unconditional, and the return code is recorded rather
/// than obeyed.</para>
///
/// <para><b>Why a throw is different from a non-zero return.</b> A non-zero return means
/// ETABS considered the request and declined it — most often because it believed it was
/// already in that state — and the census will settle who was right. A throw means the
/// call did not happen: Diagnostic&#160;#3 measured exactly this before
/// <c>ApplicationStart</c> returns, where the API object exists but its WinForms control
/// has no window handle yet. Nothing was asked of ETABS, so the caller must fail rather
/// than wait.</para>
///
/// <para><b>Why nothing converges here any more.</b> There is no polling and no deadline,
/// because there is nothing to poll: <c>Visible()</c> is not an oracle in either
/// direction. The single read after the call is recorded as telemetry so a future ETABS
/// build that does track its own state can be recognised. Actual convergence is observed
/// where it can be trusted — in the exact-owned HWND census.</para>
///
/// <para><b>What Cardex does not say.</b> ETABS 23.3 documents exactly one overload,
/// <c>int ApplicationStart()</c> — no visibility argument and no second overload — and
/// it does not state what the application's visibility is after it returns. That is why
/// nothing here assumes a starting state, and why a strictly hidden cold start is not
/// available on this API path at all.</para>
/// </summary>
public static class ManagedEtabsVisibility
{
    public const string ReadOperation = "cOAPI.Visible";
    public const string HideOperation = "cOAPI.Hide";
    public const string UnhideOperation = "cOAPI.Unhide";

    /// <summary>
    /// Background-work state: asks ETABS to leave the screen and the taskbar. Issued once,
    /// unconditionally, after <c>ApplicationStart()</c> has returned — the only readiness
    /// boundary this API path actually provides.
    /// </summary>
    public static ManagedEtabsVisibilityOutcome ApplyHidden(IEtabsVisibilityApi api) =>
        Apply(api, ManagedEtabsVisibilityIntent.Hidden);

    /// <summary>
    /// Explicit-user-action state: asks ETABS to come back on screen. Issued once,
    /// unconditionally, and only after the requested model has been confirmed open — an
    /// empty window is the symptom, not the goal.
    /// </summary>
    public static ManagedEtabsVisibilityOutcome ApplyVisible(IEtabsVisibilityApi api) =>
        Apply(api, ManagedEtabsVisibilityIntent.Visible);

    private static ManagedEtabsVisibilityOutcome Apply(
        IEtabsVisibilityApi api,
        ManagedEtabsVisibilityIntent intent)
    {
        ArgumentNullException.ThrowIfNull(api);

        var wantVisible = intent == ManagedEtabsVisibilityIntent.Visible;
        var operation = wantVisible ? UnhideOperation : HideOperation;

        int returnCode;
        try
        {
            // THE transition. No read guards it, and nothing below can undo it.
            returnCode = wantVisible ? api.Unhide() : api.Hide();
        }
        catch (Exception exception)
        {
            return new(
                intent,
                Issued: false,
                Confirmed: false,
                ReturnCode: 0,
                CsiVisibleAfter: null,
                EtabsApiDiagnosticFormatter.Exception(operation, exception));
        }

        // One telemetry read. Recorded, never obeyed. A failure to read is not a failure
        // of the transition, so it only colours the diagnostic.
        bool? visibleAfter = null;
        string? readError = null;
        try
        {
            visibleAfter = api.Visible();
        }
        catch (Exception exception)
        {
            readError = EtabsApiDiagnosticFormatter.Exception(ReadOperation, exception);
        }

        return new(
            intent,
            Issued: true,
            Confirmed: returnCode == 0,
            returnCode,
            visibleAfter,
            Describe(operation, wantVisible, returnCode, visibleAfter, readError));
    }

    /// <summary>
    /// Bounded telemetry text, or null when the call was accepted and CSI's own flag
    /// happened to agree — the only combination with nothing worth saying.
    /// </summary>
    private static string? Describe(
        string operation,
        bool wantVisible,
        int returnCode,
        bool? visibleAfter,
        string? readError)
    {
        if (returnCode == 0 && readError is null && visibleAfter == wantVisible)
        {
            return null;
        }

        var fields = new List<string>
        {
            EtabsApiErrorCodes.VisibilityNotConfirmed,
            $"operation={operation}",
            $"requested={State(wantVisible)}",
            $"returnCode={returnCode}",
            $"csiVisibleAfter={(visibleAfter is null ? "unreadable" : State(visibleAfter.Value))}"
        };

        if (readError is not null)
        {
            fields.Add(readError);
        }

        fields.Add(
            "CSI telemetry only - the exact-owned Windows census decides whether this " +
            "transition actually took effect.");

        return EtabsApiDiagnosticFormatter.Bounded(string.Join("; ", fields));
    }

    private static string State(bool visible) => visible ? "visible" : "hidden";
}
