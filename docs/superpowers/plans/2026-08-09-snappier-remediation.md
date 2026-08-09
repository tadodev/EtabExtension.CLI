# Snappier Dependency Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove vulnerable `Snappier` 1.3.0 from every EtabExtension.CLI restore and self-contained publish while preserving the existing Parquet.Net behavior.

**Architecture:** Keep `Parquet.Net` 5.5.0 and apply an explicit dependency override at all three restore roots. Central package management owns the safe version, while the production, test, and visual-test projects declare direct references so no solution-wide transitive-pinning behavior changes.

**Tech Stack:** .NET 10, Central Package Management, Parquet.Net 5.5.0, Snappier 1.3.1, xUnit v3

---

### Task 1: Reproduce the security failure and pin the patched dependency

**Files:**
- Modify: `Directory.Packages.props:8`
- Modify: `src/EtabExtension.CLI/EtabExtension.CLI.csproj:57-61`
- Modify: `EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj:16-23`
- Modify: `EtabExtension.CLI.VisualTest/EtabExtension.CLI.VisualTest.csproj:14-21`

- [ ] **Step 1: Reproduce the failing restore before editing**

Run:

```powershell
dotnet restore EtabExtension.CLI.slnx --force-evaluate
```

Expected: FAIL because warnings are errors, with `NU1903` naming `Snappier`
1.3.0 and `GHSA-pggp-6c3x-2xmx`. Preserve the exact failure line in the worker
report.

- [ ] **Step 2: Add the centrally managed patched version**

Add this entry immediately after the existing `Parquet.Net` entry in
`Directory.Packages.props`:

```xml
<PackageVersion Include="Snappier" Version="1.3.1" />
```

- [ ] **Step 3: Make the override explicit in the production restore root**

Add this entry immediately after the existing `Parquet.Net` reference in
`src/EtabExtension.CLI/EtabExtension.CLI.csproj`:

```xml
<PackageReference Include="Snappier" />
```

- [ ] **Step 4: Make the override explicit in the test restore root**

Add this entry immediately after the existing `Parquet.Net` reference in
`EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj`:

```xml
<PackageReference Include="Snappier" />
```

- [ ] **Step 5: Make the override explicit in the visual-test restore root**

Add the same entry immediately after the existing `Parquet.Net` reference in
`EtabExtension.CLI.VisualTest/EtabExtension.CLI.VisualTest.csproj`:

```xml
<PackageReference Include="Snappier" />
```

- [ ] **Step 6: Restore with the patched dependency**

Run:

```powershell
dotnet restore EtabExtension.CLI.slnx --force-evaluate
```

Expected: PASS with no `NU1903` and no warning suppression.

- [ ] **Step 7: Prove all package graphs exclude the vulnerable version**

Run:

```powershell
dotnet list src/EtabExtension.CLI/EtabExtension.CLI.csproj package --include-transitive
dotnet list EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj package --include-transitive
dotnet list EtabExtension.CLI.VisualTest/EtabExtension.CLI.VisualTest.csproj package --include-transitive
```

Expected: all three outputs list `Snappier` 1.3.1 as a top-level package. None
contains 1.3.0.

- [ ] **Step 8: Commit the dependency override**

```powershell
git add Directory.Packages.props src/EtabExtension.CLI/EtabExtension.CLI.csproj EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj EtabExtension.CLI.VisualTest/EtabExtension.CLI.VisualTest.csproj docs/superpowers/specs/2026-08-09-snappier-remediation-design.md docs/superpowers/plans/2026-08-09-snappier-remediation.md
git commit -m "fix: pin patched Snappier"
```

### Task 2: Verify Parquet compatibility and the release artifact graph

**Files:**
- Verify: `EtabExtension.CLI.Tests/ParquetServiceTests.cs`
- Verify: `src/EtabExtension.CLI/Shared/Infrastructure/Parquet/ParquetService.cs`
- Generated, do not commit: `src/EtabExtension.CLI/obj/Release/net10.0/win-x64/etab-cli.deps.json`
- Generated, ignored: `artifacts/publish/cli-10/`

- [ ] **Step 1: Run focused Parquet behavior coverage**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore --filter FullyQualifiedName~ParquetServiceTests
```

Expected: PASS. The test writes and reads a parquet file and verifies the
column-mapping metadata.

- [ ] **Step 2: Run the repository's complete safe test gate**

Run:

```powershell
dotnet test EtabExtension.CLI.Tests/EtabExtension.CLI.Tests.csproj --no-restore
```

Expected: PASS with zero failed tests and no live ETABS process launched.

- [ ] **Step 3: Run the solution build gate**

Run:

```powershell
dotnet build EtabExtension.CLI.slnx --no-restore
```

Expected: PASS with zero warnings and zero errors.

- [ ] **Step 4: Produce the release-equivalent self-contained sidecar**

Run:

```powershell
dotnet publish src/EtabExtension.CLI/EtabExtension.CLI.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish/cli-10
```

Expected: PASS and `artifacts/publish/cli-10/etab-cli.exe` exists.

- [ ] **Step 5: Inspect the dependency manifest used by the single-file publish**

Run:

```powershell
rg -n 'Snappier/1\.3\.[01]' src/EtabExtension.CLI/obj/Release/net10.0/win-x64/etab-cli.deps.json
```

Expected: one or more matches for `Snappier/1.3.1` and zero matches for
`Snappier/1.3.0`. Also inspect `artifacts/publish/cli-10` and confirm no
standalone vulnerable `Snappier.dll` is present outside the single-file
executable.

- [ ] **Step 6: Check the final diff and worktree hygiene**

Run:

```powershell
git diff --check origin/master...HEAD
git status --short
```

Expected: `git diff --check` emits no output. Only intentional commits remain;
generated `obj`, `bin`, and `artifacts` output is ignored or unstaged.

- [ ] **Step 7: Report the implementation evidence to the lead**

Report the commit SHA, files changed, exact commands and outcomes, the
pre-change `NU1903` line, the resolved package versions for both projects, and
any uncertainty. Do not push, open a pull request, merge, or close issue #10.
