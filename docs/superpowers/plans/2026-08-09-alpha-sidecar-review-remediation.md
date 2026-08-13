# Alpha Sidecar Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close four reviewed safety gaps in the C# persistent sidecar contract without launching ETABS or changing Rust/frontend code.

**Architecture:** Promote the post-launch observation to a mandatory ownership check, feed fresh process observations through the COM-free cached-status path, separate semantic version from required build metadata, and derive advertised dispatcher capabilities from the actual handler registry. Each correction is protected by a focused ETABS-free regression and committed independently.

**Tech Stack:** C# 14, .NET 10, xUnit v3, Microsoft dependency injection, line-delimited JSON serve protocol.

---

### Task 1: Fail Closed on Post-Launch Ambiguity

**Files:**
- Modify: `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`

- [ ] **Step 1: Replace the advisory-success test with failing safety regressions**

Add tests equivalent to:

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public void PostLaunchAmbiguityCleansOnlyOwnedApplication(bool unidentified)
{
    var owned = new FakeOwnedProcess(Identity(42));
    var foreign = Identity(99);
    var second = unidentified
        ? new EtabsProcessObservation([owned.Identity], 1)
        : Observation(owned.Identity, foreign);
    var processes = new FakeProcesses([Observation(), second]);
    var connector = new FakeConnector(succeedOnAttempt: 1);
    var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

    var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

    Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
    Assert.Equal(1, connector.Managed!.ExitCount);
    Assert.Equal(1, connector.Managed.DisposeCount);
    Assert.Empty(processes.TerminatedPids);
}
```

Expose the created `FakeManaged` from `FakeConnector` and record
`ExitCount`/`DisposeCount` in the fake.

- [ ] **Step 2: Run the focused tests and confirm RED**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~PostLaunchAmbiguity --no-restore --tl:off -v minimal
```

Expected: FAIL because the launcher returns the managed session after logging the
second observation.

- [ ] **Step 3: Make the second observation mandatory**

After `TryConnect` returns a managed application, transfer ownership immediately,
then verify the observation:

```csharp
ownedProcess = null;
try
{
    VerifyPostLaunchOwnership(managed.Identity.Pid);
    return managed;
}
catch
{
    try { managed.ExitWithoutSaving(); }
    finally { managed.Dispose(); }
    throw;
}
```

Implement the verifier so only one identified owned PID and zero unidentified
processes pass. Observation errors and all mismatches throw
`EtabsLaunchException(ExternalOrAmbiguousInstance, ...)`. Do not call
`IProcessInspector.Terminate` from this path.

- [ ] **Step 4: Run focused and full tests**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~ManagedEtabsLauncherTests" --no-restore --tl:off -v minimal
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: all tests pass without ETABS.

- [ ] **Step 5: Commit**

```powershell
git add EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs
git commit -m "fix: reject post-launch etabs ambiguity"
```

### Task 2: Observe Active Status and Preserve Cached Failures

**Files:**
- Modify: `EtabExtension.CLI.Tests/ServeOperationDispatcherTests.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/CachedSessionStatus.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs`

- [ ] **Step 1: Add failing active-status tests**

Add tests that start a blocking operation and assert:

```csharp
var cached = new CachedSessionStatus();
cached.Update(Result.Ok(new GetStatusData
{
    IsRunning = true,
    Pid = 42,
    Ownership = EtabsInstanceOwnership.Managed,
    ObservedPids = [42]
}));
var processes = new FakeProcesses(Observation(Identity(42), Identity(99)));
var dispatcher = CreateDispatcher(manager, session, processes, cachedStatus: cached);

var result = Assert.IsType<Result<GetStatusData>>(
    await dispatcher.DispatchAsync("get-status", null, token));
Assert.Equal(EtabsInstanceOwnership.Ambiguous, result.Data!.Ownership);
Assert.Equal([42, 99], result.Data.ObservedPids);
Assert.Equal(0, session.GetOrStartCalls);
```

Add a second ambiguity test using `UnidentifiedCount=1`, and a cached-failure test:

```csharp
cached.Update(Result.Fail<GetStatusData>("cached status failed"));
var result = Assert.IsType<Result<GetStatusData>>(await dispatcher.DispatchAsync(...));
Assert.False(result.Success);
Assert.Equal("cached status failed", result.Error);
```

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~Get_status_uses_cached|FullyQualifiedName~Active_status" --no-restore --tl:off -v minimal
```

Expected: ownership remains Managed and cached failure becomes success.

- [ ] **Step 3: Pass observations into the cache and centralize decoration**

Change the contract to:

```csharp
public interface ICachedSessionStatus
{
    void Update(Result<GetStatusData> status);
    Result<GetStatusData> Read(
        IEtabsSession session,
        EtabsProcessObservation observation);
}
```

Add one shared decorator:

```csharp
internal static Result<GetStatusData> DecorateStatus(
    Result<GetStatusData> status,
    EtabsProcessObservation observation,
    int? managedPid)
```

It returns failures unchanged, derives ownership through
`EtabsOwnershipResolver`, promotes managed-plus-no-observation to `Ambiguous`,
sorts/distincts observed PIDs, and derives `IsRunning`/`Pid` from ownership.
`CachedSessionStatus.Read` uses cached data when present, creates only the absent
cache baseline, and never replaces a cached failure.

During an active operation, `ServeDispatcher` calls `ObserveEtabs` and passes the
observation into `Read`; it never calls `GetOrStart`. Both active and idle paths
use the shared decorator. An observation exception returns a failed
`Result<GetStatusData>`.

- [ ] **Step 4: Run focused and full tests**

Run the focused command from Step 2, then the full test project. Expected: all
tests pass and active status remains COM-free.

- [ ] **Step 5: Commit**

```powershell
git add EtabExtension.CLI.Tests/ServeOperationDispatcherTests.cs src/EtabExtension.CLI/Features/Serve/CachedSessionStatus.cs src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs
git commit -m "fix: observe ownership during active status"
```

### Task 3: Require Separate Semantic Version and Build Identity

**Files:**
- Modify: `src/EtabExtension.CLI/EtabExtension.CLI.csproj`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeLoop.cs`
- Modify: `EtabExtension.CLI.Tests/ServeLoopTests.cs`

- [ ] **Step 1: Add failing identity tests**

Inject a test handshake into `ServeLoop` and assert exact values:

```csharp
var identity = new ServeHandshake(
    "etab-cli-serve", 1, "0.1.0", "0.1.0+gtest",
    Environment.ProcessId, Path.GetFullPath(Environment.ProcessPath!),
    ["get-status", "shutdown"]);
await new ServeLoop(dispatcher, identity).RunAsync(reader, writer, token);
Assert.Equal("0.1.0", first.GetProperty("version").GetString());
Assert.Equal("0.1.0+gtest", first.GetProperty("buildId").GetString());
```

Add a metadata-reader test against an assembly with no sidecar metadata and
assert `InvalidOperationException` contains the missing metadata key.

- [ ] **Step 2: Run the handshake tests and confirm RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeLoopTests --no-restore --tl:off -v minimal
```

Expected: compile failure because the injected constructor/metadata reader do not
exist, and current production version is informational.

- [ ] **Step 3: Stamp and require metadata**

In the production project:

```xml
<Version>0.1.0</Version>
<SidecarVersion>0.1.0</SidecarVersion>
<SidecarBuildId Condition="'$(SidecarBuildId)' == '' and '$(Configuration)' == 'Debug'">0.1.0+gdev</SidecarBuildId>
<Target Name="RequireReleaseSidecarBuildId" BeforeTargets="GenerateAssemblyInfo"
        Condition="'$(Configuration)' == 'Release' and '$(SidecarBuildId)' == ''">
  <Error Text="Release builds require -p:SidecarBuildId=&lt;immutable-build-id&gt;." />
</Target>
<AssemblyMetadata Include="SidecarVersion" Value="$(SidecarVersion)" />
<AssemblyMetadata Include="SidecarBuildId" Value="$(SidecarBuildId)" />
```

Implement `ServeHandshake.FromAssembly(Assembly assembly, ..., capabilities)` to
require nonblank `SidecarVersion` and `SidecarBuildId`. `Current(capabilities)`
uses `typeof(ServeHandshake).Assembly` and process identity. There is no fallback.

Add an internal `ServeLoop(IServeDispatcher, ServeHandshake)` constructor for
tests; the public constructor uses `Current(...)`.

- [ ] **Step 4: Run focused and full tests**

Run the focused command from Step 2 and the full test project. Expected: semantic
version/build tests pass; Debug tests use explicit injected identity.

- [ ] **Step 5: Commit**

```powershell
git add src/EtabExtension.CLI/EtabExtension.CLI.csproj src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs src/EtabExtension.CLI/Features/Serve/ServeLoop.cs EtabExtension.CLI.Tests/ServeLoopTests.cs
git commit -m "fix: require sidecar build identity"
```

### Task 4: Derive Capabilities from the Handler Registry

**Files:**
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeLoop.cs`
- Modify: `EtabExtension.CLI.Tests/ServeLoopTests.cs`
- Modify: `EtabExtension.CLI.Tests/ServeOperationDispatcherTests.cs`

- [ ] **Step 1: Add the failing registry parity test**

Extend `IServeDispatcher` with `IReadOnlyCollection<string> Capabilities`. In the
handshake test, assert:

```csharp
var expected = dispatcher.Capabilities
    .Append(ServeLoop.ShutdownCommand)
    .Order(StringComparer.Ordinal)
    .ToArray();
Assert.Equal(expected, handshakeCapabilities);
```

Add a dispatcher test asserting its capabilities equal the exact current
persistent command set and that a random unsupported name is absent.

- [ ] **Step 2: Run focused tests and confirm RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter "FullyQualifiedName~ServeLoopTests|FullyQualifiedName~Dispatcher_capabilities" --no-restore --tl:off -v minimal
```

Expected: compile failure because dispatchers expose no registry-backed
capabilities.

- [ ] **Step 3: Replace the command switch with actual handler delegates**

Define:

```csharp
private readonly IReadOnlyDictionary<string,
    Func<JsonElement?, CancellationToken, Task<object>>> _handlers;

public IReadOnlyCollection<string> Capabilities => _handlers.Keys;

public Task<object> DispatchAsync(string command, JsonElement? request, CancellationToken ct) =>
    _handlers.TryGetValue(command, out var handler)
        ? handler(request, ct)
        : Task.FromResult<object>(
            Result.Fail($"Command not supported in serve mode yet: '{command}'"));
```

Initialize `_handlers` with one delegate for each currently supported dispatcher
command: `get-status`, operation start/status/events/cancel,
`open-model`, `analyze-and-extract`, `snapshot-export`, `close-model`,
`unlock-model`, results/material extraction, E2K generation, metadata,
`run-analysis`, and the four inspection commands. Move each existing switch body
unchanged into a named private async handler method. Use
`StringComparer.Ordinal` and expose keys sorted for deterministic handshakes.

`ServeLoop.ShutdownCommand` remains the sole loop-owned capability. Its public
constructor creates the handshake from dispatcher capabilities plus shutdown.
Remove the independent `ServeCapabilities.All` list.

- [ ] **Step 4: Run focused and full tests**

Run the focused command from Step 2 and the full test project. Expected: emitted
capabilities exactly equal installed handlers plus shutdown, and unknown commands
still return the stable unsupported response.

- [ ] **Step 5: Commit**

```powershell
git add src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs src/EtabExtension.CLI/Features/Serve/ServeDispatcher.cs src/EtabExtension.CLI/Features/Serve/ServeLoop.cs EtabExtension.CLI.Tests/ServeLoopTests.cs EtabExtension.CLI.Tests/ServeOperationDispatcherTests.cs
git commit -m "refactor: derive sidecar capabilities from handlers"
```

### Task 5: Final C# Verification

**Files:** all files changed in Tasks 1-4.

- [ ] **Step 1: Run restore and the full safe suite**

```powershell
dotnet restore EtabExtension.CLI.slnx --force-evaluate --tl:off -v minimal
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: restore has no NU1903 and every ETABS-free test passes.

- [ ] **Step 2: Run the serialized solution build**

```powershell
dotnet build-server shutdown
dotnet build EtabExtension.CLI.slnx --no-restore --tl:off -m:1 -v minimal
```

Expected: zero build errors.

- [ ] **Step 3: Publish with the final immutable candidate ID**

Set `CONTRACT_HEAD` to the current committed branch HEAD and run:

```powershell
dotnet publish src/EtabExtension.CLI/EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish/alpha-contract --no-restore --tl:off -v minimal -p:SidecarBuildId=0.1.0+g<CONTRACT_HEAD>
```

Expected: Release publish succeeds only with the explicit property.

- [ ] **Step 4: Run the handshake/shutdown-only smoke**

Feed `{"id":1,"command":"shutdown"}` to the published executable. Assert the
handshake has exact `version=0.1.0`, exact injected build ID, final executable
path, registry-derived capabilities plus shutdown, followed by correlated
success. Do not send any ETABS command.

- [ ] **Step 5: Self-review and hygiene**

```powershell
git diff --check f4f414f..HEAD
git status --short --branch
git log --oneline f4f414f..HEAD
```

Expected: clean diff/worktree, scoped commits only, and ignored publish outputs.
