# Serve Startup Refusal and Per-Request Exception Isolation Design

**Date:** 2026-08-13

**Status:** Implemented for TAD-19 co-review findings A and B

## Problem

The co-review of CLI PR #15 at head `575a4e2` accepted the readiness/shutdown
remediation and then identified two remaining offline defects. Both are direct
consequences of the shutdown work: recovery evidence is now correctly retained,
and daemon exit now really does terminate the owned ETABS process.

1. **Startup recovery refusal is protocol-silent.** `ServeCommand` runs orphan
   recovery before `ServeLoop` writes the handshake. When recovery fails closed
   the command threw and the process exited without writing any protocol frame.
   Because the record is retained on purpose, the next consumer attempt respawns
   into the same refusal, and the only visible symptom is an opaque process exit.
   The retained record — the sole proof of which ETABS process a previous daemon
   owned — then looks like something to delete to make startup work again.
2. **Per-request exception isolation regressed.** `ServeLoop` caught malformed
   request-line JSON only. Anything thrown out of `IServeDispatcher.DispatchAsync`
   — a missing `request` payload, a payload whose field types do not match, an
   unexpected handler or COM escape — escaped the loop, produced no correlated
   response for the offending request, and took the managed ETABS session down
   through authoritative cleanup. A single bad payload could end a live session.

## Scope

C# CLI persistent sidecar only. No EtabExtension Rust, desktop, installer, ETABS
behavior, or command-surface change; no live ETABS run. Serve command parity for
`generate-e2k-corpus`, `preflight-edit`, and `apply-edit` is explicitly out of
scope and tracked as EtabExtension.CLI #16.

## Startup contract

Serve startup now has exactly two terminal outcomes, and each writes exactly one
frame to stdout before anything else happens:

```
serve startup
    ├── READY     handshake: validated identity + capabilities, request loop runs
    └── REFUSED   typed recovery error, bounded terminal facts, remediation, no loop
```

A `startup` discriminator field carries `"ready"` or `"refused"`, so a consumer
never infers startup state from process exit alone and never has to parse the
other members to tell the two apart. The `ready` frame is the existing handshake
with that one field added; every other field is unchanged.

The `refused` frame carries the same build identity as the handshake plus:

| Field | Meaning |
| -- | -- |
| `errorCode` | stable recovery code (`ETABS_SHUTDOWN_IDENTITY_MISMATCH`, `ETABS_PROCESS_EXIT_UNCONFIRMED`, `ETABS_APPLICATION_EXIT_FAILED`) |
| `error` | bounded recovery diagnostic (2,048 UTF-16 code units, control characters normalized) |
| `state`, `processExitConfirmed`, `recordRetained`, `ownedPid` | terminal facts from the recovery result |
| `recordPath` | where the retained recovery record lives |
| `remediation` | fixed actionable text, including that the record must not be deleted to bypass the refusal |

Invariants:

- no `ready` handshake is emitted unless recovery actually succeeded;
- the refusal frame advertises **no** `capabilities` — nothing is served after it;
- no request is read; the process exits with code 1 after cleanup converges;
- the retained recovery record is never cleared to make startup succeed.

`ISessionRecordStore` gains a `FilePath` member so the refusal can name the
retained evidence instead of the consumer guessing at it.

### Consumer migration

EtabExtension `6772a9a` treats any startup line without an `id` as handshake or
debug output, so it degrades to its existing generic exit error. That is accepted
for the serialized migration. PR #324/#270 must parse this frame — after #216
provides the shared bounded frame reader — and surface the typed remediation
rather than an opaque respawn loop.

## Request isolation contract

`ServeLoop` wraps only the dispatcher call, not the response write:

- a handler failure becomes one bounded correlated failure response carrying the
  original request `id`, formatted through `EtabsApiDiagnosticFormatter`
  (`ETABS_INFRASTRUCTURE_OPERATION_FAILED; operation=IServeDispatcher.DispatchAsync;
  …; command=<name>`), and the loop continues with the next request;
- `OperationCanceledException` raised by the serve cancellation token is **not**
  contained — the daemon really is stopping, so it propagates and terminates
  through the same idempotent shutdown coordinator as every other exit path;
- an `OperationCanceledException` that is *not* caused by that token is an
  ordinary request failure and is reported as one;
- transport failures (handshake write, response write, reader) stay fatal and are
  never disguised as recoverable request errors;
- no raw or unbounded exception text reaches the protocol.

## Verification

ETABS-free tests cover, for startup: identity-mismatch, exit-unconfirmed and
application-exit-failed retained-record states each producing exactly one typed
refusal frame with no loop invocation and no capability advertisement; camelCase
terminal state serialization; bounded `error`/`recordPath`; one bounded stderr
line; and the successful-recovery path writing no refusal frame at all.

For isolation: missing payload, wrong payload type, and a throwing handler each
yield one correlated bounded failure followed by a successful next request;
oversized exception text is capped; and cancellation during dispatch still
terminates through the coordinator exactly once.

A machine-level smoke additionally proves the refusal end to end without ETABS: a
retained record whose PID is live but whose executable path does not match yields
one `refused` frame, exit code 1, the record still present, and the foreign
process still running — exact-identity recovery never terminates what it cannot
prove it owns.
