# Process-control + PID-scoped attach spike

**THROWAWAY. NOT PRODUCTION. NOT FOR MERGE.** Branch `spike/alpha-22-process-control-attach`.

## The one question

Can a background ETABS be kept off-screen for the **whole** of its startup by owning
process creation, instead of letting `cHelper.CreateObject` start it and then reacting to
windows that already exist?

Everything tested before this acted too late:

| mechanism | verdict | why |
| --- | --- | --- |
| out-of-process `ShowWindow(SW_HIDE)` | **crashes ETABS** | 4/4 runs, NPE inside ETABS's own `NativeWindow.Callback` |
| do nothing | 8.8–13.4 s visible | violates the contract |
| `SetWinEventHook` + pump | harmless, buys nothing | exonerated as a crash cause; no measurable exposure benefit |
| pre-`ApplicationStart` `cOAPI.Hide()` | **not serviceable** | throws `NullReferenceException` ~12 ms in |
| post-`ApplicationStart` `cOAPI.Hide()` | action works (n=2) | but cannot be called until `ApplicationStart` returns — which *is* the exposure |

Every one of those either needs an HWND to already exist, or needs `ApplicationStart` to
have returned. The exposure window is exactly the interval where neither holds.

`STARTUPINFOW.wShowWindow` = `SW_HIDE` with `STARTF_USESHOWWINDOW` is supplied **at process
creation**. It is the only lever tested so far that operates before the target's first
window exists, with no injection and no message sent into ETABS.

## Why attach is defensible here, when it was not before

`cHelper.GetObject(progId)` resolves *the first running instance* — the ambiguity class
behind #187, and the reason "launch something and grab it from the ROT" was rejected.

This spike does not use it. `cHelper.GetObjectProcess(progId, pid)` attaches **by process
id**, and the pid it is given is the one this program created and still holds an open
handle on. A pid is only unambiguous while a handle keeps Windows from recycling it, and
this program is the thing holding that handle for the entire run.

## Sequence

```
preflight: zero ETABS.exe                      -> else FAIL CLOSED, nothing created
CreateProcessW(exact exe,
               CREATE_SUSPENDED,
               STARTF_USESHOWWINDOW, SW_HIDE)  -> pid + handle before any instruction runs
capture identity triple while frozen           -> pid + process-start-UTC + exe path
handshake: wait for "resume" on stdin          -> external HWND telemetry armed first
ownership watchdog armed (250 ms)              -> exactly one ETABS, ours, forever
ResumeThread                                   -> ETABS begins to execute
attach loop (<=120 s, 250 ms)                  -> GetObjectProcess(progId, ourPid)
readiness probe                                -> RECORD SapModel / Visible(); assume nothing
                                                  ApplicationStart is deliberately NOT called
open sample_v2.EDB                             -> return code + filename read-back
explicit intent: cOAPI.Unhide()                -> the ONLY moment a window should appear
teardown                                       -> ApplicationExit(false), confirm exit,
                                                  force-kill by our own handle if needed
```

## Fail-closed rules

- Preflight non-zero → abort before creating anything.
- Ownership is re-proven before every attach attempt **and** on a 250 ms watchdog:
  exactly one ETABS, pid matches, process-start-time matches. Any violation aborts the
  run and invalidates the experiment rather than being worked around.
- Attach deadline expiry → abort.
- Any unexpected exception → abort.
- A `finally` guarantees the created process is dead on every path.

## What this spike must never do

No `ShowWindow`. No `SetWindowPos`. No `SetWinEventHook`. No hooks of any kind, no
injection. Out-of-process `SW_HIDE` actuation is the proven ETABS-crash actuator and is
excluded by construction — this project does not even reference the production assembly
that contains it.

## What decides the result

Not this program. The external high-frequency HWND telemetry is the oracle, exactly as in
the previous four live runs. This program only reports what it did and when, one JSON line
per event on stdout, so the two timelines can be correlated.

**Strong positive:** no exact-owned top-level ETABS HWND is ever observed `IsWindowVisible`
from `ResumeThread` until `cOAPI.Unhide()`, and the model opens successfully.

**Negative:** any real visible interval before the explicit `Unhide()`. `SW_HIDE` at
creation does not constrain windows ETABS later shows explicitly, so this is a live
question, not a formality.

## Build

Not in `EtabExtension.CLI.slnx`, no reference to the production project — the shipped
build and the certification build are unaffected.

```
dotnet build spikes/EtabExtension.Spike.ProcessControl/EtabExtension.Spike.ProcessControl.csproj -c Release
```
