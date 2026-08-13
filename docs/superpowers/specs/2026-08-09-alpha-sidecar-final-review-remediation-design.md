# Alpha Sidecar Final Review Remediation Design

**Date:** 2026-08-09

## Scope

Resolve the final two C# re-review findings without changing the persistent
protocol, launching ETABS, or editing Rust/frontend code:

1. Post-launch ambiguity cleanup must deterministically stop and dispose the
   exact process started by this launcher, even when COM exit fails or returns
   while that process remains alive.
2. Handshake metadata must be read from the assembly that defines
   `ServeHandshake`, not the host entry assembly.

## Owned-process cleanup

`ManagedEtabsLauncher.Launch` keeps its original `IOwnedEtabsProcess` reference
until post-launch exclusivity verification succeeds. Connector success no longer
clears the local reference before verification.

If verification succeeds, the launcher clears its local reference immediately
before returning because ownership has transferred to the managed application.
If verification fails, cleanup receives both the managed application and the
retained owned-process handle:

1. Attempt `ExitWithoutSaving` and log, but do not propagate, an exit failure.
2. Check the retained handle. If it still reports alive, call `Kill` immediately
   and wait up to the existing ten-second cleanup timeout.
3. Dispose the managed application, whose production implementation disposes the
   owned-process handle in `finally`.
4. Clear the launcher's local reference before rethrowing so the outer launch
   cleanup cannot kill or dispose the same handle twice.

The process-inspector observations remain classification evidence only. Cleanup
never calls `Terminate` or `WaitForExit` on observed foreign or unidentified
processes.

Tests cover both cleanup triggers: successful COM exit that leaves the retained
handle alive, and throwing COM exit. Each proves exactly one owned kill, wait,
and dispose, while the process inspector records no termination.

## Handshake metadata assembly

`ServeHandshake.Current` passes `typeof(ServeHandshake).Assembly` to the existing
metadata reader. `Assembly.GetEntryAssembly` is removed from the handshake path.
This prevents test runners, alternate hosts, and desktop launchers from
accidentally becoming the metadata source.

The test project explicitly injects `SidecarVersion=0.1.0` and
`SidecarBuildId=0.1.0+gtest` as assembly metadata. A production-path unit test
calls `ServeHandshake.Current` and asserts those exact values. That test would
fail if the implementation reverted to the test runner's entry assembly. The
missing-metadata test uses an unrelated framework assembly so fail-closed
coverage remains intact.

## Verification

- Focused launcher cleanup tests.
- Focused handshake tests.
- Full ETABS-free C# suite.
- Restore without `NU1903`.
- Serialized solution build.
- Expected Release failure without `SidecarBuildId`.
- Release single-file publish with an immutable build ID based on the final
  implementation HEAD.
- Handshake/shutdown-only smoke; no ETABS command.
- Clean diff and worktree review.

## Non-goals

- No live ETABS certification.
- No Rust or frontend changes.
- No protocol schema or capability changes.
- No push, PR, merge, or issue mutation.
