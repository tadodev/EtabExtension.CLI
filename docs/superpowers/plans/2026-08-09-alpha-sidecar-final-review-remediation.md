# Alpha Sidecar Final Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ambiguity cleanup deterministically stop only the launcher-owned ETABS process and make the handshake read immutable metadata from its defining assembly.

**Architecture:** Retain the original `IOwnedEtabsProcess` through post-launch verification, then use that exact handle for cleanup before disposing through the managed wrapper. Read handshake metadata from `typeof(ServeHandshake).Assembly`; inject explicit metadata into the test assembly so `Current` is exercised directly.

**Tech Stack:** C# 14, .NET 10, xUnit v3, MSBuild assembly metadata, line-delimited JSON serve protocol.

---

## File map

- `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`:
  ownership transfer and launcher-owned process cleanup.
- `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`: ETABS-free cleanup
  behavior and foreign-process safety tests.
- `src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs`: production metadata
  assembly selection.
- `EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj`: explicit metadata for
  the assembly that contains `ServeHandshake` in tests.
- `EtabExtension.CLI.Tests/ServeLoopTests.cs`: direct `Current` and fail-closed
  metadata tests.

### Task 1: Retain and deterministically clean the owned process

**Files:**
- Modify: `EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs`
- Modify: `src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs`

- [ ] **Step 1: Write failing owned-cleanup tests**

Update `PostLaunchAmbiguityCleansOnlyOwnedApplication` so the alive owned handle
must be killed and waited after COM exit:

```csharp
Assert.Equal(1, connector.Managed!.ExitCount);
Assert.Equal(1, owned.KillCount);
Assert.Equal(1, owned.WaitForExitCount);
Assert.Equal(1, connector.Managed.DisposeCount);
Assert.Equal(1, owned.DisposeCount);
Assert.Empty(processes.TerminatedPids);
```

Add a throwing-exit test:

```csharp
[Fact]
public void PostLaunchAmbiguityKillsOwnedProcessWhenComExitThrows()
{
    var owned = new FakeOwnedProcess(Identity(42));
    var processes = new FakeProcesses(
        [Observation(), Observation(owned.Identity, Identity(99))]);
    var connector = new FakeConnector(succeedOnAttempt: 1, throwOnExit: true);
    var launcher = CreateLauncher(owned, processes, connector, new StringWriter());

    var error = Assert.Throws<EtabsLaunchException>(() => launcher.Launch());

    Assert.Equal(EtabsLaunchErrorCodes.ExternalOrAmbiguousInstance, error.Code);
    Assert.Equal(1, connector.Managed!.ExitCount);
    Assert.Equal(1, owned.KillCount);
    Assert.Equal(1, owned.WaitForExitCount);
    Assert.Equal(1, connector.Managed.DisposeCount);
    Assert.Equal(1, owned.DisposeCount);
    Assert.Empty(processes.TerminatedPids);
    Assert.DoesNotContain(99, processes.TerminatedPids);
}
```

Extend the fakes without changing production abstractions:

```csharp
private sealed class FakeConnector(
    int? succeedOnAttempt,
    bool throwOnExit = false) : IManagedEtabsConnector
{
    public List<int> RequestedPids { get; } = [];
    public FakeManaged? Managed { get; private set; }

    public IManagedEtabsApplication? TryConnect(
        IOwnedEtabsProcess process,
        Guid launchRecordId,
        out string? error)
    {
        RequestedPids.Add(process.Identity.Pid);
        if (succeedOnAttempt == RequestedPids.Count)
        {
            error = null;
            Managed = new FakeManaged(process, launchRecordId, throwOnExit);
            return Managed;
        }

        error = "COM server not ready";
        return null;
    }
}

private sealed class FakeManaged(
    IOwnedEtabsProcess process,
    Guid launchRecordId,
    bool throwOnExit) : IManagedEtabsApplication
{
    public ETABSApplication Application =>
        throw new InvalidOperationException("Fake must not expose COM");
    public ManagedProcessIdentity Identity => process.Identity;
    public Guid ManagedLaunchRecordId { get; } = launchRecordId;
    public int ExitCount { get; private set; }
    public int DisposeCount { get; private set; }

    public void ExitWithoutSaving()
    {
        ExitCount++;
        if (throwOnExit)
        {
            throw new InvalidOperationException("COM exit failed");
        }
    }

    public void Dispose()
    {
        DisposeCount++;
        process.Dispose();
    }
}
```

- [ ] **Step 2: Run the focused tests and record RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ManagedEtabsLauncherTests --no-restore --tl:off -v minimal
```

Expected: ambiguity cases fail because the current managed cleanup does not
kill or wait through the retained owned handle.

- [ ] **Step 3: Retain the handle until verification completes**

Replace the connector-success ownership transfer with:

```csharp
var managed = _connector.TryConnect(ownedProcess, launchRecordId, out lastError);
if (managed is not null)
{
    try
    {
        VerifyPostLaunchOwnership(managed.Identity.Pid);
        ownedProcess = null; // ownership transferred after verification
        return managed;
    }
    catch
    {
        CleanUpManagedApplication(managed, ownedProcess);
        ownedProcess = null; // managed cleanup disposed the transferred handle
        throw;
    }
}
```

Change managed cleanup to use the retained handle:

```csharp
private void CleanUpManagedApplication(
    IManagedEtabsApplication managed,
    IOwnedEtabsProcess ownedProcess)
{
    try
    {
        managed.ExitWithoutSaving();
    }
    catch (Exception ex)
    {
        _diagnostics.WriteLine(
            $"⚠ Could not exit managed ETABS PID {managed.Identity.Pid} after ownership failure: {ex.Message}");
    }

    StopOwnedProcess(ownedProcess, "ownership failure");

    try
    {
        managed.Dispose();
    }
    catch (Exception ex)
    {
        _diagnostics.WriteLine(
            $"⚠ Could not dispose managed ETABS PID {managed.Identity.Pid} after ownership failure: {ex.Message}");
    }
}
```

Extract the kill/wait portion of `CleanUpOwnedProcess` so both failure paths use
the same exact owned handle:

```csharp
private void StopOwnedProcess(IOwnedEtabsProcess ownedProcess, string context)
{
    try
    {
        if (!ownedProcess.HasExited)
        {
            ownedProcess.Kill();
            if (!ownedProcess.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                _diagnostics.WriteLine(
                    $"⚠ Timed out waiting for owned ETABS PID {ownedProcess.Identity.Pid} to exit after {context}.");
            }
        }
    }
    catch (Exception ex)
    {
        _diagnostics.WriteLine(
            $"⚠ Could not stop owned ETABS PID {ownedProcess.Identity.Pid} after {context}: {ex.Message}");
    }
}

private void CleanUpOwnedProcess(IOwnedEtabsProcess ownedProcess)
{
    try
    {
        StopOwnedProcess(ownedProcess, "launch failure");
    }
    finally
    {
        ownedProcess.Dispose();
    }
}
```

- [ ] **Step 4: Run focused and full tests**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ManagedEtabsLauncherTests --no-restore --tl:off -v minimal
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: all launcher tests pass; the full suite passes with at least 72 tests.

- [ ] **Step 5: Commit**

```powershell
git add EtabExtension.CLI.Tests/ManagedEtabsLauncherTests.cs src/EtabExtension.CLI/Shared/Infrastructure/Etabs/Session/SessionIdentity.cs
git commit -m "fix: stop owned process after ambiguous launch"
```

### Task 2: Read handshake metadata from the protocol assembly

**Files:**
- Modify: `EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj`
- Modify: `EtabExtension.CLI.Tests/ServeLoopTests.cs`
- Modify: `src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs`

- [ ] **Step 1: Inject explicit test-assembly metadata**

Add this item group to the test project:

```xml
<ItemGroup>
  <AssemblyMetadata Include="SidecarVersion" Value="0.1.0" />
  <AssemblyMetadata Include="SidecarBuildId" Value="0.1.0+gtest" />
</ItemGroup>
```

- [ ] **Step 2: Write the production-path and missing-metadata tests**

Add:

```csharp
[Fact]
public void Current_reads_metadata_from_the_protocol_assembly()
{
    var handshake = ServeHandshake.Current(["shutdown"]);

    Assert.Equal("0.1.0", handshake.Version);
    Assert.Equal("0.1.0+gtest", handshake.BuildId);
    Assert.Equal(["shutdown"], handshake.Capabilities);
}
```

Change the existing missing-metadata test to pass `typeof(string).Assembly` to
`FromAssembly`, retaining the exact `SidecarVersion` failure assertion.

- [ ] **Step 3: Run the focused tests and record RED**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeLoopTests --no-restore --tl:off -v minimal
```

Expected: `Current_reads_metadata_from_the_protocol_assembly` fails because the
test runner entry assembly does not carry the injected protocol metadata.

- [ ] **Step 4: Select the defining assembly**

Replace the entry-assembly lookup in `ServeHandshake.Current`:

```csharp
var assembly = typeof(ServeHandshake).Assembly;
```

Keep `FromAssembly`, the required metadata checks, PID, executable path, and
capabilities unchanged.

- [ ] **Step 5: Run focused and full tests**

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --filter FullyQualifiedName~ServeLoopTests --no-restore --tl:off -v minimal
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: all serve-loop tests pass; the full suite passes with at least 73
tests.

- [ ] **Step 6: Commit**

```powershell
git add EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj EtabExtension.CLI.Tests/ServeLoopTests.cs src/EtabExtension.CLI/Features/Serve/ServeProtocol.cs
git commit -m "fix: read sidecar metadata from protocol assembly"
```

### Task 3: Final release verification

**Files:** all files changed in Tasks 1-2.

- [ ] **Step 1: Restore and run the full safe suite**

```powershell
dotnet restore EtabExtension.CLI.slnx --force-evaluate --tl:off -v minimal
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --tl:off -v minimal
```

Expected: no `NU1903`; at least 73 ETABS-free tests pass.

- [ ] **Step 2: Run the serialized solution build**

```powershell
dotnet build-server shutdown
dotnet build EtabExtension.CLI.slnx --no-restore --tl:off -m:1 -v minimal
```

Expected: zero errors. Existing analyzer warnings are reported separately.

- [ ] **Step 3: Prove Release metadata fails closed**

```powershell
dotnet publish src/EtabExtension.CLI/EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish/alpha-contract-missing-id --no-restore --tl:off -v minimal
```

Expected: exit 1 with
`Release builds require -p:SidecarBuildId=<immutable-build-id>.`

- [ ] **Step 4: Publish with the immutable implementation HEAD**

```powershell
$contractHead = git rev-parse HEAD
$sidecarBuildId = "0.1.0+g$contractHead"
dotnet publish src/EtabExtension.CLI/EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish/alpha-contract --no-restore --tl:off -v minimal "-p:SidecarBuildId=$sidecarBuildId"
```

Expected: publish succeeds and produces `etab-cli.exe` plus `ETABSv1.dll`.

- [ ] **Step 5: Run the shutdown-only smoke**

Create ignored `artifacts/publish/alpha-contract/shutdown.json` containing
exactly:

```json
{"id":1,"command":"shutdown"}
```

Run:

```powershell
cmd /c "artifacts\publish\alpha-contract\etab-cli.exe serve < artifacts\publish\alpha-contract\shutdown.json"
```

Expected: exact `version=0.1.0`, exact HEAD-based `buildId`, registry-derived
capabilities plus `shutdown`, followed by `success=true` and `id=1`. Delete the
temporary input with `apply_patch`. Do not send any ETABS command.

- [ ] **Step 6: Self-review and hygiene**

```powershell
git diff --check 6ebc59f..HEAD
git status --short --branch
git log --oneline 6ebc59f..HEAD
```

Expected: clean worktree and only the design, plan, two implementation commits.
