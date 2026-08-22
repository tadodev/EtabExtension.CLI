// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;

public static class ManagedEtabsWindowErrorCodes
{
    /// <summary>
    /// The exact-owned window census still reported a materially visible top-level window
    /// after the bounded deadline. This is the authoritative background-readiness failure
    /// — <c>cOAPI.Visible()</c> is not.
    /// </summary>
    public const string SuppressionNotConfirmed = "ETABS_WINDOW_SUPPRESSION_NOT_CONFIRMED";

    /// <summary>
    /// The user explicitly asked to see ETABS and no owned top-level window became
    /// materially visible within the bounded deadline. "Open in ETABS" that leaves nothing
    /// on screen has not done what was asked, whatever CSI reports.
    /// </summary>
    public const string RevealNotConfirmed = "ETABS_WINDOW_REVEAL_NOT_CONFIRMED";

    /// <summary>The owned process exited, so its windows can no longer be reasoned about.</summary>
    public const string OwnedProcessGone = "ETABS_WINDOW_OWNED_PROCESS_GONE";

    /// <summary>
    /// An owned window was observed materially on screen AFTER the session had been
    /// confirmed hidden — i.e. outside the interval the engineer consented to. CLI #24:
    /// this is sticky. A later hidden census does not clear it.
    /// </summary>
    public const string UnconsentedExposure = "ETABS_WINDOW_UNCONSENTED_EXPOSURE";
}

/// <summary>
/// A window rectangle in virtual-desktop coordinates, as Windows reports it.
/// </summary>
public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>A degenerate rectangle. Nothing of it can be on screen.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whether this rectangle shares any area with <paramref name="other"/>.</summary>
    public bool Intersects(WindowBounds other) =>
        !IsEmpty
        && !other.IsEmpty
        && Left < other.Right
        && Right > other.Left
        && Top < other.Bottom
        && Bottom > other.Top;

    public override string ToString() => $"{Left},{Top},{Right},{Bottom}";
}

/// <summary>One top-level Windows window, as observed from outside the process that owns it.</summary>
/// <param name="Handle">The raw <c>HWND</c>.</param>
/// <param name="ProcessId">The process that owns the window's thread.</param>
/// <param name="IsVisible">Whether Windows currently considers the window visible.</param>
/// <param name="Bounds">The window rectangle, in virtual-desktop coordinates.</param>
public readonly record struct TopLevelWindow(
    nint Handle,
    int ProcessId,
    bool IsVisible,
    WindowBounds Bounds);

/// <summary>
/// The bounds of the whole virtual desktop, behind a seam so the exposure predicate is
/// exercisable without a real monitor layout.
/// </summary>
public interface IVirtualDesktop
{
    WindowBounds Bounds { get; }
}

/// <summary>
/// The one definition of what counts as ETABS being ON SCREEN.
///
/// <para><b>Why <c>IsWindowVisible</c> alone is not the predicate.</b> The supervised
/// Diagnostic&#160;#4 run observed ETABS's own <c>Analysis Monitor</c> helper window
/// reporting <c>IsWindowVisible=true</c> for 15&#160;ms at
/// <c>32767,234,33407,703</c> — entirely beyond the right edge of a 1920-wide desktop, so
/// no pixel of it ever reached the engineer. Treating that as a contract exposure would
/// fail healthy sessions, which is why CLI&#160;#24 defines the violation predicate as
/// MATERIAL exposure rather than the raw flag.</para>
///
/// <para>Minimized windows fall out of the same rule without a special case: Windows
/// parks them at large negative coordinates, which cannot intersect the desktop. The raw
/// flag and the bounds are still recorded as evidence either way — this decides only what
/// counts as a violation.</para>
/// </summary>
public static class ManagedEtabsWindowExposure
{
    /// <summary>
    /// Whether <paramref name="window"/> is actually presented to the user: Windows says
    /// visible, the rectangle is non-degenerate, and it overlaps the desktop.
    /// </summary>
    public static bool IsMaterial(TopLevelWindow window, WindowBounds desktop) =>
        window.IsVisible
        && !window.Bounds.IsEmpty
        && window.Bounds.Intersects(desktop);
}

/// <summary>
/// Where a managed ETABS session is in the Closed-Alpha visibility contract.
///
/// <para>The contract itself is the ruling that closed CLI&#160;#22: a strictly invisible
/// cold start is not achievable on the supported ETABS&#160;23.3 path, so startup
/// visibility becomes an explicit, consented state rather than a surprise. Seven
/// supervised live runs established the boundary — before <c>ApplicationStart</c> returns,
/// ETABS owns its GUI and the API cannot be used to hide it; after it returns, CSI hides
/// and unhides reliably.</para>
/// </summary>
public enum ManagedEtabsVisibilityState
{
    /// <summary>No managed process exists yet.</summary>
    NoSession,

    /// <summary>
    /// The engineer was told ETABS would appear on screen while it starts and agreed, and
    /// the process has been created. Visibility during this interval is expected and is
    /// NOT a contract violation.
    /// </summary>
    StartingVisibleByConsent,

    /// <summary>
    /// <c>cOAPI.Hide()</c> was issued and the exact-owned census then confirmed no
    /// materially visible window. The consent interval ends HERE: from this point any
    /// material exposure is unconsented and sticky.
    /// </summary>
    BackgroundHidden,

    /// <summary>
    /// The engineer explicitly asked to see ETABS, the requested model was confirmed open,
    /// <c>cOAPI.Unhide()</c> was issued, and the census confirmed a materially visible
    /// owned window. Later background work must never silently take this away.
    /// </summary>
    UserVisible,

    /// <summary>The session is finished; no further transition is possible.</summary>
    Retired
}

/// <summary>
/// One observation of an owned window on screen. Recorded as evidence whether or not it
/// counts as a violation, so a review can see exactly what was seen.
/// </summary>
/// <param name="Handle">The offending <c>HWND</c>.</param>
/// <param name="Bounds">Its rectangle when observed.</param>
/// <param name="SinceProtectedMs">Monotonic milliseconds since the protected interval began.</param>
public readonly record struct ManagedEtabsExposureObservation(
    nint Handle,
    WindowBounds Bounds,
    long SinceProtectedMs);

/// <summary>
/// CLI&#160;#24's temporal evidence: whether the session was ever materially on screen
/// after it was confirmed hidden.
///
/// <para><b>Why this is not a final census.</b> A point-in-time "is it hidden now?" answers
/// the wrong question. Diagnostic&#160;#3 logged "ETABS started hidden" truthfully on a run
/// that had put a full-screen ETABS window in front of the engineer for 8.76&#160;s moments
/// earlier. The product needs "has it stayed off screen for the whole protected interval?",
/// which only accumulated evidence can answer — and which a later hidden observation must
/// never be able to erase.</para>
/// </summary>
/// <param name="Observed">True once material exposure has been seen. Never returns to false.</param>
/// <param name="Observations">How many censuses saw material exposure.</param>
/// <param name="First">The first such observation.</param>
/// <param name="Last">The most recent such observation.</param>
public sealed record ManagedEtabsExposureEvidence(
    bool Observed,
    int Observations,
    ManagedEtabsExposureObservation? First,
    ManagedEtabsExposureObservation? Last)
{
    public static readonly ManagedEtabsExposureEvidence None = new(false, 0, null, null);

    /// <summary>Bounded text naming the offending window, for a failure diagnostic.</summary>
    public string Describe() => Observed && First is { } first && Last is { } last
        ? $"{ManagedEtabsWindowErrorCodes.UnconsentedExposure}; observations={Observations}; " +
            $"firstHandle=0x{first.Handle:X}; firstBounds=[{first.Bounds}]; " +
            $"firstAtMs={first.SinceProtectedMs}; lastAtMs={last.SinceProtectedMs}"
        : "no unconsented exposure observed";
}

/// <summary>
/// What the exact-owned window census established, and what it rests on.
/// </summary>
/// <param name="Confirmed">Whether the requested Windows state was actually observed.</param>
/// <param name="Observations">How many censuses the answer rests on.</param>
/// <param name="Waited">Monotonic time spent reaching it, or spent before giving up.</param>
/// <param name="ObservedWindows">
/// The owned top-level windows the last census saw MATERIALLY visible — the offenders on a
/// failed suppression, the evidence on a confirmed reveal.
/// </param>
/// <param name="Diagnostic">Bounded failure text when <paramref name="Confirmed"/> is false.</param>
public sealed record ManagedEtabsWindowConfirmation(
    bool Confirmed,
    int Observations,
    TimeSpan Waited,
    IReadOnlyList<nint> ObservedWindows,
    string? Diagnostic);

/// <summary>
/// How long the exact-owned window census is given, and how often it re-observes.
/// </summary>
/// <param name="ConfirmationDeadline">
/// Ceiling on a suppression or reveal confirmation. A CSI visibility transition is not
/// synchronous for the caller — Diagnostic&#160;#4 measured the reveal landing 14&#160;ms
/// into a 396&#160;ms <c>Unhide</c> call — so a single read after the call proves nothing.
/// Five seconds is a ceiling, not a delay.
/// </param>
/// <param name="PollInterval">How often the census is re-taken while confirming.</param>
/// <param name="BackstopSweepInterval">
/// How often the observer re-censuses with no window event to prompt it. This is a
/// BACKSTOP for the exposure evidence, not a suppression mechanism — nothing in this layer
/// mutates a window any more.
/// </param>
/// <param name="Clock">The monotonic bounded-wait seam.</param>
public sealed record ManagedEtabsWindowPolicy(
    TimeSpan ConfirmationDeadline,
    TimeSpan PollInterval,
    TimeSpan BackstopSweepInterval,
    IManagedEtabsClock Clock)
{
    public static ManagedEtabsWindowPolicy Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        SystemManagedEtabsClock.Instance);

    /// <summary>Zero or negative intervals would make the confirmation loops unbounded.</summary>
    public TimeSpan PollInterval { get; } = PollInterval > TimeSpan.Zero
        ? PollInterval
        : throw new ArgumentOutOfRangeException(nameof(PollInterval));

    public TimeSpan BackstopSweepInterval { get; } = BackstopSweepInterval > TimeSpan.Zero
        ? BackstopSweepInterval
        : throw new ArgumentOutOfRangeException(nameof(BackstopSweepInterval));
}

/// <summary>
/// The Windows window-station boundary behind one named seam, so the observer's targeting
/// rule is exercisable without user32 and without ETABS.
///
/// <para><b>Read-only by construction.</b> This interface has exactly one member, and it
/// asks a question. It has no <c>Hide</c> and no <c>Show</c>, because CLI&#160;#22 proved
/// out-of-process <c>ShowWindow</c> against a starting ETABS is unsafe: four supervised
/// runs with <c>SW_HIDE</c> active all killed ETABS with an unhandled
/// <c>NullReferenceException</c> inside its own <c>NativeWindow.Callback</c>, and the
/// controlled arm that removed the actuation survived and exported cleanly. Windows now
/// observes; CSI mutates.</para>
///
/// <para>Enumeration deliberately returns EVERY top-level window with its owning process
/// id rather than pre-filtering: the "only the exact owned process is ever read" rule is
/// the property under test, so it has to live in the observer where a test can drive
/// foreign windows past it.</para>
/// </summary>
public interface ITopLevelWindows
{
    IReadOnlyList<TopLevelWindow> Enumerate();
}

/// <summary>
/// Tells the observer, for ONE exact process, that a window of that process just surfaced.
///
/// <para>Its purpose changed with the CLI&#160;#22 ruling. It no longer prompts a
/// suppression sweep — nothing suppresses anything here now. It prompts an OBSERVATION, so
/// that exposure evidence is edge-triggered rather than dependent on catching a window
/// between backstop ticks. Diagnostic&#160;#1 showed the subscription was buying no
/// measurable suppression benefit; as an evidence source, catching the edge is exactly
/// what it is good for.</para>
///
/// <para>The subscription is process-scoped at the operating-system level, which is what
/// keeps this exact-owned: it is not a desktop-wide watch that filters afterwards.</para>
/// </summary>
public interface IOwnedWindowSurfaceMonitor : IDisposable
{
    /// <summary>
    /// Begins delivery for exactly <paramref name="processId"/>.
    /// <paramref name="onSurfaced"/> runs when a window of that process is created or
    /// shown; <paramref name="onBackstopTick"/> runs on a timer regardless, so a missed
    /// event still cannot leave an exposure unrecorded.
    /// </summary>
    void Start(int processId, Action onSurfaced, Action onBackstopTick);

    /// <summary>Whether the operating-system subscription is actually installed.</summary>
    bool Subscribed { get; }
}

/// <summary>
/// The visibility authority for ONE exact, proven-owned ETABS process — and, after
/// CLI&#160;#22, ONLY the authority. It observes and it certifies; it never mutates.
///
/// <para><b>Why Windows decides.</b> #20 settled it with evidence: on ETABS&#160;23.3
/// <c>cOAPI.Hide()</c> returns success and <c>cOAPI.Visible()</c> then stays true
/// indefinitely — 94 reads across 10.014&#160;s — while the real windows were in fact
/// hidden. Diagnostic&#160;#4 saw the same flag lie in the other direction. So CSI's flag
/// is telemetry, and the exact-owned HWND census is the acceptance gate for background
/// readiness, for an explicit reveal, and for CLI&#160;#24's exposure evidence.</para>
///
/// <para><b>Why CSI acts.</b> The same investigation proved the CSI ACTIONS work — the
/// hide landed within ~5&#160;ms, ~16&#160;ms and ~0.5&#160;s of the call across three
/// runs, and the reveal 14&#160;ms into the <c>Unhide</c> call — while out-of-process
/// <c>ShowWindow</c> crashed ETABS. So the mutation belongs to CSI and this type has no
/// actuator at all.</para>
///
/// <para><b>Why it is a latch.</b> The protected interval ends exactly once. An explicit
/// <c>open-model</c> ends it through <see cref="ReleaseForExplicitUserAction"/>; shutdown
/// ends it through <see cref="IDisposable.Dispose"/>. There is no re-arm, so background
/// work running after the engineer asked to see ETABS cannot put the session back into a
/// state that would demand hiding it again.</para>
/// </summary>
public interface IManagedEtabsWindowGuard : IDisposable
{
    /// <summary>The exact process this observer is allowed to read. Never a bare pid.</summary>
    ManagedProcessIdentity Identity { get; }

    /// <summary>False once the protected interval has been released or disposed.</summary>
    bool IsActive { get; }

    /// <summary>Where the session sits in the Closed-Alpha visibility contract.</summary>
    ManagedEtabsVisibilityState State { get; }

    /// <summary>
    /// CLI&#160;#24's accumulated evidence. Sticky: once material exposure has been seen
    /// inside the protected interval, this never reports clean again.
    /// </summary>
    ManagedEtabsExposureEvidence Exposure { get; }

    /// <summary>
    /// The background-readiness gate: observes until the exact-owned census reports NO
    /// materially visible top-level window, or the bounded deadline is spent.
    ///
    /// <para>This is what "background UI suppression = CONFIRMED" means. It is an
    /// observation of Windows, not a report of what CSI was asked for. Confirming it for
    /// the first time is what ENDS the consent interval — see
    /// <see cref="EnterBackgroundHidden"/>.</para>
    /// </summary>
    ManagedEtabsWindowConfirmation ConfirmSuppressed();

    /// <summary>
    /// The explicit-reveal gate: observes until the exact-owned census reports at least one
    /// MATERIALLY visible top-level window, or the bounded deadline is spent.
    /// </summary>
    ManagedEtabsWindowConfirmation ConfirmRevealed();

    /// <summary>
    /// Closes the startup-consent interval. Called once, immediately after the first
    /// successful <see cref="ConfirmSuppressed"/> following <c>cOAPI.Hide()</c>. From this
    /// instant, material exposure is unconsented and is recorded stickily.
    /// </summary>
    void EnterBackgroundHidden();

    /// <summary>
    /// Records that the engineer has explicitly been shown ETABS, after the requested model
    /// was confirmed open and the reveal was confirmed by the census.
    /// </summary>
    void EnterUserVisible();

    /// <summary>
    /// Ends the protected interval because the USER asked to see ETABS.
    ///
    /// <para>It restores nothing. Under the old design this call put our own hidden HWNDs
    /// back with <c>ShowWindow(SW_SHOW)</c>, and that restore was load bearing precisely
    /// because the stuck <c>Visible()</c> flag made the CSI policy skip its
    /// <c>Unhide</c>. Diagnostic&#160;#4 removed both halves of that workaround: the CSI
    /// call is now issued unconditionally, and it was measured putting the window back on
    /// screen 14&#160;ms later with <c>ShowWindow</c> impossible in either direction.</para>
    /// </summary>
    void ReleaseForExplicitUserAction();
}

/// <summary>
/// Creates an observer over an already-proven owned process. Takes the authoritative handle
/// rather than a pid so there is no signature through which an unproven or global ETABS
/// process could be observed at all.
/// </summary>
public interface IManagedEtabsWindowGuardFactory
{
    IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess);
}

/// <inheritdoc cref="IManagedEtabsWindowGuard" />
public sealed class ManagedEtabsWindowGuard : IManagedEtabsWindowGuard
{
    private readonly IOwnedEtabsProcess _owned;
    private readonly ITopLevelWindows _windows;
    private readonly IVirtualDesktop _desktop;
    private readonly IOwnedWindowSurfaceMonitor? _monitor;
    private readonly ManagedEtabsWindowPolicy _policy;
    private readonly object _gate = new();

    private ManagedEtabsVisibilityState _state = ManagedEtabsVisibilityState.StartingVisibleByConsent;
    private bool _terminated;
    private long _protectedFrom;
    private bool _exposureObserved;
    private int _exposureObservations;
    private ManagedEtabsExposureObservation? _firstExposure;
    private ManagedEtabsExposureObservation? _lastExposure;

    /// <summary>
    /// The desktop is always injected, never defaulted. A convenience overload that
    /// reached for the real monitor layout would be a platform-bound call on an
    /// unguarded path, and would also let a test silently measure exposure against the
    /// developer machine's real screen instead of a stated one.
    /// </summary>
    internal ManagedEtabsWindowGuard(
        IOwnedEtabsProcess owned,
        ITopLevelWindows windows,
        ManagedEtabsWindowPolicy policy,
        IOwnedWindowSurfaceMonitor? monitor,
        IVirtualDesktop desktop)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(desktop);

        _owned = owned;
        _windows = windows;
        _policy = policy;
        _monitor = monitor;
        _desktop = desktop;

        // Subscribed before this constructor returns, so the caller's next blocking call —
        // ApplicationStart, the interval #20 measured a visible window through — is already
        // being OBSERVED. It is not being suppressed; nothing suppresses any more.
        _monitor?.Start(owned.Identity.Pid, OnOwnedWindowSurfaced, OnBackstopTick);
    }

    /// <inheritdoc />
    public ManagedProcessIdentity Identity => _owned.Identity;

    /// <inheritdoc />
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_terminated;
            }
        }
    }

    /// <inheritdoc />
    public ManagedEtabsVisibilityState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public ManagedEtabsExposureEvidence Exposure
    {
        get
        {
            lock (_gate)
            {
                return _exposureObserved
                    ? new ManagedEtabsExposureEvidence(
                        true,
                        _exposureObservations,
                        _firstExposure,
                        _lastExposure)
                    : ManagedEtabsExposureEvidence.None;
            }
        }
    }

    /// <summary>Observation passes prompted by an operating-system window event.</summary>
    internal int EventPasses { get; private set; }

    /// <summary>Observation passes prompted only by the backstop timer.</summary>
    internal int BackstopPasses { get; private set; }

    /// <summary>The last observation failure, if any. Observation is best-effort by design.</summary>
    internal Exception? LastSweepError { get; private set; }

    /// <summary>Whether the operating-system window subscription is installed.</summary>
    internal bool Subscribed => _monitor?.Subscribed ?? false;

    /// <summary>
    /// One observation pass over the exact owned process's windows.
    ///
    /// <para>Every window that is not owned by <see cref="Identity"/> is skipped without
    /// being read further. That is the whole targeting rule, and it is stated once, here.
    /// Nothing is hidden, shown, moved or otherwise touched — the pass exists purely to
    /// accumulate CLI&#160;#24's evidence.</para>
    /// </summary>
    internal void ObserveOnce()
    {
        lock (_gate)
        {
            if (_terminated)
            {
                return;
            }

            // A pid is only provably ours while the authoritative handle keeps it from
            // being recycled. After exit it is somebody else's pid in waiting, so the
            // observer stops for good rather than reading it.
            if (_owned.HasExited)
            {
                _terminated = true;
                _state = ManagedEtabsVisibilityState.Retired;
                return;
            }

            // Exposure only counts once the consent interval has closed. Visibility during
            // StartingVisibleByConsent is exactly what the engineer agreed to.
            if (_state != ManagedEtabsVisibilityState.BackgroundHidden)
            {
                return;
            }

            var desktop = _desktop.Bounds;
            foreach (var window in _windows.Enumerate())
            {
                if (window.ProcessId != _owned.Identity.Pid)
                {
                    continue;
                }

                if (!ManagedEtabsWindowExposure.IsMaterial(window, desktop))
                {
                    continue;
                }

                RecordExposure(window);
            }
        }
    }

    /// <summary>Accumulates the sticky evidence. Callers hold the gate.</summary>
    private void RecordExposure(TopLevelWindow window)
    {
        var observation = new ManagedEtabsExposureObservation(
            window.Handle,
            window.Bounds,
            (long)_policy.Clock.ElapsedSince(_protectedFrom).TotalMilliseconds);

        _exposureObservations++;
        _lastExposure = observation;
        if (!_exposureObserved)
        {
            _exposureObserved = true;
            _firstExposure = observation;
        }
    }

    /// <inheritdoc />
    public void EnterBackgroundHidden()
    {
        lock (_gate)
        {
            // Only ever from the consented startup. A session the engineer has explicitly
            // been shown must never be walked back into a hidden state by later background
            // work, so UserVisible is deliberately absorbing.
            if (_state != ManagedEtabsVisibilityState.StartingVisibleByConsent)
            {
                return;
            }

            _state = ManagedEtabsVisibilityState.BackgroundHidden;
            _protectedFrom = _policy.Clock.Timestamp;
        }
    }

    /// <inheritdoc />
    public void EnterUserVisible()
    {
        lock (_gate)
        {
            if (_terminated)
            {
                return;
            }

            _state = ManagedEtabsVisibilityState.UserVisible;
        }
    }

    /// <inheritdoc />
    public ManagedEtabsWindowConfirmation ConfirmSuppressed() => Confirm(
        wantVisible: false,
        ManagedEtabsWindowErrorCodes.SuppressionNotConfirmed,
        "an owned ETABS top-level window is still materially on screen");

    /// <inheritdoc />
    public ManagedEtabsWindowConfirmation ConfirmRevealed() => Confirm(
        wantVisible: true,
        ManagedEtabsWindowErrorCodes.RevealNotConfirmed,
        "no owned ETABS top-level window became materially visible");

    /// <summary>
    /// The one confirmation loop, in both directions.
    ///
    /// <para>It polls rather than reading once because a CSI visibility transition is not
    /// synchronous for the caller: <c>Hide()</c> and <c>Unhide()</c> return before Windows
    /// has finished, so an immediate re-read proves nothing either way. Each iteration also
    /// takes an observation pass, so evidence keeps accumulating while we wait — and if the
    /// wait fails, the offending handles are named in the diagnostic.</para>
    /// </summary>
    private ManagedEtabsWindowConfirmation Confirm(
        bool wantVisible,
        string errorCode,
        string summary)
    {
        var started = _policy.Clock.Timestamp;
        var observations = 0;
        while (true)
        {
            if (_owned.HasExited)
            {
                return new(
                    Confirmed: false,
                    observations,
                    _policy.Clock.ElapsedSince(started),
                    [],
                    EtabsApiDiagnosticFormatter.Bounded(string.Join(
                        "; ",
                        ManagedEtabsWindowErrorCodes.OwnedProcessGone,
                        $"ownedPid={_owned.Identity.Pid}",
                        "the owned ETABS process exited before its window state could be confirmed")));
            }

            SafeObserve();

            List<nint> visible;
            try
            {
                lock (_gate)
                {
                    visible = OwnedMaterialHandles();
                }
            }
            catch (Exception exception)
            {
                LastSweepError = exception;
                return new(
                    Confirmed: false,
                    observations,
                    _policy.Clock.ElapsedSince(started),
                    [],
                    EtabsApiDiagnosticFormatter.InfrastructureException(
                        "ITopLevelWindows.Enumerate",
                        exception));
            }

            observations++;
            var waited = _policy.Clock.ElapsedSince(started);
            if (visible.Count > 0 == wantVisible)
            {
                return new(Confirmed: true, observations, waited, visible, Diagnostic: null);
            }

            if (waited >= _policy.ConfirmationDeadline)
            {
                return new(
                    Confirmed: false,
                    observations,
                    waited,
                    visible,
                    EtabsApiDiagnosticFormatter.Bounded(string.Join(
                        "; ",
                        errorCode,
                        $"ownedPid={_owned.Identity.Pid}",
                        $"observations={observations}",
                        $"waitedMs={(long)waited.TotalMilliseconds}",
                        $"materiallyVisibleOwnedWindows={visible.Count}",
                        $"handles=[{string.Join(", ", visible.Select(handle => $"0x{handle:X}"))}]",
                        summary)));
            }

            _policy.Clock.Wait(_policy.PollInterval);
        }
    }

    /// <summary>
    /// The exact-owned census: the handles of top-level windows that belong to the
    /// proven-owned process AND are MATERIALLY on screen. Callers hold the gate.
    /// </summary>
    private List<nint> OwnedMaterialHandles()
    {
        var desktop = _desktop.Bounds;
        return
        [
            .. _windows.Enumerate()
                .Where(window => window.ProcessId == _owned.Identity.Pid
                    && ManagedEtabsWindowExposure.IsMaterial(window, desktop))
                .Select(window => window.Handle)
        ];
    }

    /// <inheritdoc />
    public void ReleaseForExplicitUserAction() => Terminate();

    /// <summary>Deterministic teardown. Restores nothing; there is nothing to restore.</summary>
    public void Dispose() => Terminate();

    private void Terminate()
    {
        lock (_gate)
        {
            if (_terminated)
            {
                // Already latched. A second call — reveal then shutdown, or two shutdown
                // paths — must never re-arm anything.
                return;
            }

            _terminated = true;
        }

        // Delivery stops with the interval, so no in-flight event can record an exposure
        // against a session that is no longer protected, and no thread outlives it.
        _monitor?.Dispose();
    }

    private void OnOwnedWindowSurfaced()
    {
        EventPasses++;
        SafeObserve();
    }

    private void OnBackstopTick()
    {
        BackstopPasses++;
        SafeObserve();
    }

    private void SafeObserve()
    {
        try
        {
            ObserveOnce();
        }
        catch (Exception exception)
        {
            // Best-effort by construction: a transient window-station failure must not take
            // the daemon down. The bounded confirmation above is what actually gates
            // readiness, and it reports the failure truthfully.
            LastSweepError = exception;
        }
    }
}

/// <summary>
/// The production factory. Windows-only by construction — the sidecar drives ETABS
/// through COM on Windows and nowhere else, and a silently inert observer would reintroduce
/// exactly the "reported success, showed a window" failure #20 rejected.
/// </summary>
public sealed class WindowsManagedEtabsWindowGuardFactory : IManagedEtabsWindowGuardFactory
{
    public static readonly WindowsManagedEtabsWindowGuardFactory Instance = new();

    /// <inheritdoc />
    public IManagedEtabsWindowGuard Activate(IOwnedEtabsProcess ownedProcess)
    {
        ArgumentNullException.ThrowIfNull(ownedProcess);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Managed ETABS window observation requires Windows.");
        }

        var policy = ManagedEtabsWindowPolicy.Default;
        return new ManagedEtabsWindowGuard(
            ownedProcess,
            new Win32TopLevelWindows(),
            policy,
            new Win32OwnedWindowSurfaceMonitor(policy.BackstopSweepInterval),
            Win32VirtualDesktop.Instance);
    }
}

/// <summary>The real monitor layout, from user32's virtual-screen metrics.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32VirtualDesktop : IVirtualDesktop
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static readonly Win32VirtualDesktop Instance = new();

    /// <summary>
    /// Read on every access rather than cached: a monitor can be attached, detached or
    /// rearranged during a long background operation, and a stale rectangle would silently
    /// change what counts as on screen.
    /// </summary>
    public WindowBounds Bounds
    {
        get
        {
            var left = GetSystemMetrics(SmXVirtualScreen);
            var top = GetSystemMetrics(SmYVirtualScreen);
            return new WindowBounds(
                left,
                top,
                left + GetSystemMetrics(SmCxVirtualScreen),
                top + GetSystemMetrics(SmCyVirtualScreen));
        }
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetSystemMetrics(int nIndex);
}

/// <summary>
/// The user32 window-station boundary. Walks the desktop's top-level Z-order chain rather
/// than taking an enumeration callback, so no unmanaged function pointer and no
/// <c>AllowUnsafeBlocks</c> is needed — the same reasoning that keeps
/// <c>WindowsFileIdentity</c> on classic <c>DllImport</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32TopLevelWindows : ITopLevelWindows
{
    private const uint GwHwndNext = 2;

    /// <summary>A ceiling on the Z-order walk so a malformed chain cannot spin forever.</summary>
    private const int WindowScanLimit = 20_000;

    public IReadOnlyList<TopLevelWindow> Enumerate()
    {
        var windows = new List<TopLevelWindow>();
        var handle = GetTopWindow(nint.Zero);
        for (var scanned = 0; handle != nint.Zero && scanned < WindowScanLimit; scanned++)
        {
            _ = GetWindowThreadProcessId(handle, out var processId);

            // GetWindowRect can fail on a window that is being destroyed underneath the
            // walk. A failed read yields a zero rect, which the exposure predicate treats
            // as non-material - the correct answer for a window that no longer exists.
            _ = GetWindowRect(handle, out var rect);
            windows.Add(new(
                handle,
                unchecked((int)processId),
                IsWindowVisible(handle),
                new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom)));
            handle = GetWindow(handle, GwHwndNext);
        }

        return windows;
    }

    // Classic DllImport rather than the source-generated LibraryImport, for the reason
    // already recorded on WindowsFileIdentity: LibraryImport requires AllowUnsafeBlocks
    // project-wide, which is far too broad a change for a handful of P/Invokes.
    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetTopWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>
/// How a monitor teardown ended. Teardown asks the pump thread to stop and gives it a
/// bounded wait; the two answers are materially different and are never conflated.
/// </summary>
internal enum OwnedWindowMonitorTeardown
{
    /// <summary>Teardown has not been attempted.</summary>
    NotTornDown,

    /// <summary>The pump exited within the bound, or there was never a pump to wait for.</summary>
    Resolved,

    /// <summary>
    /// The pump had NOT exited when the bound expired — almost always because the
    /// <c>SetWinEventHook</c> call it is inside has not returned yet. Retirement has taken
    /// effect regardless, so that thread can no longer pump, observe, or hand a hook to the
    /// monitor; it is reported rather than presented as a clean teardown.
    /// </summary>
    PumpStillAlive
}

/// <summary>
/// A process-scoped <c>SetWinEventHook</c> subscription plus a backstop tick, on one
/// dedicated message-pumping thread.
///
/// <para>The hook is installed with <c>idProcess</c> set to the proven-owned pid, so the
/// operating system itself filters delivery to that one process — this is not a
/// desktop-wide watch that discards other processes' events afterwards, and no window
/// outside the owned process is ever observed through it.</para>
///
/// <para><c>WINEVENT_OUTOFCONTEXT</c> means the callback is delivered to THIS thread's
/// message queue rather than injected into ETABS, so nothing of ours runs inside the
/// guarded process. That is also why the thread must pump messages: the callback fires
/// from inside the message-retrieval call.</para>
///
/// <para><b>Activation is a handshake, and it is proven.</b> <see cref="Start"/> returns
/// only once the pump has reported back that <c>SetWinEventHook</c> ran AND returned a
/// non-zero hook. Anything else — the acknowledgement never arriving, or arriving with a
/// zero hook — tears the pump down and throws. There is deliberately no backstop-only
/// fallback: sampling alone is what #20 measured the ~234 ms and ~462 ms flickers through,
/// so a session that cannot subscribe must fail loudly rather than quietly degrade to the
/// mechanism that was already rejected.</para>
///
/// <para><b>Teardown is a custody transfer, not a deadline.</b> The one thing teardown
/// cannot do is cancel a <c>SetWinEventHook</c> call that is already in flight, so a pump
/// can outlive any join bound it is given. Three rules make that survivable, and none of
/// them depends on the pump being quick:</para>
///
/// <list type="number">
/// <item><description><b>Retirement is a latch, taken before the wait.</b> Once it is set
/// the monitor will not accept a hook, the callback refuses to run, and the message loop is
/// never entered. A pump that wakes up afterwards is already disarmed.</description></item>
/// <item><description><b>The installing thread is the only remover.</b> Whatever
/// <c>SetWinEventHook</c> returns, and however late, that same thread unhooks it before it
/// exits. Teardown never removes a hook itself — which is also what user32 asks for, since
/// <c>UnhookWinEvent</c> belongs on the thread that hooked.</description></item>
/// <item><description><b>The stop handle is reference counted.</b> Signalling and disposal
/// are separate concerns: teardown signals, and whichever of {teardown, pump} leaves last
/// disposes. Nothing a live pump can still reach is closed underneath it.</description></item>
/// </list>
///
/// <para>What teardown does NOT do is claim a success it cannot see. A pump still alive at
/// the bound is reported as <see cref="OwnedWindowMonitorTeardown.PumpStillAlive"/> and
/// named in the activation failure, because "that thread is disarmed" and "that thread is
/// gone" are different facts and the release gate is entitled to both.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32OwnedWindowSurfaceMonitor : IOwnedWindowSurfaceMonitor
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 0x0001;
    private const uint WaitObject0 = 0;

    /// <summary>
    /// How long activation waits for the pump's acknowledgement. Generous, because it only
    /// covers a thread start and one user32 call — and because exceeding it is a failure,
    /// not a fallback, so it must not fire on a merely busy machine.
    /// </summary>
    public static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long teardown waits for the pump to exit before reporting it as still alive.
    /// This is a REPORTING bound, not a safety bound: correctness does not improve by
    /// lengthening it, because retirement has already disarmed that thread and it removes
    /// its own hook whenever its install returns. The bound only decides whether teardown
    /// gets to say "gone" or has to say "disarmed, still running".
    /// </summary>
    public static readonly TimeSpan DefaultJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _backstopInterval;
    private readonly TimeSpan _installTimeout;
    private readonly TimeSpan _joinTimeout;
    private readonly Func<int, WinEventProc, nint> _installHook;
    private readonly Action<nint> _removeHook;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();

    /// <summary>
    /// Live references on <see cref="_stop"/>: one held by the monitor, and one more by a
    /// pump from before it can run until after its last use of the handle. It is disposed
    /// by whichever releases last, which is the whole reason a pump that outlives teardown
    /// cannot come back to a closed — or worse, recycled — kernel handle.
    /// </summary>
    private int _stopUsers = 1;

    // The delegate must outlive the hook: the OS holds a raw function pointer to it.
    private WinEventProc? _proc;
    private Thread? _thread;
    private nint _hook;
    private int _retired;
    private OwnedWindowMonitorTeardown _teardown = OwnedWindowMonitorTeardown.NotTornDown;
    private Exception? _installError;
    private Exception? _teardownError;

    public Win32OwnedWindowSurfaceMonitor(TimeSpan backstopInterval)
        : this(backstopInterval, InstallHook, RemoveHook, DefaultInstallTimeout)
    {
    }

    /// <summary>
    /// The same monitor with the user32 hook calls and the acknowledgement deadline behind
    /// parameters, so the activation contract — which is pure control flow around two
    /// P/Invokes — is exercisable without waiting on the real window station to misbehave.
    /// The public constructor above binds the real calls, and that is what production uses.
    /// </summary>
    internal Win32OwnedWindowSurfaceMonitor(
        TimeSpan backstopInterval,
        Func<int, WinEventProc, nint> installHook,
        Action<nint> removeHook,
        TimeSpan installTimeout)
        : this(backstopInterval, installHook, removeHook, installTimeout, DefaultJoinTimeout)
    {
    }

    /// <summary>
    /// And with the teardown join bound behind a parameter too, so the state a pump enters
    /// when it outlives that bound is reachable in a test without anyone sleeping for
    /// seconds to get there. Production always takes <see cref="DefaultJoinTimeout"/>.
    /// </summary>
    internal Win32OwnedWindowSurfaceMonitor(
        TimeSpan backstopInterval,
        Func<int, WinEventProc, nint> installHook,
        Action<nint> removeHook,
        TimeSpan installTimeout,
        TimeSpan joinTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backstopInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(installTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(joinTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(installHook);
        ArgumentNullException.ThrowIfNull(removeHook);

        _backstopInterval = backstopInterval;
        _installTimeout = installTimeout;
        _joinTimeout = joinTimeout;
        _installHook = installHook;
        _removeHook = removeHook;
    }

    /// <inheritdoc />
    public bool Subscribed => Volatile.Read(ref _hook) != nint.Zero;

    /// <summary>
    /// Whether this monitor has been retired. Never false again once true, and the single
    /// fact every disarming rule below reads.
    /// </summary>
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>How the teardown ended — for the failure text, and for teardown assertions.</summary>
    internal OwnedWindowMonitorTeardown Teardown
    {
        get
        {
            lock (_gate)
            {
                return _teardown;
            }
        }
    }

    /// <summary>A failure while removing a hook, if any. Recorded, never thrown.</summary>
    internal Exception? TeardownError => _teardownError;

    /// <summary>
    /// Whether the stop handle has been closed. This is the one resource a still-live pump
    /// can reach, so it is observable rather than assumed: a teardown that closed it while
    /// its pump was still running is exactly the defect this monitor was repaired for.
    /// </summary>
    internal bool StopSignalClosed => _stop.SafeWaitHandle.IsClosed;

    /// <summary>Whether the pump thread is still alive, for teardown assertions.</summary>
    internal bool PumpAlive
    {
        get
        {
            lock (_gate)
            {
                return _thread?.IsAlive ?? false;
            }
        }
    }

    /// <inheritdoc />
    public void Start(int processId, Action onSurfaced, Action onBackstopTick)
    {
        ArgumentNullException.ThrowIfNull(onSurfaced);
        ArgumentNullException.ThrowIfNull(onBackstopTick);

        // Deliberately never disposed. The pump sets it on its way past the install call,
        // which may be long after this method has given up waiting — disposing it here
        // would be the same defect as closing the stop handle underneath a live pump, and
        // the runtime reclaims its handle either way.
        var installed = new ManualResetEventSlim(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired != 0, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException(
                    "This owned-window monitor has already been started.");
            }

            var pump = new Thread(() => Pump(processId, onSurfaced, onBackstopTick, installed))
            {
                IsBackground = true,
                Name = "etabs-window-guard"
            };
            _thread = pump;

            // The pump's reference is taken BEFORE it can run, so there is no instant in
            // which a pump exists uncounted and teardown could close the handle under it.
            _ = Interlocked.Increment(ref _stopUsers);
            try
            {
                pump.Start();
            }
            catch
            {
                _ = Interlocked.Decrement(ref _stopUsers);
                _thread = null;
                throw;
            }
        }

        // Deliberately OUTSIDE the gate. The pump publishes its result and signals; waiting
        // for that acknowledgement while holding the lock the pump needs would guarantee the
        // wait times out and let activation "succeed" before any hook existed — the exact
        // race the subscription was introduced to remove.
        var acknowledged = installed.Wait(_installTimeout);
        if (acknowledged && Subscribed)
        {
            return;
        }

        FailActivation(acknowledged);
    }

    public void Dispose() => _ = Retire();

    /// <summary>
    /// Activation could not be proven, so the monitor is retired and the caller is told —
    /// including, when it is true, that the pump had not exited yet. That last part is the
    /// point: an activation that throws while a thread of ours is still running is a
    /// different fact from one that throws over a fully joined teardown, and this text is
    /// what the caller carries into the launch cleanup envelope.
    /// </summary>
    private void FailActivation(bool acknowledged)
    {
        var teardown = Retire();

        var reason = acknowledged
            ? "SetWinEventHook returned no hook for the owned ETABS process, so window " +
                "events cannot be observed. Background UI suppression would degrade to " +
                "sampling, which is not sufficient."
            : "The owned-window subscription did not report installation within " +
                $"{_installTimeout.TotalSeconds:0.###}s.";

        if (teardown == OwnedWindowMonitorTeardown.PumpStillAlive)
        {
            reason +=
                " Teardown is UNRESOLVED: the subscription thread had still not exited " +
                $"{_joinTimeout.TotalSeconds:0.###}s after being told to stop, so this " +
                "activation is reported as failed WITHOUT a completed cleanup. That thread " +
                "is already retired — it cannot pump, cannot deliver a callback, and it " +
                "removes whatever hook its install returns before it exits.";
        }

        throw new InvalidOperationException(reason, _installError);
    }

    /// <summary>
    /// The single teardown path, shared by disposal and by failed activation, and idempotent
    /// because reveal-then-shutdown reaches it twice.
    ///
    /// <para>Retirement is latched first and under the gate, so it is in force BEFORE the
    /// wait rather than after it. Everything that could still act — claiming a hook, running
    /// the callback, entering the message loop — reads that latch, which is why the bounded
    /// join below is only ever a question about reporting, never about safety.</para>
    /// </summary>
    private OwnedWindowMonitorTeardown Retire()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_retired != 0)
            {
                return _teardown;
            }

            Volatile.Write(ref _retired, 1);
            thread = _thread;
        }

        // Signalling is not disposal. Set() only wakes a pump that is in the message loop;
        // the handle stays open until this reference is released AND the pump has released
        // its own, so a pump still inside its install call cannot return to a closed handle.
        _stop.Set();

        var resolved = thread is null || thread.Join(_joinTimeout);
        var teardown = resolved
            ? OwnedWindowMonitorTeardown.Resolved
            : OwnedWindowMonitorTeardown.PumpStillAlive;
        lock (_gate)
        {
            _teardown = teardown;
        }

        ReleaseStopHandle();
        return teardown;
    }

    /// <summary>Drops one reference on the stop handle, disposing it on the last one.</summary>
    private void ReleaseStopHandle()
    {
        if (Interlocked.Decrement(ref _stopUsers) == 0)
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// Hands a freshly installed hook to the monitor, but only while the monitor is still
    /// live. A refusal means this pump is late: the hook stays its own to remove, and it
    /// must not arm anything with it.
    /// </summary>
    private bool TryClaimHook(nint hook)
    {
        lock (_gate)
        {
            if (_retired != 0)
            {
                return false;
            }

            Volatile.Write(ref _hook, hook);
            return true;
        }
    }

    private void ReleaseClaimedHook()
    {
        lock (_gate)
        {
            Volatile.Write(ref _hook, nint.Zero);
        }
    }

    private void Pump(
        int processId,
        Action onSurfaced,
        Action onBackstopTick,
        ManualResetEventSlim installed)
    {
        var hook = nint.Zero;
        try
        {
            // Held in a field for the hook's lifetime; a collected delegate is a hard crash.
            var proc = BuildCallback(onSurfaced);
            _proc = proc;

            // Retirement can beat this thread to its first instruction — the activation
            // deadline is a wall clock and this thread is at the scheduler's mercy.
            // Installing then would put a hook on the window station only to take it off
            // again, against a pid that is already no longer provably ours.
            if (!IsRetired)
            {
                try
                {
                    hook = _installHook(processId, proc);
                }
                catch (Exception exception)
                {
                    _installError = exception;
                }
            }

            // Custody, decided exactly once and under the gate.
            var claimed = hook != nint.Zero && TryClaimHook(hook);

            // Acknowledged only once custody is settled, success or not: Start must never
            // wait out its deadline for an answer that already exists, and must never see a
            // Subscribed that is about to be true rather than true.
            installed.Set();

            if (!claimed)
            {
                // Either nothing installed, or this pump is late and owns its own hook.
                // Both mean the same thing here: nothing is subscribed on the monitor's
                // behalf, so there is nothing to pump — and the backstop must NOT run on
                // its own. Sampling-only suppression is the mechanism #20 measured the
                // flickers through, and a failed activation is about to be reported.
                return;
            }

            RunMessageLoop(onBackstopTick);
            ReleaseClaimedHook();
        }
        finally
        {
            // The installing thread is the only remover, on every path out of this method.
            // user32 wants the unhook from the thread that hooked, and putting it here
            // rather than on the caller's thread during teardown is also what makes a late
            // install impossible to orphan: this runs whenever the install returns, however
            // long after teardown gave up waiting for it.
            if (hook != nint.Zero)
            {
                RemoveHookSafely(hook);
            }

            // Unrooted only after the hook is gone: the OS held a raw pointer to it.
            _proc = null;
            ReleaseStopHandle();
        }
    }

    /// <summary>
    /// The callback the operating system is given. It reads the retirement latch itself
    /// rather than trusting the pump to be the only gate: out-of-context delivery can only
    /// reach us from inside this thread's message retrieval, which retirement stops — but
    /// the pid this hook was scoped to also stops being provably ours at that same instant,
    /// so the callback refuses on its own account too.
    /// </summary>
    private WinEventProc BuildCallback(Action onSurfaced) =>
        (_, eventType, _, idObject, idChild, _, _) =>
        {
            if (IsRetired)
            {
                return;
            }

            if (idObject == ObjIdWindow
                && idChild == ChildIdSelf
                && (eventType == EventObjectCreate || eventType == EventObjectShow))
            {
                onSurfaced();
            }
        };

    private void RunMessageLoop(Action onBackstopTick)
    {
        var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle() };
        var backstopMs = (uint)Math.Max(1, (long)_backstopInterval.TotalMilliseconds);
        while (true)
        {
            var wait = MsgWaitForMultipleObjects(1, handles, false, backstopMs, QsAllInput);
            if (wait == WaitObject0)
            {
                return;
            }

            // Draining the queue is what actually invokes the WinEvent callback above.
            while (PeekMessageW(out var message, nint.Zero, 0, 0, PmRemove))
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessageW(ref message);
            }

            onBackstopTick();
        }
    }

    private void RemoveHookSafely(nint hook)
    {
        try
        {
            _removeHook(hook);
        }
        catch (Exception exception)
        {
            // Recorded rather than thrown. This runs on the pump thread while it is on its
            // way out, where an escaping exception would take the whole daemon down and
            // still would not have removed the hook.
            _teardownError = exception;
        }
    }

    private static nint InstallHook(int processId, WinEventProc proc) => SetWinEventHook(
        EventObjectCreate,
        EventObjectShow,
        nint.Zero,
        proc,
        unchecked((uint)processId),
        0,
        WinEventOutOfContext);

    private static void RemoveHook(nint hook) => _ = UnhookWinEvent(hook);

    internal delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint MsgWaitForMultipleObjects(
        uint nCount,
        nint[] pHandles,
        [MarshalAs(UnmanagedType.Bool)] bool fWaitAll,
        uint dwMilliseconds,
        uint dwWakeMask);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message,
        nint hWnd,
        uint filterMin,
        uint filterMax,
        uint removeMsg);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint DispatchMessageW(ref NativeMessage message);
}
