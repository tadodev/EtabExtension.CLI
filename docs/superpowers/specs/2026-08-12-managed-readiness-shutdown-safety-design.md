# Managed ETABS Readiness and Shutdown Safety Design

**Date:** 2026-08-12

**Status:** Approved with durable-record sequencing correction

## Problem and Live Evidence

The focused Closed Alpha proof against the exact CLI PR #15 artifact established
two independent defects:

1. The persistent sidecar started and established exclusive `Managed` ownership
   of one ETABS process, but the first `OpenFile` call failed immediately. The
   managed launch path attaches COM and exposes the session without first
   initializing an empty `SapModel`.
2. The `shutdown` command returned success before session disposal. The sidecar
   exited and deleted its recovery record, but its exact owned ETABS process
   remained alive and required external exact-identity cleanup.

The model and its existing ETABS lock file were unchanged, and no snapshot,
export, analysis, calculation, or report command ran. This design corrects the
two lifecycle defects without broadening the Alpha workflow.

## Scope

This change is limited to the C# CLI persistent sidecar. It adds a deterministic
API-readiness boundary for newly launched managed ETABS sessions, bounded
operation diagnostics for `OpenModel`, and an authoritative shutdown state
machine whose protocol response follows cleanup.

It does not change EtabExtension, its Rust sidecar client, desktop work, issues
#323 or #216, PR #324, model contents, analysis behavior, report behavior, or
installer policy. It does not run another live ETABS proof automatically.

## CSI API Contract

The local Cardex ETABS 23 API cards are authoritative for the two CSI calls:

- `cSapModel.InitializeNewModel(eUnits)` returns zero on success and nonzero on
  failure. The call clears any prior model and initializes the application/model
  API state.
- `cOAPI.ApplicationExit(bool FileSave)` returns zero on success and nonzero on
  failure. `FileSave` remains `false` for every managed cleanup path.

No arbitrary delay or retry loop substitutes for either return-code contract.
The only waits are bounded waits on the authoritative owned process handle.

## Durable Ownership Before API Readiness

A newly launched managed session follows this exact sequence:

1. Resolve and start ETABS, retaining the authoritative process handle and its
   PID, UTC start time, and executable path.
2. Attach COM to that exact PID.
3. Verify exclusive managed ownership: exactly one identified ETABS process,
   its PID equals the owned PID, and no unidentified process exists.
4. Write the durable managed-session recovery record containing the full process
   identity and `ManagedLaunchRecordId`.
5. Invoke `SapModel.InitializeNewModel(eUnits.kip_in_F)` exactly once.
6. If the return code is zero, mark the in-memory session API-ready and return it.

The record proves recoverable ownership, not API readiness. It is deliberately
written before initialization so a sidecar crash during the CSI call leaves the
next daemon enough evidence to recover the exact owned process.

Only the first launch performs initialization. Reusing an established in-memory
session verifies ownership but never calls `InitializeNewModel` again.

## Initialization Failure

A nonzero return or exception from `InitializeNewModel` fails launch with the
stable code `ETABS_MODEL_INITIALIZATION_FAILED`. The diagnostic includes the
operation, return code or bounded exception evidence, and the owned PID.

Failure cleanup uses only the authoritative owned process handle:

1. Request `ApplicationExit(false)` when the COM object is usable.
2. Wait a bounded grace period for the exact process to exit.
3. Force terminate that exact process handle if it remains alive.
4. Wait for confirmed exit.
5. Clear the recovery record only after confirmed exit.

If exit cannot be confirmed, cleanup preserves the record and reports the
unresolved process state. No PID obtained only from enumeration is terminated.

## Bounded OpenModel Diagnostics

`OpenModel` tracks the active CSI call site before each COM/API interaction. An
exception produces a stable `ETABS_COM_OPERATION_FAILED` diagnostic containing:

- operation/call site;
- exception type;
- HRESULT in hexadecimal;
- a control-character-normalized, length-bounded message;
- at most one inner exception type, HRESULT, and bounded message.

Nonzero CSI return codes use `ETABS_API_CALL_FAILED` and include the operation
and exact return code. Diagnostics never include stack traces, arbitrary object
dumps, recursively nested exceptions, or unbounded messages.

## Authoritative Shutdown State Machine

Shutdown begins only after the in-memory managed identity, durable recovery
record, and authoritative process-handle identity agree. A mismatch returns
`ETABS_SHUTDOWN_IDENTITY_MISMATCH`, performs no COM exit or process termination,
and preserves recovery evidence.

With proven identity, shutdown executes:

1. Call `ApplicationExit(false)` and retain its return code or bounded exception.
2. Wait up to 10 seconds for the exact owned process handle to report exit.
3. If still alive, force terminate only that exact process handle.
4. Wait up to 10 seconds for confirmed exit.
5. Dispose COM/process resources and clear the recovery record only after exit
   is confirmed.

The terminal result separates CSI truth from process safety:

- `Succeeded`: `ApplicationExit(false)` returned zero and process exit was
  confirmed, either normally or after exact-handle force termination.
- `ETABS_APPLICATION_EXIT_FAILED`: the CSI call returned nonzero or threw, but
  cleanup continued. If process exit is confirmed, the record is cleared and
  the diagnostic states whether force termination was required. The protocol
  still reports failure because the CSI operation failed.
- `ETABS_SHUTDOWN_IDENTITY_MISMATCH`: ownership cannot be proven; no process is
  targeted and the record is retained.
- `ETABS_PROCESS_EXIT_UNCONFIRMED`: graceful and exact-handle force cleanup could
  not confirm exit; the record is retained for orphan recovery.

A process that is confirmed gone never leaves a recovery record, even when the
CSI exit call failed. A process whose exit is unconfirmed never loses its
recovery record.

## Idempotency and Orphan Recovery

The session caches its terminal shutdown result. Repeated shutdown calls do not
repeat `ApplicationExit`, force termination, record deletion, or disposal and
return the same meaningful terminal state. A never-started session with no
record returns success without touching ETABS.

Orphan recovery remains exact-identity-only. It terminates only a process whose
PID, UTC start time, and executable path all match the durable record. It clears
the record only after confirmed exit. Identity mismatch or unconfirmed exit
preserves the record and emits a typed/bounded diagnostic for later recovery.

## Serve Protocol Ordering

`ServeLoop` receives one idempotent asynchronous shutdown coordinator. On a
`shutdown` request, it awaits the coordinator's complete terminal result and
serializes that result afterward. It never writes an optimistic success response
and then depends on scope disposal for cleanup.

The coordinator submits the session shutdown state machine to the existing STA
worker and awaits it. The worker's serial queue places shutdown after any ETABS
work already accepted by that daemon and keeps COM cleanup on the same apartment
that created and used the managed application. Only after the session reaches a
terminal shutdown state does the coordinator dispose the worker. The
command-level `finally` path invokes the same coordinator for stdin EOF,
cancellation, or exceptions; idempotency avoids duplicate cleanup. A protocol
failure can therefore state both the CSI outcome and whether the ETABS process
is confirmed gone.

## Regression Matrix

ETABS-free tests cover:

1. Exclusive ownership is verified and the recovery record is written before
   exactly one `InitializeNewModel` call.
2. Initialization return zero marks the session ready; reuse does not initialize
   again.
3. Initialization nonzero and exception produce
   `ETABS_MODEL_INITIALIZATION_FAILED` with bounded call evidence.
4. Initialization failure clears the record only after confirmed exact-process
   exit and retains it when exit is unconfirmed.
5. `ApplicationExit(false)` return zero plus normal process exit succeeds and
   clears the record.
6. Nonzero/exceptional `ApplicationExit(false)` never reports success, even when
   exact-handle forced termination confirms zero process.
7. Return zero with a still-live process force terminates only the exact owned
   handle, confirms exit, and succeeds.
8. Unconfirmed forced termination fails with
   `ETABS_PROCESS_EXIT_UNCONFIRMED` and retains recovery evidence.
9. Identity mismatch never calls `ApplicationExit` or terminates a process and
   retains the record.
10. The record is never cleared while the verified process remains alive.
11. `shutdown` emits no response before coordinator completion and serializes
    the coordinator's success or typed failure exactly once.
12. Repeated shutdown is deterministic/idempotent and never saves.
13. Orphan recovery remains full-identity-only and retains the record on unsafe
    or unconfirmed cleanup.
14. OpenModel exception and return-code diagnostics are bounded and preserve the
    operation, exception type, HRESULT, message, one inner level, and API return
    code where applicable.

## Verification and Release Boundary

After focused RED/GREEN tests, run the complete ETABS-free solution tests,
forced restore and package-graph security checks, serialized solution build,
two deterministic Release publishes with the new exact commit build ID, binary
identity/Snappier checks, and executable-only serve handshake/shutdown smoke.

No live ETABS process is launched during implementation verification. The final
handoff provides the refreshed exact head, precise diff, regression matrix, and
the proposed supervised three-gate rerun protocol. CLI PR #15 remains draft and
unmerged until that later run is separately authorized and reviewed.
