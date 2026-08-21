// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The revert guard for CLI #22.
///
/// <para>Every behavioral test above reaches ETABS through an injected seam, because
/// <c>cOAPI</c> and <c>ETABSApplication</c> cannot be faked. That is exactly where this
/// bug lived: the seams were fine and the WIRING was missing — the daemon's launcher
/// never issued a hide at all, so a background Commit put a blank <c>(Untitled)</c>
/// window on screen while every test stayed green.</para>
///
/// <para>These tests therefore assert on compiled IL, per METHOD rather than per type,
/// and they scan the WHOLE production assembly rather than a list of known files. Per
/// method matters here: <c>ServeDispatcher</c> hosts both intents, so a type-level rule
/// could not tell "open-model reveals ETABS" from "snapshot-export reveals ETABS" —
/// which is the one distinction the issue is about.</para>
///
/// <para>What is actually inspected is <c>EtabExtension.CLI.Tests.dll</c>, built Debug
/// from the same production sources the sidecar compiles (the test project includes
/// <c>src/EtabExtension.CLI/**/*.cs</c>, excluding only <c>Program.cs</c>). It is not the
/// shipped binary — a Release-only difference would not be seen here — but the call graph
/// it carries is the same code, which is what these rules are about.</para>
/// </summary>
public sealed class EtabsVisibilityWiringTests
{
    /// <summary>The CSI application-visibility calls, by method name.</summary>
    private static readonly string[] CsiVisibilityMethods = ["Hide", "Unhide", "Visible"];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProductionCalls =
        VisibilityCallGraph.ByProductionMethod(typeof(IEtabsRawApi).Assembly);

    /// <summary>
    /// The complete set of production types allowed to issue a CSI visibility call at all.
    ///
    /// <list type="bullet">
    /// <item><c>EtabsRawApi</c> — the managed daemon's adapter onto <c>cOAPI</c>, added by
    /// CLI #22. Everything the daemon does goes through it.</item>
    /// <item><c>EtabsSessionHelpers</c> — the pre-existing one-shot Mode B owner
    /// (<c>HideIfVisible</c>), used by <c>generate-e2k</c>, <c>run-analysis</c>,
    /// <c>extract-*</c> and the standalone <c>snapshot-export</c>/<c>analyze-and-extract</c>
    /// paths that create and destroy their own ETABS. That path was always correct; it is
    /// the daemon that lost the hide, which is what CLI #22 restores.</item>
    /// </list>
    /// </summary>
    private static readonly string[] CsiVisibilityOwners =
    [
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsRawApi",
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.EtabsSessionHelpers"
    ];

    /// <summary>
    /// Every seam through which ETABS can be put on screen — not just the one the
    /// dispatcher happens to use today.
    ///
    /// <para>Keying this rule on a single string was a real hole, demonstrated rather than
    /// imagined: <c>EnsureVisibleForExplicitUserAction</c> is public on
    /// <see cref="IManagedEtabsApplication"/>, and every handler can obtain one from
    /// <c>_session.GetOrStartOwned()</c>. A background command calling THAT reveals ETABS
    /// with the entire suite green, and the CSI-owner rule cannot see it either because the
    /// <c>Unhide</c> still happens inside allow-listed <c>EtabsRawApi</c>. The rule has to
    /// name the whole chain — interface and implementation at each level — because reaching
    /// any link of it is reaching the screen.</para>
    /// </summary>
    private static readonly string[] RevealSeams =
    [
        $"{typeof(IEtabsSession).FullName}.{nameof(IEtabsSession.RevealForExplicitUserRequest)}",
        $"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.RevealForExplicitUserRequest)}",
        $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.EnsureVisibleForExplicitUserAction),
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.EnsureVisibleForExplicitUserAction),
        $"{typeof(ManagedEtabsVisibility).FullName}.{nameof(ManagedEtabsVisibility.EnsureVisible)}"
    ];

    /// <summary>
    /// The mirror on the hide side. A background command that HIDES a window the user
    /// explicitly asked for is the inverse regression, and it is expressible through
    /// exactly the same public seams.
    /// </summary>
    private static readonly string[] HideSeams =
    [
        $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.EnsureHiddenForBackgroundWork),
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.EnsureHiddenForBackgroundWork),
        $"{typeof(ManagedEtabsVisibility).FullName}.{nameof(ManagedEtabsVisibility.EnsureHidden)}"
    ];

    /// <summary>
    /// The complete set of production methods allowed to touch a reveal seam: the intent
    /// decision, then each link of the chain it delegates through. Anything else is a
    /// command deciding on its own that ETABS should be on screen.
    /// </summary>
    private static readonly string[] RevealCallers =
    [
        // The intent decision, and the only entry point.
        "EtabExtension.CLI.Features.Serve.ServeDispatcher.RevealAfterConfirmedOpen",
        // The chain it delegates through.
        $"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.RevealForExplicitUserRequest)}",
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.EnsureVisibleForExplicitUserAction)
    ];

    /// <summary>
    /// The complete set of production methods allowed to touch a hide seam. Both entry
    /// points run only while a session is being created; nothing on a command path is
    /// listed, and that absence is what makes reuse of a user-revealed session safe.
    /// </summary>
    private static readonly string[] HideCallers =
    [
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
            ".HideBeforeAnythingElseTouchesIt",
        $"{typeof(EtabsSession).FullName}.ConfirmHiddenForBackgroundWork",
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.EnsureHiddenForBackgroundWork)
    ];

    /// <summary>
    /// Arming Windows-level window suppression, at every link of the chain. The #20
    /// certification proved the CSI hide alone cannot keep a window off the screen during
    /// startup, so this is now a product-state guard — and, like the reveal, it has exactly
    /// one legitimate origin.
    /// </summary>
    private static readonly string[] GuardArmSeams =
    [
        $"{typeof(IManagedEtabsWindowGuardFactory).FullName}." +
            nameof(IManagedEtabsWindowGuardFactory.Activate),
        $"{typeof(WindowsManagedEtabsWindowGuardFactory).FullName}." +
            nameof(WindowsManagedEtabsWindowGuardFactory.Activate)
    ];

    /// <summary>
    /// Retiring that suppression FOR THE USER — the reveal latch, which also puts back the
    /// windows the guard hid. A background command reaching this would silently undo the
    /// repair for the rest of the session.
    /// </summary>
    private static readonly string[] GuardReleaseSeams =
    [
        $"{typeof(IManagedEtabsWindowGuard).FullName}." +
            nameof(IManagedEtabsWindowGuard.ReleaseForExplicitUserAction),
        $"{typeof(ManagedEtabsWindowGuard).FullName}." +
            nameof(ManagedEtabsWindowGuard.ReleaseForExplicitUserAction),
        $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.ReleaseWindowGuardForExplicitUserAction),
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.ReleaseWindowGuardForExplicitUserAction)
    ];

    /// <summary>The one method allowed to arm suppression, and the chain it delegates through.</summary>
    private static readonly string[] GuardArmCallers =
    [
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
            ".ActivateWindowGuard"
    ];

    /// <summary>The one method allowed to retire it for the user, and its delegation chain.</summary>
    private static readonly string[] GuardReleaseCallers =
    [
        $"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.RevealForExplicitUserRequest)}",
        $"{typeof(ManagedEtabsApplication).FullName}." +
            nameof(ManagedEtabsApplication.ReleaseWindowGuardForExplicitUserAction)
    ];

    /// <summary>
    /// The un-fakeable wiring: which raw CSI call each adapter actually makes. Swapping
    /// <c>Hide</c> for <c>Unhide</c> here would leave every fake-driven test green and put
    /// a window on screen for every background run.
    /// </summary>
    [Theory]
    [InlineData("Hide", "ETABSv1.cOAPI.Hide")]
    [InlineData("Unhide", "ETABSv1.cOAPI.Unhide")]
    [InlineData("Visible", "ETABSv1.cOAPI.Visible")]
    public void TheRawAdapterMakesTheCsiCallItIsNamedFor(string adapter, string expectedCall)
    {
        var calls = Calls($"EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsRawApi.{adapter}");

        Assert.Contains(expectedCall, calls, StringComparer.Ordinal);
        Assert.DoesNotContain(
            calls,
            call => IsCsiVisibilityCall(call)
                && !string.Equals(call, expectedCall, StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole-assembly rule. A command that reaches for <c>cOAPI.Hide</c> on its own —
    /// which is how a background command would end up hiding a window the user asked for —
    /// fails here the day it is written.
    /// </summary>
    [Fact]
    public void OnlyTheDeclaredOwnersIssueCsiVisibilityCalls()
    {
        var offenders = ProductionCalls
            .Where(method => method.Value.Any(IsCsiVisibilityCall))
            .Select(method => OwningType(method.Key))
            .Where(type => !CsiVisibilityOwners.Contains(type, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These types call cOAPI Hide/Unhide/Visible directly: {string.Join(", ", offenders)}. " +
            $"Managed-session visibility belongs to {nameof(ManagedEtabsVisibility)} through " +
            "IEtabsVisibilityApi, which reads the state before transitioning and confirms it " +
            "after — Cardex documents that Hide/Unhide error when the application is already " +
            "in the requested state.");
    }

    /// <summary>
    /// The hide has to be ON the start path. Proving the policy is correct is worthless if
    /// nothing calls it — that is precisely the shape of the shipped defect.
    /// </summary>
    [Fact]
    public void TheManagedLaunchHidesTheApplicationItStarted()
    {
        Assert.True(
            ReachesAny(
                "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher.Launch",
                HideSeams),
            "ManagedEtabsLauncher.Launch no longer hides the application it started. A managed " +
            "session exists to do background work; nothing may reach the screen between " +
            "ApplicationStart and an explicit user request.");
    }

    /// <summary>
    /// The session re-asserts hidden once the blank model exists. <c>InitializeNewModel</c>
    /// is what makes the window say <c>(Untitled)</c>, and the supervised RC1 timeline shows
    /// that title arriving 6 s after the window did — so a single hide at
    /// <c>ApplicationStart</c> is not on its own proof that nothing surfaces later.
    /// </summary>
    [Fact]
    public void TheSessionReAssertsHiddenBeforeHandingTheApplicationOut()
    {
        Assert.True(
            ReachesAny(
                $"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.GetOrStartOwned)}",
                HideSeams),
            "EtabsSession no longer confirms the managed application hidden before handing it " +
            "to a command.");
    }

    /// <summary>
    /// Both hide sites must report whether they actually caught a window.
    ///
    /// <para>This is instrumentation, so no behavioral test can miss its removal — but it
    /// is the one fact the supervised #20 run cannot reconstruct afterwards. The RC1
    /// timeline is compatible with two very different outcomes: either
    /// <c>ApplicationStart</c> returns with a window already up and the startup hide takes
    /// it straight down, or the window does not exist yet, the startup hide finds nothing,
    /// and the session's hide only acts after <c>InitializeNewModel</c> — leaving ETABS on
    /// screen for the seconds in between. Reading <c>Changed</c> at both sites is what
    /// tells those apart.</para>
    /// </summary>
    [Theory]
    [InlineData(
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
        ".HideBeforeAnythingElseTouchesIt")]
    [InlineData(
        "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.EtabsSession" +
        ".ConfirmHiddenForBackgroundWork")]
    public void EachHideSiteReportsWhetherItCaughtAWindow(string hideSite)
    {
        var calls = Calls(hideSite);

        Assert.Contains(
            $"{typeof(ManagedEtabsVisibilityOutcome).FullName}." +
            $"get_{nameof(ManagedEtabsVisibilityOutcome.Changed)}",
            calls,
            StringComparer.Ordinal);
        Assert.Contains("System.IO.TextWriter.WriteLine", calls, StringComparer.Ordinal);
    }

    /// <summary>
    /// The acceptance criterion — "focused tests … fail if the two intents collapse to one
    /// behavior" — stated over the compiled assembly. Any method outside the declared chain
    /// touching ANY reveal seam fails here, including a sibling dispatch method that looks
    /// harmless in review.
    /// </summary>
    [Fact]
    public void OnlyTheExplicitOpenPathAsksForEtabsToBeShown()
    {
        var offenders = OffendersAgainst(RevealSeams, RevealCallers);

        Assert.True(
            offenders.Length == 0,
            $"These methods can put ETABS on screen: {string.Join(", ", offenders)}. Only the " +
            "explicit open-model path may, and only after the requested model is confirmed " +
            "open. A background command that reveals ETABS is the CLI #22 defect.");
    }

    /// <summary>
    /// The inverse regression, guarded identically. A command that hides the window an
    /// engineer explicitly opened is just as wrong as one that shows a window nobody asked
    /// for, and it is expressible through the same public seams.
    /// </summary>
    [Fact]
    public void OnlyTheSessionCreationPathHidesEtabs()
    {
        var offenders = OffendersAgainst(HideSeams, HideCallers);

        Assert.True(
            offenders.Length == 0,
            $"These methods can hide ETABS: {string.Join(", ", offenders)}. Hiding happens only " +
            "while a session is being created, before any command can hold it. A command that " +
            "hides would yank away a window the user explicitly opened.");
    }

    /// <summary>
    /// The other direction: every allow-listed caller must actually touch a seam, so neither
    /// list can silently describe a path that no longer does anything, and the open-model
    /// handler must actually reach the reveal.
    /// </summary>
    [Fact]
    public void TheExplicitOpenPathReallyDoesRevealAndIsReachedFromTheOpenDispatch()
    {
        Assert.All(
            RevealCallers,
            caller => Assert.Contains(
                Calls(caller),
                call => RevealSeams.Contains(call, StringComparer.Ordinal)));
        Assert.All(
            HideCallers,
            caller => Assert.Contains(
                Calls(caller),
                call => HideSeams.Contains(call, StringComparer.Ordinal)));
        Assert.True(
            ReachesAny($"{typeof(ServeDispatcher).FullName}.{OpenDispatch}", RevealSeams),
            "open-model no longer ends with ETABS visible.");
    }

    /// <summary>
    /// The acceptance criterion in its sharpest form. <c>ServeDispatcher</c> hosts every
    /// command, so this walks its dispatch handlers and requires that exactly one of them —
    /// the explicit open — can reach a reveal seam at all, and that none of them can reach a
    /// hide seam. Collapsing the two intents, in either direction, fails here.
    ///
    /// <para>The handler list comes from reflection and is compared for EQUALITY, not
    /// counted. A scanner regression that dropped every async handler still left nine
    /// non-async ones behind — enough to sail through a "found more than a few" canary while
    /// silently exempting the one handler this issue is about.</para>
    /// </summary>
    [Fact]
    public void ExactlyOneDispatchHandlerCanEverPutEtabsOnScreenAndNoneCanHideIt()
    {
        // Public as well as private: the public DispatchAsync router is named Dispatch* and
        // shows up in the IL scan, so excluding it here would make the two sides disagree
        // for a reason that has nothing to do with visibility. It routes through a delegate
        // table, so it reaches no seam of its own — and that is worth asserting, not hiding.
        var expected = typeof(ServeDispatcher)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name.StartsWith("Dispatch", StringComparison.Ordinal))
            .Select(name => $"{typeof(ServeDispatcher).FullName}.{name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var scanned = ProductionCalls.Keys
            .Where(name => name.StartsWith(
                $"{typeof(ServeDispatcher).FullName}.Dispatch",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The scan must see every declared handler. If it ever sees fewer, the rules below
        // are quietly exempting whichever ones went missing.
        Assert.Equal(expected, scanned);

        Assert.Equal(
            [$"{typeof(ServeDispatcher).FullName}.{OpenDispatch}"],
            scanned.Where(handler => ReachesAny(handler, RevealSeams)).ToArray());
        Assert.Equal(
            [],
            scanned.Where(handler => ReachesAny(handler, HideSeams)).ToArray());
    }

    /// <summary>
    /// The load-bearing wiring assertion for the #20 repair, and the one that must go RED
    /// if the production activation is deleted.
    ///
    /// <para>Reachability alone would not be enough here: a guard armed AFTER
    /// <c>ApplicationStart</c> returns is a guard that was not up for the 5.19 s the
    /// supervised run measured a window through. So this reads the launcher's own IL in
    /// order — ownership, then arming, then the blocking start — and a guard that drifts
    /// later in the method fails on the ordering rather than on its absence.</para>
    /// </summary>
    [Fact]
    public void TheManagedLaunchArmsWindowSuppressionOverProvenOwnershipBeforeItStartsAnything()
    {
        const string launch =
            "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher.Launch";

        var ownership = IndexOfCall(
            launch,
            "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
            ".CensusExactlyOneOwnedProcess");
        var handle = IndexOfCall(
            launch,
            $"{typeof(IProcessInspector).FullName}.{nameof(IProcessInspector.OpenExact)}");
        var arm = IndexOfCall(
            launch,
            "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
            ".ActivateWindowGuard");
        var start = IndexOfCall(
            launch,
            "EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
            ".StartApplication");

        Assert.True(
            ownership < handle,
            "The ownership census must precede the authoritative handle it is opened from.");
        Assert.True(
            handle < arm,
            "Window suppression may only be armed over a handle whose identity is proven. " +
            "Arming from anything less is the global-PID failure this repair must not have.");
        Assert.True(
            arm < start,
            "Window suppression must be armed BEFORE ApplicationStart. #20 measured a real " +
            "ETABS window on screen for 5.19 s inside that call, so a guard armed after it " +
            "returns is a guard that was down for the entire defect.");
    }

    /// <summary>
    /// The other end of the same wire: the explicit reveal retires suppression BEFORE it
    /// asks CSI to show the application. Reversed, the guard would still be sweeping when
    /// the engineer's window appeared and would take it straight back down.
    /// </summary>
    [Fact]
    public void TheExplicitRevealRetiresSuppressionBeforeTheCsiTransition()
    {
        var reveal =
            $"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.RevealForExplicitUserRequest)}";

        var release = IndexOfCall(
            reveal,
            $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.ReleaseWindowGuardForExplicitUserAction));
        var unhide = IndexOfCall(
            reveal,
            $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.EnsureVisibleForExplicitUserAction));

        Assert.True(
            release < unhide,
            "The window guard must be retired before the CSI visible transition, not after.");
    }

    /// <summary>
    /// Suppression is armed from exactly one place and retired for the user from exactly
    /// one place. Both lists are checked in both directions, so neither can describe a path
    /// that no longer exists.
    /// </summary>
    [Fact]
    public void OnlyTheLaunchArmsSuppressionAndOnlyTheExplicitOpenRetiresIt()
    {
        Assert.Equal([], OffendersAgainst(GuardArmSeams, GuardArmCallers));
        Assert.Equal([], OffendersAgainst(GuardReleaseSeams, GuardReleaseCallers));
        Assert.All(
            GuardArmCallers,
            caller => Assert.Contains(
                Calls(caller),
                call => GuardArmSeams.Contains(call, StringComparer.Ordinal)));
        Assert.All(
            GuardReleaseCallers,
            caller => Assert.Contains(
                Calls(caller),
                call => GuardReleaseSeams.Contains(call, StringComparer.Ordinal)));
    }

    /// <summary>
    /// No dispatch handler reaches the window guard on its own — not to arm it, and not to
    /// retire it. The only route from a command to the guard is
    /// <see cref="IEtabsSession.RevealForExplicitUserRequest"/>, and
    /// <see cref="ExactlyOneDispatchHandlerCanEverPutEtabsOnScreenAndNoneCanHideIt"/>
    /// already pins that seam to the explicit open alone; the second half here closes the
    /// chain by proving that seam's one implementation is what retires suppression.
    ///
    /// <para>A handler that disabled the guard directly would silently undo the #20 repair
    /// for the rest of the session while every fake-driven test stayed green.</para>
    /// </summary>
    [Fact]
    public void NoDispatchHandlerTouchesTheWindowGuardExceptThroughTheExplicitRevealSeam()
    {
        var handlers = ProductionCalls.Keys
            .Where(name => name.StartsWith(
                $"{typeof(ServeDispatcher).FullName}.Dispatch",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(handlers);
        Assert.Equal([], handlers.Where(handler => ReachesAny(handler, GuardArmSeams)).ToArray());
        Assert.Equal([], handlers.Where(handler => ReachesAny(handler, GuardReleaseSeams)).ToArray());

        // The chain's far end: the sole implementation behind the reveal seam is the sole
        // production method that retires suppression for the user.
        Assert.Equal(
            [$"{typeof(EtabsSession).FullName}.{nameof(EtabsSession.RevealForExplicitUserRequest)}"],
            ProductionCalls
                .Where(method => method.Value.Any(
                    call => GuardReleaseSeams.Contains(call, StringComparer.Ordinal)))
                .Select(method => method.Key)
                .Where(name => !name.StartsWith(
                    $"{typeof(ManagedEtabsApplication).FullName}.",
                    StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Shutdown disposes the guard, on every route in. <c>ShutdownCore</c> is the single
    /// funnel both public shutdown entry points reach, so a sweep thread cannot outlive the
    /// session that armed it.
    /// </summary>
    [Fact]
    public void ShutdownDisposesTheWindowGuardDeterministically()
    {
        var dispose = $"{typeof(IManagedEtabsApplication).FullName}." +
            nameof(IManagedEtabsApplication.DisposeWindowGuard);

        Assert.Contains(
            dispose,
            Calls($"{typeof(ManagedEtabsShutdownMachine).FullName}.ShutdownCore"),
            StringComparer.Ordinal);
        Assert.All(
            new[]
            {
                nameof(ManagedEtabsShutdownMachine.Shutdown),
                nameof(ManagedEtabsShutdownMachine.ShutdownAfterRecoveryRecordWriteFailure)
            },
            entry => Assert.True(
                ReachesAny($"{typeof(ManagedEtabsShutdownMachine).FullName}.{entry}", [dispose]),
                $"{entry} no longer disposes the managed window guard."));

        // And a failed launch retires it too, before anything is asked to exit. The guard
        // is the only IDisposable that method touches — the owned process handle is
        // disposed a level down, in StopOwnedProcess — so this call is that disposal.
        Assert.Contains(
            "System.IDisposable.Dispose",
            Calls("EtabExtension.CLI.Shared.Infrastructure.Etabs.Session.ManagedEtabsLauncher" +
                ".CleanUpFailedStart"),
            StringComparer.Ordinal);
    }

    private const string OpenDispatch = "DispatchOpenModelAsync";

    /// <summary>
    /// Where <paramref name="call"/> appears in <paramref name="from"/>'s IL. Order matters
    /// for the startup guard in a way reachability cannot express: the whole defect is a
    /// window that was up for five seconds before anything hid it.
    /// </summary>
    private static int IndexOfCall(string from, string call)
    {
        var calls = Calls(from);
        var index = calls.ToList().IndexOf(call);
        Assert.True(index >= 0, $"{from} no longer calls {call}.");
        return index;
    }

    /// <summary>
    /// Every production method that touches one of <paramref name="seams"/> directly and is
    /// not on <paramref name="allowed"/>.
    /// </summary>
    private static string[] OffendersAgainst(string[] seams, string[] allowed) => ProductionCalls
        .Where(method => method.Value.Any(call => seams.Contains(call, StringComparer.Ordinal)))
        .Select(method => method.Key)
        .Where(name => !allowed.Contains(name, StringComparer.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<string> Calls(string methodFullName)
    {
        Assert.True(
            ProductionCalls.ContainsKey(methodFullName),
            $"{methodFullName} was not found in the production assembly — was it renamed or moved?");
        return ProductionCalls[methodFullName];
    }

    /// <summary>
    /// Whether any of <paramref name="targets"/> is callable from <paramref name="from"/>
    /// through production code. Transitive on purpose: moving a call one helper deeper is
    /// the cheapest way to slip past a direct-call assertion, and it changes nothing about
    /// what the daemon actually does.
    /// </summary>
    private static bool ReachesAny(string from, string[] targets)
    {
        Assert.True(
            ProductionCalls.ContainsKey(from),
            $"{from} was not found in the production assembly — was it renamed or moved?");

        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Queue<string>();
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            foreach (var call in ProductionCalls[pending.Dequeue()])
            {
                if (targets.Contains(call, StringComparer.Ordinal))
                {
                    return true;
                }

                if (ProductionCalls.ContainsKey(call) && seen.Add(call))
                {
                    pending.Enqueue(call);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A CSI/EtabSharp visibility call as opposed to one of our own seams. Our
    /// <c>IEtabsVisibilityApi</c> declares methods with the SAME names, so the owning type
    /// is what discriminates: anything outside the <c>EtabExtension.CLI</c> namespace root
    /// is the real application-visibility surface.
    /// </summary>
    private static bool IsCsiVisibilityCall(string call) =>
        !call.StartsWith("EtabExtension.CLI.", StringComparison.Ordinal)
        && CsiVisibilityMethods.Any(method =>
            call.EndsWith($".{method}", StringComparison.Ordinal));

    private static string OwningType(string methodFullName) =>
        methodFullName[..methodFullName.LastIndexOf('.')];
}

/// <summary>
/// A minimal IL reader: every method each production METHOD calls, keyed
/// <c>Namespace.Type.Method</c>.
///
/// <para>Compiler-generated members are attributed back to the method that wrote them —
/// an async state machine <c>&lt;Foo&gt;d__7</c> and a lambda <c>&lt;Foo&gt;b__7_0</c>
/// both count as <c>Foo</c> — so `await`ing or capturing does not launder a call out of
/// the guard.</para>
///
/// <para>Deliberately named apart from any other IL helper in this suite: sibling
/// branches carry their own scanners, and two files declaring one type name would not
/// compose.</para>
/// </summary>
internal static class VisibilityCallGraph
{
    private const byte TwoBytePrefix = 0xFE;
    private const string ProductionNamespaceRoot = "EtabExtension.CLI";
    private const string TestNamespace = "EtabExtension.CLI.Tests";

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opCode => opCode.Value)
        .ToDictionary(group => group.Key, group => group.First());

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByProductionMethod(
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = OpenAssembly(assembly);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var byMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (definition.IsNested || !IsProduction(reader.GetString(definition.Namespace)))
            {
                continue;
            }

            var owner = FullName(reader, handle);
            foreach (var typeHandle in WithNestedTypes(reader, handle))
            {
                var nested = reader.GetTypeDefinition(typeHandle);
                var typeOrigin = Origin(reader.GetString(nested.Name));
                foreach (var methodHandle in nested.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var name = reader.GetString(method.Name);
                    var key = $"{owner}.{Origin(name) ?? typeOrigin ?? name}";
                    if (!byMethod.TryGetValue(key, out var calls))
                    {
                        calls = [];
                        byMethod[key] = calls;
                    }

                    Collect(peReader, reader, method, calls);
                }
            }
        }

        return byMethod.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The originating method behind a compiler-generated name: <c>&lt;Foo&gt;d__7</c>,
    /// <c>&lt;Foo&gt;b__7_0</c> and <c>&lt;Foo&gt;g__Local|7_0</c> all yield <c>Foo</c>.
    /// Display-class names such as <c>&lt;&gt;c</c> yield null and fall through to the
    /// method's own name.
    ///
    /// <para>Unwraps repeatedly and matches angle brackets by depth, because the nesting
    /// really does stack: an async lambda inside an async method compiles to
    /// <c>&lt;&lt;Foo&gt;b__0&gt;d</c>, and stopping at the first <c>&gt;</c> would
    /// attribute its calls to a method that does not exist.</para>
    /// </summary>
    private static string? Origin(string name)
    {
        var unwrapped = false;
        while (name.Length > 0 && name[0] == '<')
        {
            unwrapped = true;
            var depth = 0;
            var end = -1;
            for (var index = 0; index < name.Length; index++)
            {
                if (name[index] == '<')
                {
                    depth++;
                }
                else if (name[index] == '>' && --depth == 0)
                {
                    end = index;
                    break;
                }
            }

            if (end <= 1)
            {
                return null;
            }

            name = name[1..end];
        }

        // A plain method name is NOT an origin. Returning it here would key an async
        // method's MoveNext under "MoveNext" and quietly drop the entire body of every
        // async method from the graph — which is most of the dispatcher.
        return unwrapped && name.Length > 0 ? name : null;
    }

    private static bool IsProduction(string? typeNamespace) =>
        typeNamespace is not null
        && typeNamespace.StartsWith(ProductionNamespaceRoot, StringComparison.Ordinal)
        && !typeNamespace.Equals(TestNamespace, StringComparison.Ordinal)
        && !typeNamespace.StartsWith($"{TestNamespace}.", StringComparison.Ordinal);

    private static FileStream OpenAssembly(Assembly assembly)
    {
        var location = assembly.Location;
        return string.IsNullOrEmpty(location)
            ? throw new InvalidOperationException(
                $"{assembly.FullName} has no file on disk to read IL from.")
            : File.OpenRead(location);
    }

    private static void Collect(
        PEReader peReader,
        MetadataReader reader,
        MethodDefinition method,
        List<string> calls)
    {
        if (method.RelativeVirtualAddress == 0)
        {
            return;
        }

        var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il is null)
        {
            return;
        }

        foreach (var token in CalledTokens(il))
        {
            calls.Add(Describe(reader, token));
        }
    }

    private static List<TypeDefinitionHandle> WithNestedTypes(
        MetadataReader reader,
        TypeDefinitionHandle root)
    {
        var all = new List<TypeDefinitionHandle>();
        var pending = new Queue<TypeDefinitionHandle>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var handle = pending.Dequeue();
            all.Add(handle);
            foreach (var nested in reader.GetTypeDefinition(handle).GetNestedTypes())
            {
                pending.Enqueue(nested);
            }
        }

        return all;
    }

    private static List<int> CalledTokens(byte[] il)
    {
        var tokens = new List<int>();
        var offset = 0;
        while (offset < il.Length)
        {
            short value = il[offset];
            offset++;
            if (value == TwoBytePrefix && offset < il.Length)
            {
                value = unchecked((short)((TwoBytePrefix << 8) | il[offset]));
                offset++;
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                throw new InvalidOperationException(
                    $"Unknown IL opcode 0x{value:X4} at offset {offset - 1}");
            }

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                var branches = BitConverter.ToInt32(il, offset);
                offset += 4 + (4 * branches);
                continue;
            }

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                tokens.Add(BitConverter.ToInt32(il, offset));
            }

            offset += OperandSize(opCode.OperandType);
        }

        return tokens;
    }

    private static int OperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4
    };

    private static string Describe(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return $"{OwnerName(reader, member.Parent)}.{reader.GetString(member.Name)}";

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return $"{FullName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";

            case HandleKind.MethodSpecification:
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return Describe(reader, MetadataTokens.GetToken(specification.Method));

            default:
                return token.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string OwnerName(MetadataReader reader, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)parent),
        _ => "?"
    };

    private static string FullName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);
        var space = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var space = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }
}
