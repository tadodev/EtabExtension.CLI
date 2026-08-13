# Raw CSI Managed Startup Design (Defect C)

**Date:** 2026-08-13

**Status:** Implemented for TAD-19 Defect C

## Problem

The supervised proof at `2454f4b` started one ETABS process and owned it correctly, then
failed at the readiness boundary: `cSapModel.InitializeNewModel` threw a
`NullReferenceException` and `cOAPI.ApplicationExit(false)` returned `-1` on the same
freshly attached object. Cleanup was authoritative and left zero orphans, so the failure
was functional, not a safety failure.

The cause was the startup path itself. It used `Process.Start(ETABS.exe)` followed by
`ETABSWrapper.ConnectToProcess(pid)`, which attaches through
`cHelper.GetObjectProcess(ProgID, pid)` and **never calls `ApplicationStart()`**. The
object it returned exposed a `cSapModel` that was not usable. The same path also retried
attachment on a clock and hid the window before anything was proven ready.

## Cardex contract (ETABS 23.3)

Corpus `etabs-api-23.3`, `source_docs_build` 23.3.0.4545, `corpus_sha256`
`d051f991…`. Recorded previously and unchanged; one new fact was queried for this change.

| Symbol | Contract | Use here |
| -- | -- | -- |
| `cHelper.CreateObject(string fullPath) → cOAPI` | "Starts the program at the given path"; returns "nothing" on failure | Starts ETABS; a null return is a typed `ETABS_API_OBJECT_CREATION_FAILED` |
| `cOAPI.ApplicationStart() → int` | zero on success; no-arg in 23.3 | Called exactly once, return required to be 0 |
| `cOAPI.SapModel → cSapModel` | reference to the model | Required present before ownership is claimed |
| `cSapModel.InitializeNewModel(eUnits = kip_in_F) → int` | zero on success | Exactly once, return required to be 0 |
| `cOAPI.ApplicationExit(bool) → int` | zero on success; drop the `cSapModel` reference afterwards | Unchanged authoritative exit; references released after confirmed exit |
| `cHelper.GetOAPIVersionNumber() → Double` | "The API version" | **New fact.** Wrap metadata only |

## Sequence

```
preflight census (zero ETABS, zero unidentified)
        ↓
cHelper.CreateObject(exact ETABS.exe)        ← starts the program
        ↓
cOAPI.ApplicationStart() == 0                ← exactly once
        ↓
cOAPI.SapModel present
        ↓
exact OS census: exactly one identified process, zero unidentified
        ↓
authoritative handle opened by pid + startUtc + exePath
        ↓  (launcher returns; session continues)
recovery record written
        ↓
cSapModel.InitializeNewModel() == 0          ← exactly once
        ↓
ETABSWrapper.WrapExisting(same cOAPI, …)     ← no create, start, attach or ROT
        ↓
Managed / API-ready
```

Preflight proves zero ETABS processes, so exactly one identified process after
`CreateObject` is ours by construction. That is the entire ownership claim — no pid
guessing, and no adoption of a process we cannot account for.

## What is gone

`IEtabsProcessStarter`, `WindowsEtabsProcessStarter`, `IManagedEtabsConnector`,
`EtabSharpManagedEtabsConnector`, `IEtabsLaunchClock` and `SystemEtabsLaunchClock` are
deleted, not merely unused: `Process.Start`, `ConnectToProcess`, the ROT fallback inside
EtabSharp's attach path, the attach-retry clock and the early `Hide()` are no longer
expressible from managed startup. A test asserts those types are absent from the assembly,
and another pins the whole member list of the raw boundary so a visibility or attach member
cannot be added quietly.

## Failure handling

Every failure after `CreateObject` may have left a process behind, so each one requests the
raw `ApplicationExit(false)` and then resolves the process:

- an authoritative handle exists → stop it through that handle and dispose it;
- no handle, exactly one identified survivor and zero unidentified → terminate by exact
  identity;
- anything else → **terminate nothing** and report that ownership could not be proven.

Initialization or wrap failure returns the session to the existing shutdown machine, which
is unchanged: checked `ApplicationExit(false)`, bounded 10 s graceful wait, exact-owned
force termination only if needed, second 10 s confirmation, record cleared only after
confirmed exit.

## Dispose ordering

EtabSharp `0.3.6-beta` adds `ETABSWrapper.WrapExisting` and its `Dispose()` is passive COM
reference cleanup. The session therefore releases references **after** the authoritative
exit and confirmed process exit — `ApplicationExit(false)` → wait/force/confirm → clear
record → release process handle → release API references — never as a substitute for the
exit, and never before it.

## API version metadata

`majorVersion` and `fullVersion` come from the owned executable's file version, read
without COM. `apiVersion` comes from `cHelper.GetOAPIVersionNumber()`; a version that
cannot be read is reported as `0` rather than failing a lifecycle that is otherwise proven,
matching the wrap contract's acceptance of `apiVersion == 0`.
