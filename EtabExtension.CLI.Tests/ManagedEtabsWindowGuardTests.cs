// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Reflection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The Windows suppression layer the #20 supervised certification proved was missing.
///
/// <para>The candidate hid ETABS through CSI alone and was measured showing a real window
/// for 5.19 s of a background run, inside <c>ApplicationStart</c>, before <c>open-model</c>
/// was ever called. A convergence wait cannot close that: it can only measure the window
/// it waits through. This guard holds the window down while CSI catches up.</para>
///
/// <para>Its entire risk is targeting, so that is what these tests are mostly about: a
/// guard that could reach a window it does not own would be a far worse defect than the one
/// it fixes.</para>
/// </summary>
public sealed class ManagedEtabsWindowGuardTests
{
    private static readonly ManagedProcessIdentity Owned = new(
        4242,
        new DateTimeOffset(2026, 8, 21, 5, 12, 12, TimeSpan.Zero),
        @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe");

    private const int ForeignPid = 4243;

    [Fact]
    public void ASweepHidesEveryVisibleWindowOfTheExactOwnedProcess()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(10, Owned.Pid, IsVisible: true),
            new TopLevelWindow(11, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);

        guard.SweepOnce();

        Assert.Equal([10, 11], windows.Hidden);
        Assert.Equal([(nint)10, 11], guard.Suppressed);
        Assert.True(guard.IsActive);
    }

    /// <summary>
    /// The rule the whole design rests on. A foreign process's window is not hidden, not
    /// shown, and not recorded — even when it is the only visible window on the desktop and
    /// even when it belongs to another ETABS.
    /// </summary>
    [Fact]
    public void NoWindowOfAnyOtherProcessIsEverTouched()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(20, ForeignPid, IsVisible: true),
            new TopLevelWindow(21, Owned.Pid, IsVisible: true),
            new TopLevelWindow(22, ForeignPid, IsVisible: true));
        var guard = Guard(windows, out _);

        guard.SweepOnce();
        guard.ReleaseForExplicitUserAction();

        Assert.Equal([21], windows.Hidden);
        Assert.Equal([21], windows.Shown);
        Assert.DoesNotContain((nint)20, windows.Touched);
        Assert.DoesNotContain((nint)22, windows.Touched);
    }

    /// <summary>
    /// A pid is only provably ours while the authoritative handle keeps Windows from
    /// recycling it. Once the process is gone the pid belongs to whoever gets it next, so
    /// the guard stops for good rather than acting on it.
    /// </summary>
    [Fact]
    public void AnExitedOwnedProcessStopsTheGuardInsteadOfActingOnItsPid()
    {
        var windows = new FakeWindows(new TopLevelWindow(30, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out var owned);
        owned.Exit();

        guard.SweepOnce();
        guard.SweepOnce();

        Assert.Empty(windows.Touched);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// Cardex's non-idempotency lesson applied to windows: a window that is already down
    /// is left alone, so the guard cannot churn the desktop while it waits.
    /// </summary>
    [Fact]
    public void AlreadyHiddenWindowsOfTheOwnedProcessAreLeftAlone()
    {
        var windows = new FakeWindows(new TopLevelWindow(40, Owned.Pid, IsVisible: false));
        var guard = Guard(windows, out _);

        guard.SweepOnce();

        Assert.Empty(windows.Touched);
        Assert.Empty(guard.Suppressed);
    }

    [Fact]
    public void RepeatedSweepsRecordEachSuppressedWindowOnce()
    {
        var windows = new FakeWindows(new TopLevelWindow(50, Owned.Pid, IsVisible: true))
        {
            StaysVisible = true
        };
        var guard = Guard(windows, out _);

        guard.SweepOnce();
        guard.SweepOnce();
        guard.SweepOnce();

        Assert.Equal([50, 50, 50], windows.Hidden);
        Assert.Equal([(nint)50], guard.Suppressed);

        guard.ReleaseForExplicitUserAction();
        Assert.Equal([50], windows.Shown);
    }

    /// <summary>
    /// The explicit-reveal half of the latch. The windows this guard hid go back, because
    /// <c>cOAPI.Visible()</c> may be derived from the real window state — in which case CSI
    /// would read its own application as already visible and refuse the <c>Unhide</c>,
    /// leaving the engineer with nothing on screen.
    /// </summary>
    [Fact]
    public void ReleasingForAnExplicitUserActionRestoresExactlyWhatWasSuppressed()
    {
        var windows = new FakeWindows(
            new TopLevelWindow(60, Owned.Pid, IsVisible: true),
            new TopLevelWindow(61, ForeignPid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();

        guard.ReleaseForExplicitUserAction();

        Assert.Equal([60], windows.Shown);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// The restore-time ownership recheck. Suppression filters by owning process id, but
    /// what gets SAVED is a raw <c>HWND</c> value — and the open process handle does not
    /// protect that. It keeps Windows from recycling the pid; it says nothing about a handle
    /// value, which Windows may hand to a different window in a different process the moment
    /// ETABS destroys the one we hid.
    ///
    /// <para>So a reveal that showed the saved list blind would <c>ShowWindow</c> a
    /// stranger's window — a worse defect than the one this guard exists to fix. Ownership
    /// is therefore re-proven against a fresh census at restore time.</para>
    /// </summary>
    [Fact]
    public void ASuppressedHandleThatNowBelongsToAnotherProcessIsNeverShown()
    {
        var windows = new FakeWindows(new TopLevelWindow(60, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();
        Assert.Equal([(nint)60], guard.Suppressed);

        // ETABS destroyed that window between the sweep and the reveal, and Windows handed
        // the handle value to somebody else.
        windows.Reassign(60, ForeignPid);

        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
        Assert.DoesNotContain((nint)60, windows.Touched.Skip(windows.Hidden.Count));
    }

    /// <summary>A handle that simply no longer exists is skipped rather than shown.</summary>
    [Fact]
    public void ASuppressedHandleThatNoLongerExistsIsNeverShown()
    {
        var windows = new FakeWindows(new TopLevelWindow(61, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();

        windows.Destroy(61);
        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    /// <summary>
    /// And once the owned process is gone its pid is no longer provably ours, so a reveal
    /// restores nothing at all rather than reasoning about handles that outlived it.
    /// </summary>
    [Fact]
    public void AnExitedOwnedProcessRestoresNothingOnReveal()
    {
        var windows = new FakeWindows(new TopLevelWindow(62, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out var owned);
        guard.SweepOnce();
        owned.Exit();

        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    /// <summary>Shutdown is not a reveal: a process on its way out must not flash a window.</summary>
    [Fact]
    public void DisposingRestoresNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(70, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();

        guard.Dispose();

        Assert.Empty(windows.Shown);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// The latch. Once suppression has ended it never resumes, whatever calls arrive
    /// afterwards — which is exactly why a background command reusing a session the user
    /// asked to see cannot take the window away again.
    /// </summary>
    [Fact]
    public void SuppressionNeverResumesAfterAnExplicitRelease()
    {
        var windows = new FakeWindows(new TopLevelWindow(80, Owned.Pid, IsVisible: true))
        {
            StaysVisible = true
        };
        var guard = Guard(windows, out _);
        guard.ReleaseForExplicitUserAction();

        guard.SweepOnce();
        guard.SweepOnce();

        Assert.Empty(windows.Hidden);
        Assert.False(guard.IsActive);
    }

    /// <summary>
    /// Terminating twice — reveal then shutdown, or two shutdown routes — is idempotent.
    /// A second restore would put a window back that the first teardown already resolved.
    /// </summary>
    [Fact]
    public void TerminatingTwiceRestoresOnceAndReArmsNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(90, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();

        guard.ReleaseForExplicitUserAction();
        guard.Dispose();
        guard.ReleaseForExplicitUserAction();
        guard.SweepOnce();

        Assert.Equal([90], windows.Shown);
        Assert.Equal([90], windows.Hidden);
        Assert.False(guard.IsActive);
    }

    [Fact]
    public void DisposingAfterAShutdownStillRestoresNothing()
    {
        var windows = new FakeWindows(new TopLevelWindow(100, Owned.Pid, IsVisible: true));
        var guard = Guard(windows, out _);
        guard.SweepOnce();

        guard.Dispose();
        guard.ReleaseForExplicitUserAction();

        Assert.Empty(windows.Shown);
    }

    /// <summary>
    /// The production shape, with its own thread: suppression really does keep happening
    /// while the caller is blocked in <c>ApplicationStart</c>, and it really does stop on
    /// disposal rather than outliving the session.
    /// </summary>
    [Fact]
    public void TheRunningGuardKeepsSweepingUntilItIsDisposed()
    {
        var windows = new FakeWindows(new TopLevelWindow(110, Owned.Pid, IsVisible: true))
        {
            StaysVisible = true
        };
        var owned = new FakeOwnedProcess(Owned);
        using var guard = new ManagedEtabsWindowGuard(
            owned,
            windows,
            TimeSpan.FromMilliseconds(5),
            startPump: true);

        Assert.True(
            SpinUntil(() => guard.Sweeps >= 3, TimeSpan.FromSeconds(5)),
            $"the guard thread swept {guard.Sweeps} times");
        Assert.Contains((nint)110, guard.Suppressed);

        guard.Dispose();
        var afterDispose = guard.Sweeps;
        Assert.False(
            SpinUntil(() => guard.Sweeps > afterDispose, TimeSpan.FromMilliseconds(200)),
            "the guard thread kept sweeping after disposal");
        Assert.False(guard.IsActive);
        Assert.Null(guard.LastSweepError);
    }

    /// <summary>
    /// A window station that misbehaves must not take the daemon down with it. The CSI hide
    /// remains the authoritative transition; this layer is defence in depth.
    /// </summary>
    [Fact]
    public void ASweepFailureIsRecordedRatherThanThrownOutOfTheGuardThread()
    {
        var windows = new FakeWindows(new TopLevelWindow(120, Owned.Pid, IsVisible: true))
        {
            EnumerateException = new InvalidOperationException("window station went away")
        };
        var owned = new FakeOwnedProcess(Owned);
        using var guard = new ManagedEtabsWindowGuard(
            owned,
            windows,
            TimeSpan.FromMilliseconds(5),
            startPump: true);

        Assert.True(
            SpinUntil(() => guard.LastSweepError is not null, TimeSpan.FromSeconds(5)),
            "the guard thread never recorded the sweep failure");
        Assert.IsType<InvalidOperationException>(guard.LastSweepError);
    }

    /// <summary>
    /// The structural half of "never operates on an unproven or global ETABS pid": there is
    /// no signature through which a bare process id could be guarded. Activation takes the
    /// authoritative handle the launcher opened by exact identity, and nothing else.
    /// </summary>
    [Fact]
    public void TheOnlyWayToArmAGuardIsWithAnAuthoritativeOwnedHandle()
    {
        var methods = typeof(IManagedEtabsWindowGuardFactory).GetMethods();

        var activate = Assert.Single(methods);
        Assert.Equal(nameof(IManagedEtabsWindowGuardFactory.Activate), activate.Name);
        Assert.Equal(
            [typeof(IOwnedEtabsProcess)],
            activate.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.Throws<ArgumentNullException>(
            () => WindowsManagedEtabsWindowGuardFactory.Instance.Activate(null!));
    }

    /// <summary>
    /// And the guard's own surface offers no way back on. Suppression is armed once, by the
    /// launcher, and ended once — there is no re-arm to reach from a command path.
    /// </summary>
    [Fact]
    public void TheGuardContractExposesNoWayToResumeSuppression()
    {
        var members = typeof(IManagedEtabsWindowGuard)
            .GetInterfaces()
            .Append(typeof(IManagedEtabsWindowGuard))
            .SelectMany(contract => contract.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(member => member.Name)
            .Where(name => !name.StartsWith("get_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["Dispose", "Identity", "IsActive", "ReleaseForExplicitUserAction"],
            members);
    }

    /// <summary>The guard's identity is the handle's identity — never a value it was told.</summary>
    [Fact]
    public void TheGuardReportsTheIdentityOfTheHandleItWasGiven()
    {
        var guard = Guard(new FakeWindows(), out var owned);

        Assert.Equal(owned.Identity, guard.Identity);
        Assert.Equal(Owned, guard.Identity);
    }

    private static ManagedEtabsWindowGuard Guard(
        FakeWindows windows,
        out FakeOwnedProcess owned)
    {
        owned = new FakeOwnedProcess(Owned);
        return new ManagedEtabsWindowGuard(
            owned,
            windows,
            ManagedEtabsWindowGuard.DefaultSweepInterval,
            startPump: false);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return condition();
    }

    private sealed class FakeOwnedProcess(ManagedProcessIdentity identity) : IOwnedEtabsProcess
    {
        public ManagedProcessIdentity Identity { get; } = identity;
        public bool HasExited { get; private set; }
        public void Exit() => HasExited = true;
        public void Kill() => HasExited = true;
        public bool WaitForExit(TimeSpan timeout) => HasExited;
        public void Dispose()
        {
        }
    }

    private sealed class FakeWindows : ITopLevelWindows
    {
        private readonly List<TopLevelWindow> _windows;
        private readonly object _gate = new();

        public FakeWindows(params TopLevelWindow[] windows) => _windows = [.. windows];

        /// <summary>A window that refuses to go down, so repeated sweeps stay expressible.</summary>
        public bool StaysVisible { get; init; }

        public Exception? EnumerateException { get; init; }

        public List<nint> Hidden { get; } = [];

        public List<nint> Shown { get; } = [];

        public IEnumerable<nint> Touched => Hidden.Concat(Shown);

        public IReadOnlyList<TopLevelWindow> Enumerate()
        {
            if (EnumerateException is not null)
            {
                throw EnumerateException;
            }

            lock (_gate)
            {
                return [.. _windows];
            }
        }

        public void Hide(nint handle)
        {
            lock (_gate)
            {
                Hidden.Add(handle);
                if (!StaysVisible)
                {
                    Replace(handle, visible: false);
                }
            }
        }

        public void Show(nint handle)
        {
            lock (_gate)
            {
                Shown.Add(handle);
                Replace(handle, visible: true);
            }
        }

        /// <summary>Hands a live handle value to a different process, as Windows may.</summary>
        public void Reassign(nint handle, int processId)
        {
            lock (_gate)
            {
                for (var index = 0; index < _windows.Count; index++)
                {
                    if (_windows[index].Handle == handle)
                    {
                        _windows[index] = _windows[index] with { ProcessId = processId };
                    }
                }
            }
        }

        /// <summary>Destroys a window, so its handle stops appearing in the census.</summary>
        public void Destroy(nint handle)
        {
            lock (_gate)
            {
                _ = _windows.RemoveAll(window => window.Handle == handle);
            }
        }

        private void Replace(nint handle, bool visible)
        {
            for (var index = 0; index < _windows.Count; index++)
            {
                if (_windows[index].Handle == handle)
                {
                    _windows[index] = _windows[index] with { IsVisible = visible };
                }
            }
        }
    }
}
