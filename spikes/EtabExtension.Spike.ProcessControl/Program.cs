// THROWAWAY SPIKE — NOT PRODUCTION, NOT FOR MERGE.
//
// Answers one question and stops: can we keep a background ETABS off-screen for the
// WHOLE of its startup by owning process creation (CreateProcessW + STARTF_USESHOWWINDOW
// + SW_HIDE + CREATE_SUSPENDED) and then attaching to that exact pid, instead of letting
// cHelper.CreateObject start it and reacting to windows afterwards?
//
// Nothing here touches a window. No ShowWindow, no SetWindowPos, no SetWinEventHook.
// Out-of-process SW_HIDE actuation is the actuator proven to crash ETABS 23.3 and is
// excluded by construction. The external high-frequency HWND telemetry remains the
// oracle for whether this worked; this program only reports what it did and when.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ETABSv1;

namespace EtabExtension.Spike.ProcessControl;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string EtabsProgId = "CSI.ETABS.API.ETABSObject";
    private const string EtabsProcessName = "ETABS";

    private static readonly TimeSpan AttachDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AttachInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OwnershipInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(60);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static volatile bool _ownershipViolated;
    private static string _ownershipViolation = string.Empty;

    private static int Main(string[] args)
    {
        var etabs = Arg(args, "--etabs")
            ?? @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe";
        var model = Arg(args, "--model");
        var waitForResume = !args.Contains("--no-handshake", StringComparer.Ordinal);

        Emit("spike-start", new
        {
            etabs,
            model,
            waitForResume,
            note = "throwaway process-control spike; no window is ever manipulated"
        });

        if (!File.Exists(etabs))
        {
            Emit("fail-closed", new { stage = "preflight", reason = $"ETABS not found at '{etabs}'" });
            return 2;
        }

        // ---- gate 1: the desktop must be empty of ETABS -----------------------
        var preflight = Process.GetProcessesByName(EtabsProcessName);
        if (preflight.Length != 0)
        {
            Emit("fail-closed", new
            {
                stage = "preflight",
                reason = "ETABS already running; ownership could never be proven",
                pids = preflight.Select(p => p.Id).ToArray()
            });
            return 2;
        }

        Emit("preflight-clean", new { etabsProcesses = 0 });

        NativeProcessLaunch.SuspendedProcess? owned = null;
        cOAPI? api = null;
        try
        {
            // ---- gate 2: we create it, suspended and hidden -------------------
            owned = NativeProcessLaunch.CreateSuspendedHidden(etabs);

            // Identity is captured while the process is still frozen, so the triple is
            // established before ETABS can have done anything at all.
            var identity = Process.GetProcessById(owned.ProcessId);
            var startUtc = identity.StartTime.ToUniversalTime();
            Emit("created-suspended", new
            {
                pid = owned.ProcessId,
                threadId = owned.ThreadId,
                startUtc = startUtc.ToString("o", CultureInfo.InvariantCulture),
                executablePath = etabs,
                showWindow = "SW_HIDE via STARTF_USESHOWWINDOW",
                creationFlags = "CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT"
            });

            // ---- handshake: the observer is armed before ETABS runs -----------
            if (waitForResume)
            {
                Emit("awaiting-resume", new { hint = "write 'resume' on stdin once telemetry is armed" });
                var line = Console.ReadLine();
                if (!string.Equals(line?.Trim(), "resume", StringComparison.OrdinalIgnoreCase))
                {
                    Emit("fail-closed", new { stage = "handshake", reason = $"expected 'resume', got '{line}'" });
                    return 3;
                }
            }

            StartOwnershipWatchdog(owned.ProcessId, startUtc);

            // ---- gate 3: first instruction of ETABS ---------------------------
            NativeProcessLaunch.Resume(owned);
            Emit("resumed", new { pid = owned.ProcessId });

            // ---- gate 4: attach to THAT pid, not "an ETABS" -------------------
            api = AttachToOwnedProcess(owned.ProcessId);
            if (api is null)
            {
                Emit("fail-closed", new { stage = "attach", reason = _ownershipViolated ? _ownershipViolation : "attach deadline expired" });
                return 4;
            }

            // ---- readiness is RECORDED, never assumed -------------------------
            // Deliberately NOT calling ApplicationStart: CSI's manual-start guidance is
            // that an attached, already-started instance must not be started again. What
            // that means in practice on 23.3 is exactly what this records.
            ProbeReadiness(api);

            if (!RequireOwnership("after-attach"))
            {
                return 5;
            }

            // ---- optional: does a real model open work over this attach? ------
            if (!string.IsNullOrWhiteSpace(model))
            {
                OpenModel(api, model);
            }

            // ---- explicit user intent leg: CSI reveal, never ShowWindow -------
            RevealForExplicitIntent(api);

            Emit("spike-complete", new { ownershipViolated = _ownershipViolated });
            return _ownershipViolated ? 6 : 0;
        }
        catch (Exception exception)
        {
            Emit("fail-closed", new
            {
                stage = "unexpected",
                exceptionType = exception.GetType().FullName,
                message = exception.Message
            });
            return 7;
        }
        finally
        {
            // The spike never leaves ETABS behind, on any path.
            Teardown(api, owned);
        }
    }

    /// <summary>
    /// Polls <c>cHelper.GetObjectProcess(progId, pid)</c> — the PID-scoped attach.
    ///
    /// <para>This is the whole reason attach is defensible here. The generic
    /// <c>GetObject</c> resolves "the first running instance", which is precisely the
    /// ambiguity class that made attach unsafe before. <c>GetObjectProcess</c> names the
    /// process, and the process it names is the one this program created and still holds
    /// an open handle on.</para>
    ///
    /// <para>The ownership invariant is re-checked before every attempt, so a second
    /// ETABS appearing mid-attach invalidates the run instead of being silently tolerated.</para>
    /// </summary>
    private static cOAPI? AttachToOwnedProcess(int pid)
    {
        // Declared as the interface, exactly as the production factory does: the attach
        // methods are explicit interface implementations and are not reachable through
        // the concrete Helper type.
        cHelper helper = new Helper();
        var started = Clock.Elapsed;
        var attempts = 0;

        while (Clock.Elapsed - started < AttachDeadline)
        {
            if (!RequireOwnership("during-attach"))
            {
                return null;
            }

            attempts++;
            try
            {
                var api = helper.GetObjectProcess(EtabsProgId, pid);
                if (api is not null)
                {
                    Emit("attached", new
                    {
                        pid,
                        attempts,
                        elapsedMs = (Clock.Elapsed - started).TotalMilliseconds,
                        via = "cHelper.GetObjectProcess(progId, pid)"
                    });
                    return api;
                }
            }
            catch (Exception exception)
            {
                // Expected while ETABS is still coming up; recorded rather than swallowed
                // so the timeline shows how long the API took to become attachable.
                if (attempts == 1 || attempts % 20 == 0)
                {
                    Emit("attach-attempt-failed", new
                    {
                        attempts,
                        elapsedMs = (Clock.Elapsed - started).TotalMilliseconds,
                        exceptionType = exception.GetType().Name,
                        message = exception.Message
                    });
                }
            }

            Thread.Sleep(AttachInterval);
        }

        return null;
    }

    /// <summary>Records what the attached object reports. Asserts nothing, starts nothing.</summary>
    private static void ProbeReadiness(cOAPI api)
    {
        bool hasSapModel;
        string? sapModelError = null;
        try
        {
            hasSapModel = api.SapModel is not null;
        }
        catch (Exception exception)
        {
            hasSapModel = false;
            sapModelError = $"{exception.GetType().Name}: {exception.Message}";
        }

        bool? visible = null;
        string? visibleError = null;
        try
        {
            visible = api.Visible();
        }
        catch (Exception exception)
        {
            visibleError = $"{exception.GetType().Name}: {exception.Message}";
        }

        Emit("readiness-probe", new
        {
            hasSapModel,
            sapModelError,
            csiVisibleFlag = visible,
            visibleError,
            applicationStartCalled = false,
            note = "cOAPI.Visible() is telemetry only; the external HWND census is the oracle"
        });
    }

    private static void OpenModel(cOAPI api, string model)
    {
        if (!File.Exists(model))
        {
            Emit("model-open-skipped", new { model, reason = "file not found" });
            return;
        }

        var started = Clock.Elapsed;
        try
        {
            var returnCode = api.SapModel.File.OpenFile(model);
            var openedName = api.SapModel.GetModelFilename(true);
            Emit("model-open", new
            {
                requested = model,
                returnCode,
                openedFilename = openedName,
                exactMatch = string.Equals(
                    Path.GetFullPath(openedName ?? string.Empty),
                    Path.GetFullPath(model),
                    StringComparison.OrdinalIgnoreCase),
                elapsedMs = (Clock.Elapsed - started).TotalMilliseconds
            });
        }
        catch (Exception exception)
        {
            Emit("model-open-failed", new
            {
                requested = model,
                exceptionType = exception.GetType().Name,
                message = exception.Message
            });
        }
    }

    /// <summary>
    /// The explicit-user-intent leg, through CSI only. If the spike's premise holds, this
    /// is the ONLY moment an ETABS window should ever become visible, and the external
    /// telemetry should show the first visible interval starting here and nowhere earlier.
    /// </summary>
    private static void RevealForExplicitIntent(cOAPI api)
    {
        try
        {
            var returnCode = api.Unhide();
            Emit("explicit-reveal", new
            {
                call = "cOAPI.Unhide()",
                returnCode,
                note = "if the premise holds, the first visible HWND interval starts here"
            });
        }
        catch (Exception exception)
        {
            Emit("explicit-reveal-failed", new
            {
                exceptionType = exception.GetType().Name,
                message = exception.Message
            });
        }
    }

    /// <summary>
    /// Continuous ownership proof. Exactly one ETABS, and it is ours by pid AND by
    /// process start time. Anything else invalidates the experiment rather than being
    /// worked around.
    /// </summary>
    private static void StartOwnershipWatchdog(int pid, DateTime startUtc)
    {
        var thread = new Thread(() =>
        {
            while (!_ownershipViolated)
            {
                _ = RequireOwnership("watchdog", pid, startUtc);
                Thread.Sleep(OwnershipInterval);
            }
        })
        {
            IsBackground = true,
            Name = "spike-ownership-watchdog"
        };
        thread.Start();
        Emit("ownership-watchdog-armed", new { pid, intervalMs = OwnershipInterval.TotalMilliseconds });
    }

    private static int _ownedPid;
    private static DateTime _ownedStartUtc;

    private static bool RequireOwnership(string stage) =>
        RequireOwnership(stage, _ownedPid, _ownedStartUtc);

    private static bool RequireOwnership(string stage, int pid, DateTime startUtc)
    {
        if (pid != 0)
        {
            _ownedPid = pid;
            _ownedStartUtc = startUtc;
        }

        if (_ownershipViolated)
        {
            return false;
        }

        var live = Process.GetProcessesByName(EtabsProcessName);
        if (live.Length != 1)
        {
            Violate(stage, $"expected exactly one ETABS process, observed {live.Length}: [{string.Join(", ", live.Select(p => p.Id))}]");
            return false;
        }

        if (live[0].Id != _ownedPid)
        {
            Violate(stage, $"the single ETABS process is pid {live[0].Id}, not the owned pid {_ownedPid}");
            return false;
        }

        try
        {
            if (live[0].StartTime.ToUniversalTime() != _ownedStartUtc)
            {
                Violate(stage, "pid matches but process start time does not — the pid was recycled");
                return false;
            }
        }
        catch (Exception exception)
        {
            Violate(stage, $"could not read the owned process start time: {exception.Message}");
            return false;
        }

        return true;
    }

    private static void Violate(string stage, string reason)
    {
        _ownershipViolated = true;
        _ownershipViolation = $"{stage}: {reason}";
        Emit("ownership-violated", new { stage, reason });
    }

    private static void Teardown(cOAPI? api, NativeProcessLaunch.SuspendedProcess? owned)
    {
        if (owned is null)
        {
            return;
        }

        var graceful = false;
        if (api is not null)
        {
            try
            {
                var returnCode = api.ApplicationExit(false);
                Emit("application-exit", new { returnCode });
                graceful = returnCode == 0;
            }
            catch (Exception exception)
            {
                Emit("application-exit-failed", new
                {
                    exceptionType = exception.GetType().Name,
                    message = exception.Message
                });
            }
        }

        var exited = WaitForOwnedExit();
        if (!exited)
        {
            Emit("forcing-exit", new { pid = owned.ProcessId, reason = "did not exit within the deadline" });
            NativeProcessLaunch.TerminateOwned(owned);
            exited = WaitForOwnedExit();
        }

        NativeProcessLaunch.CloseHandles(owned);
        Emit("teardown", new
        {
            pid = owned.ProcessId,
            graceful,
            processExitConfirmed = exited,
            etabsProcessesRemaining = Process.GetProcessesByName(EtabsProcessName).Length
        });
    }

    private static bool WaitForOwnedExit()
    {
        var deadline = Clock.Elapsed + ExitDeadline;
        while (Clock.Elapsed < deadline)
        {
            try
            {
                var p = Process.GetProcessById(_ownedPid);
                if (p.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // no such process
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>One JSON line per event on stdout, so the runner can correlate to telemetry.</summary>
    private static void Emit(string @event, object payload)
    {
        var line = JsonSerializer.Serialize(new
        {
            elapsedMs = Math.Round(Clock.Elapsed.TotalMilliseconds, 1),
            utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            @event,
            payload
        });
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }
}
