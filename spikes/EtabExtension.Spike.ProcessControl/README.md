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
- `--model` is required, and the open must be **proven** → abort before reveal otherwise.
- Any unexpected exception → abort.
- A `finally` guarantees the created process is dead on every path.

## Evidence defects found in review, and how they are fixed

Three ways the first draft could have falsely invalidated itself or falsely reported
success. All three are evidence-integrity bugs, not feature gaps.

1. **Ownership initialisation race.** The watchdog thread used to *initialise*
   `_ownedPid`/`_ownedStartUtc` on its first tick, while the main thread resumed ETABS and
   entered the attach loop immediately. If the main thread checked first, the owned pid
   was still `0` and the spike would declare the correct process foreign.
   **Fix:** the identity is a single immutable `OwnedIdentity` record, published once with
   `Volatile.Write` *before* the watchdog starts and *before* `ResumeThread`. The watchdog
   now only observes. A check that finds no published identity is itself a violation.

2. **Teardown manufacturing a violation.** The watchdog had no stop path, so when
   `ApplicationExit(false)` deliberately removed the sole owned ETABS, the watchdog could
   observe zero processes and set `_ownershipViolated` — invalidating a run that had
   actually succeeded.
   **Fix:** an explicit retirement boundary. `RetireOwnershipWatchdog()` sets a stop event
   and joins the thread *before* any intentional shutdown. Violations observed before that
   point are preserved and reported; retirement never clears them.

3. **A broken attach path could exit `0`.** `OpenModel` logged a non-zero `OpenFile`, a
   filename mismatch or an exception, then fell through to `Unhide()` and `spike-complete`.
   Since "the model opens" is part of the strong-positive predicate, that could report
   success over a broken run.
   **Fix:** `--model` is mandatory, and `OpenModelProven()` returns true only when
   `OpenFile == 0` **and** `GetModelFilename(true)` matches the requested path exactly.
   A failed proof stops the run *before* the explicit reveal, with a distinct exit code.

## Runtime identity

This project is framework-dependent, so no single EXE hash describes the running code. On
startup the spike emits a `runtime-identity` event recording, for both its own assembly and
the **interop assembly that actually loaded**, the full name, location, file version, size
and SHA-256 — `typeof(cHelper).Assembly.Location` settles which `ETABSv1` was bound rather
than leaving it to inference. The staged file set is hashed separately before the run and
the two are compared.

Note the staging rule here differs from the production sidecar deliberately: the shipped
`etab-cli.exe` is staged with **no** neighbouring `ETABSv1.dll` so that runtime resolution
of the customer's installed ETABS 23 API is exercised. This spike is a throwaway research
tool, not a shipped artifact — it carries its interop locally, and the `runtime-identity`
event is what makes that a matter of record instead of an assumption.

## Exit codes

| code | meaning |
| --- | --- |
| 0 | completed, ownership never violated |
| 2 | preflight failed (ETABS already running, or a required path missing) |
| 3 | resume handshake failed |
| 4 | attach failed or deadline expired |
| 5 | ownership violated |
| 6 | completed, but ownership was violated during the experiment |
| 7 | unexpected exception |
| 8 | `--model` not supplied |
| 9 | exact model-open proof failed (stopped before reveal) |

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
