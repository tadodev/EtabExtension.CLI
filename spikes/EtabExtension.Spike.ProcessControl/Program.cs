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
using System.Security.Cryptography;
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
    private static readonly TimeSpan WatchdogJoinDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(60);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>
    /// The exact process this spike owns. Published ONCE, synchronously, before the
    /// watchdog starts and before ETABS is resumed — so every later reader, on any
    /// thread, sees a fully initialised identity. The watchdog OBSERVES this; it never
    /// initialises it. (Fix 1: the previous shape let the attach loop reach a
    /// still-zero pid and falsely call the correct process foreign.)
    /// </summary>
    private sealed record OwnedIdentity(int Pid, DateTime StartUtc, string ExecutablePath);

    private static OwnedIdentity? _owned;

    private static volatile bool _ownershipViolated;
    private static string _ownershipViolation = string.Empty;

    /// <summary>
    /// Retirement boundary for the observer. Ownership violations are preserved for the
    /// whole experiment, then the watchdog is stopped and joined BEFORE we intentionally
    /// make ETABS exit — otherwise the deliberate disappearance of the sole owned process
    /// would be recorded as a violation and would invalidate a run that actually
    /// succeeded. (Fix 2.)
    /// </summary>
    private static readonly ManualResetEventSlim WatchdogStop = new(false);

    private static Thread? _watchdogThread;
    private static volatile bool _watchdogRetired;

    private static int Main(string[] args)
    {
        var etabs = Arg(args, "--etabs")
            ?? @"C:\Program Files\Computers and Structures\ETABS 23\ETABS.exe";
        var model = Arg(args, "--model");
        var waitForResume = !args.Contains("--no-handshake", StringComparer.Ordinal);

        EmitRuntimeIdentity();

        Emit("spike-start", new
        {
            etabs,
            model,
            waitForResume,
            note = "throwaway process-control spike; no window is ever manipulated"
        });

        // Fix 3: the model-open proof is part of the strong-positive predicate, so a run
        // without a model cannot produce the result we agreed to measure.
        if (string.IsNullOrWhiteSpace(model))
        {
            Emit("fail-closed", new
            {
                stage = "arguments",
                reason = "--model is required; the sanctioned run must prove an exact model open"
            });
            return 8;
        }

        if (!File.Exists(etabs))
        {
            Emit("fail-closed", new { stage = "preflight", reason = $"ETABS not found at '{etabs}'" });
            return 2;
        }

        if (!File.Exists(model))
        {
            Emit("fail-closed", new { stage = "preflight", reason = $"model not found at '{model}'" });
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
            // established before ETABS can have done anything at all, and it is PUBLISHED
            // before anything can read it.
            var identity = Process.GetProcessById(owned.ProcessId);
            var published = new OwnedIdentity(
                owned.ProcessId,
                identity.StartTime.ToUniversalTime(),
                etabs);
            Volatile.Write(ref _owned, published);

            Emit("created-suspended", new
            {
                pid = published.Pid,
                threadId = owned.ThreadId,
                startUtc = published.StartUtc.ToString("o", CultureInfo.InvariantCulture),
                executablePath = published.ExecutablePath,
                showWindow = "SW_HIDE via STARTF_USESHOWWINDOW",
                creationFlags = "CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT",
                identityPublished = true
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

            // Identity is already published, so the watchdog starts as a pure observer
            // and the attach loop cannot race it.
            StartOwnershipWatchdog();

            if (!RequireOwnership("before-resume"))
            {
                return 5;
            }

            // ---- gate 3: first instruction of ETABS ---------------------------
            NativeProcessLaunch.Resume(owned);
            Emit("resumed", new { pid = published.Pid });

            // ---- gate 4: attach to THAT pid, not "an ETABS" -------------------
            api = AttachToOwnedProcess(published.Pid);
            if (api is null)
            {
                Emit("fail-closed", new
                {
                    stage = "attach",
                    reason = _ownershipViolated ? _ownershipViolation : "attach deadline expired"
                });
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

            // ---- gate 5: the model-open proof is HARD -------------------------
            if (!OpenModelProven(api, model))
            {
                Emit("fail-closed", new
                {
                    stage = "model-open-proof",
                    reason = "exact model open was not proven; stopping BEFORE explicit reveal"
                });
                return 9;
            }

            if (!RequireOwnership("after-model-open"))
            {
                return 5;
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
    /// Which code is actually running. This project is framework-dependent, so no single
    /// EXE hash describes it — the interop assembly that really loads is recorded here,
    /// by path, version and content hash, and matched against the staged file set before
    /// the live run.
    /// </summary>
    private static void EmitRuntimeIdentity()
    {
        var spike = typeof(Program).Assembly;
        var interop = typeof(cHelper).Assembly;

        Emit("runtime-identity", new
        {
            spikeAssembly = Describe(spike),
            interopAssembly = Describe(interop),
            processPath = Environment.ProcessPath,
            runtime = Environment.Version.ToString(),
            note = "interopAssembly.location settles WHICH ETABSv1 was loaded at run time"
        });
    }

    private static object Describe(System.Reflection.Assembly assembly)
    {
        var location = assembly.Location;
        string? fileVersion = null;
        string? sha256 = null;
        long? size = null;
        try
        {
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                fileVersion = FileVersionInfo.GetVersionInfo(location).FileVersion;
                size = new FileInfo(location).Length;
                sha256 = HashFile(location);
            }
        }
        catch (Exception exception)
        {
            fileVersion = $"unreadable: {exception.Message}";
        }

        return new
        {
            fullName = assembly.FullName,
            location,
            fileVersion,
            sizeBytes = size,
            sha256
        };
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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

    /// <summary>
    /// The model-open proof, as a hard gate. Returns true ONLY when <c>OpenFile</c>
    /// returned zero AND the model ETABS reports open is exactly the file requested.
    /// A logged-but-tolerated failure here would let a broken attach path exit
    /// successfully, which is the opposite of what this experiment is for.
    /// </summary>
    private static bool OpenModelProven(cOAPI api, string model)
    {
        var started = Clock.Elapsed;
        try
        {
            var returnCode = api.SapModel.File.OpenFile(model);
            var openedName = api.SapModel.GetModelFilename(true);

            var exactMatch =
                !string.IsNullOrWhiteSpace(openedName)
                && string.Equals(
                    Path.GetFullPath(openedName),
                    Path.GetFullPath(model),
                    StringComparison.OrdinalIgnoreCase);

            var proven = returnCode == 0 && exactMatch;

            Emit("model-open", new
            {
                requested = model,
                returnCode,
                openedFilename = openedName,
                exactMatch,
                proven,
                elapsedMs = (Clock.Elapsed - started).TotalMilliseconds
            });

            return proven;
        }
        catch (Exception exception)
        {
            Emit("model-open-failed", new
            {
                requested = model,
                exceptionType = exception.GetType().Name,
                message = exception.Message
            });
            return false;
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
    /// worked around. Pure observer: the identity it checks was published synchronously
    /// before this thread existed.
    /// </summary>
    private static void StartOwnershipWatchdog()
    {
        var identity = Volatile.Read(ref _owned)
            ?? throw new InvalidOperationException(
                "the owned identity must be published before the watchdog starts");

        _watchdogThread = new Thread(() =>
        {
            while (!WatchdogStop.IsSet && !_ownershipViolated)
            {
                _ = RequireOwnership("watchdog");
                _ = WatchdogStop.Wait(OwnershipInterval);
            }
        })
        {
            IsBackground = true,
            Name = "spike-ownership-watchdog"
        };
        _watchdogThread.Start();

        Emit("ownership-watchdog-armed", new
        {
            identity.Pid,
            startUtc = identity.StartUtc.ToString("o", CultureInfo.InvariantCulture),
            intervalMs = OwnershipInterval.TotalMilliseconds
        });
    }

    /// <summary>
    /// Stops and joins the observer before any intentional ETABS shutdown, so a deliberate
    /// exit can never be recorded as an ownership violation. Violations observed BEFORE
    /// this point are preserved — retirement does not clear them.
    /// </summary>
    private static void RetireOwnershipWatchdog(string reason)
    {
        if (_watchdogRetired)
        {
            return;
        }

        _watchdogRetired = true;
        WatchdogStop.Set();
        var joined = _watchdogThread is null || _watchdogThread.Join(WatchdogJoinDeadline);

        Emit("ownership-watchdog-retired", new
        {
            reason,
            joined,
            violationsPreserved = _ownershipViolated,
            violation = _ownershipViolated ? _ownershipViolation : null
        });
    }

    private static bool RequireOwnership(string stage)
    {
        if (_ownershipViolated)
        {
            return false;
        }

        // After retirement the sole owned process is expected to disappear, so this is
        // no longer a meaningful question.
        if (_watchdogRetired)
        {
            return true;
        }

        var identity = Volatile.Read(ref _owned);
        if (identity is null)
        {
            Violate(stage, "the owned identity was not published before this check");
            return false;
        }

        var live = Process.GetProcessesByName(EtabsProcessName);
        if (live.Length != 1)
        {
            Violate(stage, $"expected exactly one ETABS process, observed {live.Length}: [{string.Join(", ", live.Select(p => p.Id))}]");
            return false;
        }

        if (live[0].Id != identity.Pid)
        {
            Violate(stage, $"the single ETABS process is pid {live[0].Id}, not the owned pid {identity.Pid}");
            return false;
        }

        try
        {
            if (live[0].StartTime.ToUniversalTime() != identity.StartUtc)
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
            RetireOwnershipWatchdog("teardown-without-process");
            return;
        }

        // Fix 2: retire the observer BEFORE we intentionally remove the sole owned ETABS.
        RetireOwnershipWatchdog("intentional-teardown");

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

        var exited = WaitForOwnedExit(owned.ProcessId);
        if (!exited)
        {
            Emit("forcing-exit", new { pid = owned.ProcessId, reason = "did not exit within the deadline" });
            NativeProcessLaunch.TerminateOwned(owned);
            exited = WaitForOwnedExit(owned.ProcessId);
        }

        NativeProcessLaunch.CloseHandles(owned);
        Emit("teardown", new
        {
            pid = owned.ProcessId,
            graceful,
            processExitConfirmed = exited,
            etabsProcessesRemaining = Process.GetProcessesByName(EtabsProcessName).Length,
            ownershipViolatedDuringExperiment = _ownershipViolated
        });
    }

    private static bool WaitForOwnedExit(int pid)
    {
        var deadline = Clock.Elapsed + ExitDeadline;
        while (Clock.Elapsed < deadline)
        {
            try
            {
                var p = Process.GetProcessById(pid);
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
