# Alpha Sidecar Review Remediation Design

## Scope

This addendum closes four review findings in the C# sidecar contract without
changing Rust, frontend, or live ETABS behavior. It preserves the Snappier
remediation and the approved Alpha protocol-safety design.

## Post-Launch Ownership Verification

The pre-launch observation remains a fail-closed guard. After COM ownership
transfers to the managed application, a second observation becomes mandatory:
success requires exactly one identified ETABS process, its PID must equal the
owned PID, and no unidentified process may exist.

A foreign PID, an unidentified process, a missing owned PID, or an observation
error fails with `ETABS_EXTERNAL_OR_AMBIGUOUS_INSTANCE`. Failure cleanup calls
`ExitWithoutSaving` and `Dispose` only on the managed application. It never
terminates a PID obtained from the observation, so an external ETABS process is
never targeted.

## Active-Operation Status

`get-status` during an active operation remains COM-free but performs a fresh
process observation. `ICachedSessionStatus.Read` receives that observation and
uses the same ownership decoration as the idle path. This exposes foreign or
ambiguous process state while the operation is active instead of assuming the
managed PID is healthy.

If the last cached status is a failure, the cache returns that failure unchanged
rather than replacing it with a successful synthetic status. An observation
failure also fails closed.

## Handshake Build Identity

The protocol assembly carries two separate required metadata values:

- `SidecarVersion` is the semantic product version, `0.1.0`.
- `SidecarBuildId` is the immutable build identity injected by the build.

Handshake construction reads the assembly containing `ServeHandshake`, not the
entry assembly and not `AssemblyInformationalVersion`. Missing or blank metadata
throws an actionable error; there is no runtime fallback. Tests pass an explicit
test handshake to `ServeLoop`. Debug builds use a visibly named development ID.
Release builds fail unless `SidecarBuildId` is supplied explicitly. The final
Release smoke injects an ID tied to the contract branch commit used for publish.

## Authoritative Capabilities

`ServeDispatcher` owns a dictionary from command names to the actual handler
delegates. `IServeDispatcher.Capabilities` is derived from those dictionary keys.
`ServeLoop` adds only `shutdown`, which it implements directly. The emitted
handshake therefore cannot advertise a dispatcher command without an installed
handler, and a newly registered handler is advertised automatically.

## Verification

Focused ETABS-free tests prove:

1. Post-launch owned-plus-foreign and unidentified observations fail, clean up
   the owned application once, and never terminate a foreign PID.
2. Active-operation status observes processes without COM and preserves cached
   failures.
3. Handshake version is exactly `0.1.0`, build identity is injected, and missing
   metadata throws.
4. The emitted capability set equals the dispatcher registry plus `shutdown`.

After focused tests, run the full C# suite, shut down build servers, build the
solution serially, publish Release with an explicit immutable build ID, and run
only the daemon handshake/shutdown smoke. Do not launch ETABS.
