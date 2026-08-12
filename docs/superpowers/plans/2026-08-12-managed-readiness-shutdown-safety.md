# Managed ETABS Readiness and Shutdown Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a newly owned ETABS session API-ready before use and make every serve termination report truthfully only after exact-process cleanup reaches a terminal state.

**Architecture:** `EtabsSession` writes recovery identity before exact-once initialization, then delegates every failure or normal exit to one `ManagedEtabsShutdownMachine` that retains the authoritative process handle through bounded graceful/forced cleanup. `ExitWithoutSaving()` is the sole CSI exit request: the machine never calls EtabSharp `ETABSApplication.Dispose()`, releases only the `IOwnedEtabsProcess` handle after confirmed exit, and retains the wrapper/handle/record on unsafe terminal states. `ServeShutdownCoordinator` queues that same session shutdown on the existing STA worker, caches the result, disposes the worker afterward, and is awaited by every `ServeLoop` termination path before any explicit shutdown response is serialized.

**Tech Stack:** .NET 10, C# 14, System.Text.Json line protocol, EtabSharp/ETABSv1, xUnit, PowerShell release verification.

---

## File Map

- Create `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/EtabsApiDiagnosticFormatter.cs`: stable error codes, bounded exception/API-return formatting, and control normalization.
- Create `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsShutdown.cs`: terminal shutdown data and the sole exact-handle shutdown state machine.
- Modify `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs`: expose initialization, the sole explicit CSI exit request, authoritative process operations, and a process-handle-only release through a non-`IDisposable` `IManagedEtabsApplication`.
- Modify `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`: remove the duplicate managed-application declaration and make orphan cleanup exact-identity/confirmed-exit safe.
- Modify `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/EtabsSession.cs`: write record before exact-once initialization and route all cleanup through the shutdown machine.
- Modify `src/EtabExtension.CLI/Features/OpenModel/OpenModelService.cs`: report bounded operation-specific COM/API diagnostics.
- Create `src/EtabExtension.CLI/Features/Serve/ServeShutdownCoordinator.cs`: one cached async coordinator that queues session cleanup on the STA worker.
- Modify `src/EtabExtension.CLI/Features/Serve/ServeLoop.cs`: await the coordinator on explicit shutdown and in `finally` for every termination path.
- Modify `src/EtabExtension.CLI/Features/Serve/ServeCommand.cs`: resolve the coordinator, use an async DI scope, and remove optimistic post-loop session cleanup.
- Modify `src/EtabExtension.CLI/Features/Serve/ServeExtensions.cs`: register the shutdown machine/coordinator.
- Create `EtabExtension.CLI.Tests/EtabsApiDiagnosticFormatterTests.cs`: exact numeric-bound tests.
- Create `EtabExtension.CLI.Tests/OpenModelDiagnosticsTests.cs`: return-code and exception call-site tests without ETABS.
- Rewrite `EtabExtension.CLI.Tests/ManagedSessionTests.cs`: readiness ordering and full shutdown regression matrix.
- Modify `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`: adapt fakes and retain exact-owned cleanup assertions.
- Modify `EtabExtension.CLI.Tests/ServeLoopTests.cs`: explicit/EOF/cancellation/fatal ordering and result serialization.
- Create `EtabExtension.CLI.Tests/ServeShutdownCoordinatorTests.cs`: STA queue, idempotency, and terminal result tests.

### Task 1: Add numerically bounded ETABS diagnostics

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/EtabsApiDiagnosticFormatter.cs`
- Test: `EtabExtension.CLI.Tests/EtabsApiDiagnosticFormatterTests.cs`

- [ ] **Step 1: Write formatter RED tests**

Cover component and total bounds, fixed HRESULT text, control normalization, final-ellipsis truncation, one inner level, and API return codes. The intended public surface is:

```csharp
public static class EtabsApiErrorCodes
{
    public const string ComOperationFailed = "ETABS_COM_OPERATION_FAILED";
    public const string ApiCallFailed = "ETABS_API_CALL_FAILED";
}

public static class EtabsApiDiagnosticFormatter
{
    public const int OperationLimit = 128;
    public const int ExceptionTypeLimit = 256;
    public const int MessageLimit = 512;
    public const int TotalLimit = 2048;

    public static string ApiReturn(string operation, int returnCode);
    public static string Exception(string operation, Exception exception);
}
```

Test a `COMException` with HRESULT `unchecked((int)0x80004005)`, messages longer than 512, type/operation strings beyond their caps, CR/LF/tab/control characters, and a two-level inner chain. Assert `0x80004005`, no control characters, one inner only, each component cap, total `Length <= 2048`, and terminal `…` on truncation.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~EtabsApiDiagnosticFormatterTests --no-restore --tl:off
```

Expected: compile failure because the formatter does not exist.

- [ ] **Step 3: Implement the formatter minimally**

Normalize every `char.IsControl` character to a space, truncate each component to its own cap with a final `…`, render exception types from `GetType().FullName`, render HRESULT as `0x{HResult:X8}`, inspect `InnerException` once, compose stable labeled fields, and cap the final string with the same ellipsis rule. `ApiReturn` includes the exact integer return code.

- [ ] **Step 4: Run focused tests GREEN**

Run the Step 2 command. Expected: all formatter tests pass.

- [ ] **Step 5: Commit the diagnostic primitive**

```powershell
git add src/EtabExtension.CLI/Shared/Infrastructure/Etabs/EtabsApiDiagnosticFormatter.cs EtabExtension.CLI.Tests/EtabsApiDiagnosticFormatterTests.cs
git commit -m "add bounded etabs api diagnostics"
```

### Task 2: Define the authoritative owned-process shutdown state machine

**Files:**
- Create: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsShutdown.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`
- Rewrite tests: `EtabExtension.CLI.Tests/ManagedSessionTests.cs`
- Modify tests: `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`

- [ ] **Step 1: Write the shutdown regression matrix RED**

Use a fake managed application backed by a fake `IOwnedEtabsProcess`. Test:

```csharp
public enum ManagedEtabsShutdownState
{
    Succeeded,
    ApplicationExitFailed,
    IdentityMismatch,
    ProcessExitUnconfirmed
}

public sealed record ManagedEtabsShutdownData(
    ManagedEtabsShutdownState State,
    bool ProcessExitConfirmed,
    bool Forced,
    bool RecordRetained,
    int? ApplicationExitReturnCode,
    int? OwnedPid);

public sealed record ManagedEtabsShutdownResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    ManagedEtabsShutdownData Data);
```

Required cases:

- exit returns zero and first 10-second wait returns true: success, no kill, record cleared;
- exit returns nonzero and exact-process kill/second wait confirms exit: `ETABS_APPLICATION_EXIT_FAILED`, `Success=false`, `Forced=true`, confirmed, record cleared;
- exit throws and cleanup confirms exit: same typed failure with bounded exception evidence;
- exit returns zero but first wait false: exact handle killed, second wait true, success with `Forced=true`, record cleared;
- second wait false: `ETABS_PROCESS_EXIT_UNCONFIRMED`, record retained;
- record/handle identity or launch ID mismatch: `ETABS_SHUTDOWN_IDENTITY_MISMATCH`, no COM exit, no wait/kill, record retained;
- process already confirmed gone: no COM call/kill, record cleared;
- no call ever passes `FileSave=true`;
- EtabSharp `ETABSApplication.Dispose()` is never called because it would issue
  an implicit second `ApplicationExit(false)`;
- graceful, forced-confirmed, and API-failure/confirmed-exit cases make exactly
  one explicit exit request and exactly one process-handle-only release;
- identity mismatch makes zero explicit exits, kills, wrapper disposals, or
  handle releases and retains the record plus in-memory owned identity;
- exit unconfirmed makes exactly one explicit exit and one exact-handle kill,
  makes zero wrapper disposals/handle releases, and retains the record plus
  in-memory owned identity;
- repeated state-machine calls through the session do not repeat side effects.

- [ ] **Step 2: Run managed-session tests and verify RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~ManagedSessionTests|FullyQualifiedName~ManagedEtabsLauncherTests" --no-restore --tl:off
```

Expected: compile failures for the new terminal types and handle methods.

- [ ] **Step 3: Move managed application ownership operations beside the handle**

Define `IManagedEtabsApplication` in `ManagedEtabsLaunchInfrastructure.cs` with:

```csharp
ETABSApplication Application { get; }
ManagedProcessIdentity Identity { get; }
Guid ManagedLaunchRecordId { get; }
int InitializeNewModel();
int ExitWithoutSaving();
bool HasExited { get; }
bool WaitForExit(TimeSpan timeout);
void Kill();
void ReleaseOwnedProcessHandle();
```

`ManagedEtabsApplication` delegates `InitializeNewModel()` to
`Application.Model.ModelInfo.InitializeNewModel(eUnits.kip_in_F)`, delegates
`ExitWithoutSaving()` to `Application.Application.ApplicationExit(false)`, and
delegates process properties/actions to the retained `IOwnedEtabsProcess`.
`ReleaseOwnedProcessHandle()` calls only `IOwnedEtabsProcess.Dispose()` and never
calls `ETABSApplication.Dispose()`. Adapt launcher failure cleanup to consume the
returned exit code while still killing/waiting on the same retained handle; it
releases that handle only after exit is confirmed and retains it if exit remains
unconfirmed.

- [ ] **Step 4: Implement `ManagedEtabsShutdownMachine`**

Add stable codes:

```csharp
public const string ApplicationExitFailed = "ETABS_APPLICATION_EXIT_FAILED";
public const string IdentityMismatch = "ETABS_SHUTDOWN_IDENTITY_MISMATCH";
public const string ProcessExitUnconfirmed = "ETABS_PROCESS_EXIT_UNCONFIRMED";
```

Use `GracefulExitTimeout = TimeSpan.FromSeconds(10)` and
`ForcedExitTimeout = TimeSpan.FromSeconds(10)`. Validate the durable record
against full identity plus `ManagedLaunchRecordId` before any COM/process
action. Call exit, retain its nonzero/exception truth, wait, exact-handle kill if
needed, wait again, and decide the terminal data. If and only if
`ProcessExitConfirmed=true`, release the authoritative process handle exactly
once and clear a matching record. Do not use an unconditional `finally` release.
On identity mismatch or unconfirmed exit, retain the wrapper, handle, and record.
Return failure with populated terminal data rather than a data-less error.

- [ ] **Step 5: Strengthen orphan recovery**

Make `IOrphanSessionCleaner.Clean()` return a typed recovery result or throw a
stable typed exception. It may use PID lookup only to reacquire a process after
a daemon crash, and may terminate only after full PID/start-time/exe identity
match. Clear the record only if `WaitForExit` confirms exit. Preserve it on
identity mismatch or timeout. Add cases for already-gone, exact match/confirmed
exit, mismatch, and unconfirmed exit.

- [ ] **Step 6: Run focused tests GREEN**

Run Step 2. Expected: all managed-session/launcher tests pass with zero ETABS processes started.

- [ ] **Step 7: Commit the shutdown primitive**

```powershell
git add src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsShutdown.cs src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/ManagedEtabsLaunchInfrastructure.cs src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs EtabExtension.CLI.Tests/ManagedSessionTests.cs EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs
git commit -m "harden managed etabs process shutdown"
```

### Task 3: Add durable-record-first, exact-once API readiness

**Files:**
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/EtabsSession.cs`
- Modify tests: `EtabExtension.CLI.Tests/ManagedSessionTests.cs`

- [ ] **Step 1: Write readiness-order RED tests**

Instrument fake store/application event logs and assert exact order:

```text
launch -> exclusive ownership already proven by launcher -> record-write -> initialize
```

Test initialization zero returns the session, two `GetOrStartOwned()` calls
initialize once, nonzero/exception throws `EtabsLaunchException` with code
`ETABS_MODEL_INITIALIZATION_FAILED`, and cleanup then uses the Task 2 state
machine. Test record cleared only on confirmed exit and retained on unconfirmed
exit. After a failed first launch, do not silently launch another instance in
the same daemon.

- [ ] **Step 2: Run readiness tests and verify RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ManagedSessionTests --no-restore --tl:off
```

Expected: ordering/exact-once tests fail against current session behavior.

- [ ] **Step 3: Implement session readiness**

Add `ETABS_MODEL_INITIALIZATION_FAILED` to `EtabsLaunchErrorCodes`. In the first
launch path: call launcher; assign the owned handle for recovery; write
`ToRecord`; call `InitializeNewModel()` once; on zero mark a private `_ready`
flag and return. On nonzero or exception, capture the stable bounded diagnostic,
invoke the shared shutdown machine, cache its terminal cleanup result, and throw
the initialization exception with both API failure and terminal safety facts.
Null `_owned` only when the terminal cleanup reports confirmed process exit; on
identity mismatch or unconfirmed exit retain `_owned` so the authoritative
wrapper/handle remains in memory while the cached terminal result prevents
reuse. Reuse verifies identity and requires `_ready`; it never initializes
again. Change `Shutdown()` to return the cached/current
`ManagedEtabsShutdownResult`. `EtabsSession.Dispose()` only invokes `Shutdown()`
and never calls wrapper disposal or handle release itself.

- [ ] **Step 4: Run readiness tests GREEN**

Run Step 2. Expected: all readiness/order/failure cleanup tests pass.

- [ ] **Step 5: Commit readiness**

```powershell
git add src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/EtabsSession.cs EtabExtension.CLI.Tests/ManagedSessionTests.cs
git commit -m "initialize managed etabs sessions once"
```

### Task 4: Improve OpenModel diagnostic fidelity

**Files:**
- Modify: `src/EtabExtension.CLI/Features/OpenModel/OpenModelService.cs`
- Create: `EtabExtension.CLI.Tests/OpenModelDiagnosticsTests.cs`

- [ ] **Step 1: Extract and test the ETABS-free OpenModel operation core RED**

Introduce an internal delegate-driven helper so tests do not construct COM:

```csharp
internal static Result<OpenModelData> OpenOnAttachedModel(
    string filePath,
    bool save,
    Func<string?> getCurrentPath,
    Func<string, int> saveFile,
    Func<string, int> openFile)
```

Test operation names `cSapModel.GetModelFilename`, `cFile.Save`, and
`cFile.OpenFile`; exact nonzero return codes; bounded COM exception details; and
that `save=false` never invokes save.

- [ ] **Step 2: Run OpenModel diagnostics and verify RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~OpenModelDiagnosticsTests --no-restore --tl:off
```

Expected: compile failure because the helper does not exist.

- [ ] **Step 3: Implement the helper and delegate from `OpenModelOnAppAsync`**

Set the active operation immediately before each delegate call. On nonzero
return use `ApiReturn`; on exception use `Exception`. Preserve existing success
data and no-save behavior. Do not include stack traces or raw objects.

- [ ] **Step 4: Run focused tests GREEN and commit**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~OpenModelDiagnosticsTests|FullyQualifiedName~EtabsApiDiagnosticFormatterTests" --no-restore --tl:off
git add src/EtabExtension.CLI/Features/OpenModel/OpenModelService.cs EtabExtension.CLI.Tests/OpenModelDiagnosticsTests.cs
git commit -m "improve open model api diagnostics"
```

### Task 5: Add one idempotent STA shutdown coordinator

**Files:**
- Create: `src/EtabExtension.CLI/Features/Serve/ServeShutdownCoordinator.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeExtensions.cs`
- Create: `EtabExtension.CLI.Tests/ServeShutdownCoordinatorTests.cs`

- [ ] **Step 1: Write coordinator RED tests**

Target:

```csharp
public interface IServeShutdownCoordinator : IAsyncDisposable
{
    Task<Result<ManagedEtabsShutdownData>> ShutdownAsync();
}
```

Tests use a fake `IStaExecutionWorker` and fake session to prove session shutdown
is submitted exactly once to the worker, concurrent/repeated calls await the
same cached task/result, the worker is disposed only after session cleanup
returns, failures retain populated terminal data, and `DisposeAsync` converges
on `ShutdownAsync`.

- [ ] **Step 2: Run coordinator tests and verify RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeShutdownCoordinatorTests --no-restore --tl:off
```

- [ ] **Step 3: Implement/register coordinator**

Use a lock-protected cached `Task<Result<ManagedEtabsShutdownData>>`. Queue
`session.Shutdown()` via `IStaExecutionWorker.ExecuteAsync`, await it, then
dispose the operation manager/worker exactly once in `finally`. Cancellation
must not cancel safety cleanup after it starts. Register one scoped coordinator.

- [ ] **Step 4: Run focused tests GREEN and commit**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeShutdownCoordinatorTests --no-restore --tl:off
git add src/EtabExtension.CLI/Features/Serve/ServeShutdownCoordinator.cs src/EtabExtension.CLI/Features/Serve/ServeExtensions.cs EtabExtension.CLI.Tests/ServeShutdownCoordinatorTests.cs
git commit -m "coordinate serve shutdown on sta worker"
```

### Task 6: Route every serve termination path through the coordinator

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeLoop.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeCommand.cs`
- Modify: `EtabExtension.CLI.Tests/ServeLoopTests.cs`

- [ ] **Step 1: Write protocol-order RED tests**

Use a manually controlled coordinator task. Assert explicit `shutdown` produces
no correlated response before that task completes; afterward it serializes the
coordinator's exact success/failure plus terminal `data`, then reads no further
request. Separately prove stdin EOF/client disconnect, cancellation, normal
async disposal, and a fatal reader exception each call the same coordinator.
Assert explicit shutdown plus loop `finally` still calls underlying cleanup once.

- [ ] **Step 2: Run ServeLoop tests and verify RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeLoopTests --no-restore --tl:off
```

- [ ] **Step 3: Implement convergence and command lifetime**

Inject `IServeShutdownCoordinator` into `ServeLoop`. For explicit shutdown,
await it, convert its terminal result into `Result<ManagedEtabsShutdownData>`,
write once, and return. Wrap the whole loop in `try/finally` and await the same
coordinator in `finally`; log cleanup failure for paths with no writable client.
In `ServeCommand`, use `await using var scope = services.CreateAsyncScope()`,
resolve the coordinator, pass it to the loop, and invoke that same coordinator
in command `finally`. Remove direct optimistic `session.Shutdown()` and direct
worker disposal. Scope disposal is idempotent through coordinator
`IAsyncDisposable`.

- [ ] **Step 4: Run focused tests GREEN and commit**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~ServeLoopTests|FullyQualifiedName~ServeShutdownCoordinatorTests" --no-restore --tl:off
git add src/EtabExtension.CLI/Features/Serve/ServeLoop.cs src/EtabExtension.CLI/Features/Serve/ServeCommand.cs EtabExtension.CLI.Tests/ServeLoopTests.cs
git commit -m "acknowledge shutdown after process cleanup"
```

### Task 7: Run complete ETABS-free correctness and security gates

**Files:** none unless a new regression reveals an in-scope defect.

- [ ] **Step 1: Assert no live ETABS process before gates**

```powershell
@(Get-Process ETABS -ErrorAction SilentlyContinue).Count
```

Expected: `0`. Do not start ETABS during any gate.

- [ ] **Step 2: Forced restore and package graphs**

```powershell
dotnet restore EtabExtension.CLI.slnx --force-evaluate --tl:off -v minimal
dotnet list src/EtabExtension.CLI/EtabExtension.CLI.csproj package --include-transitive
dotnet list EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj package --include-transitive
dotnet list EtabExtension.CLI.VisualTest/EtabExtension.CLI.VisualTest.csproj package --include-transitive
```

Expected: no NU1903; all three roots resolve direct Snappier `1.3.1`, never `1.3.0`; Parquet.Net remains `5.5.0`.

- [ ] **Step 3: Full ETABS-free tests**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: all tests pass, zero failed/skipped, and ETABS process count remains zero.

- [ ] **Step 4: Serialized solution build**

```powershell
dotnet build-server shutdown
dotnet build EtabExtension.CLI.slnx --no-restore --tl:off -v minimal -m:1
```

Expected: zero errors. Record analyzer warnings separately if VisualTest retains baseline warnings.

- [ ] **Step 5: Diff hygiene and review**

```powershell
git diff --check 6b0e90652ddc9f83634c0b3e39065ecaa75dbbc7...HEAD
git status --short --branch
git diff --stat 6b0e90652ddc9f83634c0b3e39065ecaa75dbbc7...HEAD
```

Expected: clean worktree and only CLI lifecycle/tests/docs changes.

### Task 8: Produce deterministic final-head artifacts without live ETABS

**Files:** ignored `artifacts/` only.

- [ ] **Step 1: Capture the final exact head and build ID**

```powershell
$head = (git rev-parse HEAD).Trim()
$buildId = "0.1.0+g$head"
```

- [ ] **Step 2: Publish twice from clean build state**

For each output `artifacts/publish/readiness-a-$($head.Substring(0,8))` and
`readiness-b-$($head.Substring(0,8))`, run `dotnet clean`,
`dotnet build-server shutdown`, then:

```powershell
dotnet publish src/EtabExtension.CLI/EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output <output> --tl:off -v minimal -p:SidecarBuildId=$buildId
```

Expected: both publish commands pass and their `etab-cli.exe` SHA-256 values are identical.

- [ ] **Step 3: Inspect dependency and embedded security identity**

Assert Release deps/assets and binary scanning contain `Snappier/1.3.1` and no
`Snappier/1.3.0`. Record executable length, ProductVersion, FileVersion, SHA-256,
and embedded exact build ID. Confirm no tracked `ETABSv1.dll` or executable.

- [ ] **Step 4: Run executable-only handshake/shutdown smoke**

Copy only `etab-cli.exe` to an isolated smoke directory. Set
`ETABS_INSTALL_DIR` to a deliberately nonexistent directory, start `serve`,
validate exact protocol/version/build/PID/exe path/capabilities, send only
`shutdown`, and require a successful terminal response and exit code zero.
Confirm no sibling `ETABSv1.dll`, no ETABS process before/after, and no managed
session record. This smoke must not request any ETABS assembly or launch ETABS.

### Task 9: Lead review and handback (no live rerun)

**Files:** no code changes unless review finds a specification defect.

- [ ] **Step 1: Review the exact implementation diff against all 20 spec matrix items**

Check handle lifetime, record ordering, return-code truth, terminal data on
failure, forced flag, idempotency, zero EtabSharp-wrapper disposal, exactly one
explicit CSI exit, confirmed-exit-only process-handle release, unsafe-state
wrapper retention, STA ordering, EOF/cancellation/fatal cleanup, orphan exact
identity, and numeric diagnostics.

- [ ] **Step 2: Prepare the proposed supervised three-gate protocol**

Protocol only; do not execute it:

1. exact executable-only sidecar handshake/start timing;
2. one managed ETABS launch, full ownership/session record, API-readiness
   evidence, and timing;
3. sanctioned `sample_v2.EDB` open and timing;
4. hard stop without snapshot/export/analyze/calc/report;
5. shutdown response only after verified zero ETABS/sidecar processes, with
   record and model-hash evidence.

- [ ] **Step 3: After lead approval, push and report**

Push the existing `codex/alpha-sidecar-contract` branch normally without force.
Keep PR #15 open/draft. Add matching exact-head comments to CLI PR #15 and
Linear TAD-19 containing the precise diff, regression matrix, tests/builds,
deterministic artifact SHA, remaining live-only risk, and explicit statement
that no live rerun occurred. Do not modify EtabExtension, #323, #216, or #324.
