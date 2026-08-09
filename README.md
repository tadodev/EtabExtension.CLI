# EtabExtension.CLI

C# command-line sidecar for automating ETABS from the Rust EtabExtension
workspace. The sidecar exists to isolate ETABS COM automation behind a small,
stable process contract.

## What This Does

`etab-cli` can:

- inspect and manage a user-visible ETABS instance
- open and close models
- unlock model files
- export `.edb` snapshots to `.e2k`
- run analysis in a hidden ETABS session
- extract ETABS database tables to Parquet
- run analysis and extraction in one hidden ETABS session
- export E2K, snapshot tables, and metadata in one hidden ETABS session
- write lightweight `run-metrics.json` timing artifacts for combined sessions

The newest important commands are `analyze-and-extract` and `snapshot-export`.
They avoid paying the ETABS startup/open cost twice for the analysis path and
the commit snapshot path.

## Repository Map

```text
src/EtabExtension.CLI/
  Program.cs
  Features/
    AnalyzeAndExtract/
    SnapshotExport/
    CloseModel/
    ExtractMaterials/
    ExtractResults/
    GenerateE2K/
    GetStatus/
    OpenModel/
    RunAnalysis/
    UnlockModel/
  Shared/
    Common/
    Infrastructure/
      Etabs/
        Metadata/
      Parquet/

EtabExtension.CLI.Tests/
EtabExtension.CLI.VisualTest/
Architecture.md
Reference.md
API_REFERENCE.md
AGENTS.md
```

## Process Contract

Every command writes exactly one JSON object to stdout. All progress goes to
stderr.

This matters because the Rust caller parses stdout as the command result and
streams stderr as human progress.

## Quick Start

Build:

```powershell
dotnet build EtabExtension.CLI.slnx
```

Run tests:

```powershell
dotnet test EtabExtension.CLI.Tests\EtabExtension.CLI.Tests.csproj
```

Show commands:

```powershell
dotnet run --project src\EtabExtension.CLI\EtabExtension.CLI.csproj -- --help
```

## Runtime ETABS API Dependency

The Alpha supports ETABS 23. `etab-cli` resolves `ETABSv1.dll` directly from
the customer's ETABS 23 installation. Set `ETABS_INSTALL_DIR` only for a custom
ETABS 23 install location; when set, it is authoritative and an invalid value
does not fall back to the default Program Files installation.

EtabSharp may copy `ETABSv1.dll` into local build and publish directories so
the project can compile. That proprietary CSI file is not an installer input:
desktop packaging must bundle `etab-cli.exe` and must exclude `ETABSv1.dll`.
Do not embed, commit, or redistribute the DLL.

Run the combined workflow:

```powershell
dotnet run --project src\EtabExtension.CLI\EtabExtension.CLI.csproj -- `
  analyze-and-extract `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\results `
  --units US_Kip_Ft `
  --profile results
```

Run the commit snapshot export workflow:

```powershell
dotnet run --project src\EtabExtension.CLI\EtabExtension.CLI.csproj -- `
  snapshot-export `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\v1 `
  --units US_Kip_Ft `
  --profile snapshot
```

## Command Overview

| Command | ETABS session | Purpose |
|---|---|---|
| `get-status` | attach | Report currently running ETABS/model state |
| `open-model` | attach or visible new instance | Open an `.edb` for user interaction |
| `close-model` | attach | Close the current visible model |
| `unlock-model` | attach | Clear model lock state |
| `generate-e2k` | hidden | Export `.edb` to `.e2k` |
| `run-analysis` | hidden | Run ETABS analysis |
| `extract-results` | hidden | Extract configured tables to Parquet |
| `extract-materials` | hidden | Extract one material/geometry table |
| `analyze-and-extract` | hidden | Run analysis, extract tables, collect metadata |
| `snapshot-export` | hidden | Export E2K, snapshot tables, collect metadata |

See `API_REFERENCE.md` for flags, JSON shapes, and examples.

## Current State After Review

The sidecar now has the pieces needed to address the largest analysis-path
bottleneck:

- `analyze-and-extract` opens ETABS once for analysis plus extraction.
- `EtabsSessionHelpers` contains reusable operations for an already-open model.
- `ExtractTablesOnOpenModelAsync` centralizes table extraction for old and new
  command paths.
- `EtabsTableQueryService` caches wildcard load case/combo name lists per run.
- Table display selection is reset once at the end of extraction.
- `model-metadata.json` is written by `analyze-and-extract`.
- `snapshot-export` writes version-root `model.e2k`, `materials/*.parquet`, and
  `model-metadata.json` for the Rust commit workflow.
- `model-metadata.json` schema version 2 includes load patterns, cases, combos,
  stories, groups, materials, frame/area sections, and warnings.
- `run-metrics.json` schema version 1 records phase timings so slow ETABS
  startup/open/table/metadata phases can be found from artifacts.
- Extraction profiles (`full`, `results`, `geometry`, `snapshot`) let callers
  avoid broad default table extraction when they only need part of the model.

Still pending outside this wave:

- Timeout scaling and heartbeat cancellation belong in the Rust sidecar client.

## Documentation

- `AGENTS.md`: implementation notes for coding agents.
- `API_REFERENCE.md`: current command and JSON API reference.
- `Architecture.md`: deeper historical architecture notes.
- `Reference.md`: older command reference; prefer `API_REFERENCE.md` for the
  current combined-session command surface.
