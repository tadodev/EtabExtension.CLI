# API Reference

`etab-cli` is a one-shot ETABS automation sidecar. It is called by Rust, but it
can also be run directly for debugging.

## Process Contract

Every command writes exactly one JSON envelope to stdout:

```json
{
  "success": true,
  "data": {},
  "error": null,
  "timestamp": "2026-05-06T00:00:00Z"
}
```

Progress lines go to stderr. The Rust caller treats `success: false` as a fatal
sidecar error. Per-table extraction failures are represented inside `data.tables`
and do not necessarily fail the process.

## Units

Commands that normalize units accept:

- `US_Kip_Ft`
- `US_Kip_In`
- `US_Lb_Ft`
- `US_Lb_In`
- `SI_kN_m`
- `SI_kN_mm`
- `SI_N_m`
- `SI_N_mm`
- `SI_kgf_m`
- `SI_tonf_m`

When omitted, the default is `US_Kip_Ft`.

## Table Selection JSON

`extract-results`, `analyze-and-extract`, and `snapshot-export` use
`TableSelections` JSON.
Each table property is either:

- `null` or omitted: skip the table
- `{}`: run the table without load selection
- `{ "loadCases": ["*"], "loadCombos": ["*"] }`: select all cases and combos
- `{ "loadCases": ["DEAD", "LIVE"], "groups": ["Core"] }`: exact filters

Supported table keys:

- `materialListByStory`
- `materialPropertiesConcreteData`
- `groupAssignments`
- `storyDefinitions`
- `baseReactions`
- `storyForces`
- `jointDrifts`
- `pierForces`
- `pierSectionProperties`
- `modalParticipatingMassRatios`

Extraction profiles can supply common table sets when the request `tables`
object is empty:

- `full`: geometry, material, and result tables
- `results`: result tables plus modal participating mass ratios
- `geometry`: stories, pier sections, groups, and material tables
- `snapshot`: the geometry/material table set used by commit snapshot export

`TableFilter` properties:

```json
{
  "loadCases": ["*"],
  "loadCombos": ["*"],
  "groups": ["Core"],
  "fieldKeys": ["Story", "OutputCase"]
}
```

## Commands

### `get-status`

Attach to the current ETABS instance and report process/model state.

```powershell
etab-cli get-status
```

Returns `GetStatusData`:

```json
{
  "isRunning": true,
  "pid": 1234,
  "etabsVersion": "23.0.0",
  "openFilePath": "D:\\Models\\tower.edb",
  "isModelOpen": true,
  "isLocked": true,
  "isAnalyzed": true,
  "unitSystem": {
    "force": "kip",
    "length": "ft",
    "temperature": "F",
    "isUS": true,
    "isMetric": false
  }
}
```

### `open-model`

Open an `.edb` in a visible ETABS instance.

```powershell
etab-cli open-model --file D:\Models\tower.edb [--save] [--new-instance]
```

### `close-model`

Close the current visible ETABS model.

```powershell
etab-cli close-model --save
etab-cli close-model --no-save
```

### `unlock-model`

Unlock an ETABS model file.

```powershell
etab-cli unlock-model --file D:\Models\tower.edb
```

### `generate-e2k`

Open a hidden ETABS session, export `.edb` to `.e2k`, then exit.

```powershell
etab-cli generate-e2k --file D:\Models\tower.edb --output D:\Out\tower.e2k --overwrite
```

### `run-analysis`

Open a hidden ETABS session, run analysis, then exit.

```powershell
etab-cli run-analysis --file D:\Models\tower.edb --units US_Kip_Ft
etab-cli run-analysis --file D:\Models\tower.edb --cases DEAD LIVE EQX
```

Returns `RunAnalysisData`:

```json
{
  "filePath": "D:\\Models\\tower.edb",
  "casesRequested": ["DEAD"],
  "caseCount": 12,
  "finishedCaseCount": 12,
  "casesRun": ["DEAD"],
  "casesRunFinishedCount": 1,
  "casesNotFinished": [],
  "analysisTimeMs": 90000,
  "units": {
    "force": "kip",
    "length": "ft",
    "temperature": "F"
  }
}
```

A default run (no `--cases`) explicitly RESTORES the all-cases selection before
analysing, so it never inherits the narrowed selection an earlier `--cases`
request left behind on a long-lived `serve` session. A `--cases` run establishes
its selection outright — selecting what it asked for and deselecting the rest.
Either way the selection is read back from the model before anything runs, and
`casesRun` reports what the model actually held.

A `--cases` run is all-or-nothing. If ANY requested case is absent from the
model's load-case list, the command fails and names it, without changing a single
run flag and without running analysis — partial analysis is never returned as
success. Absence is established only from a census call that itself succeeded; a
failed census fails the command as a failed call, never as "case not found".

Success is judged against `casesRun` alone. `finishedCaseCount` is a fact about
the whole model, including results left over from earlier runs, and cannot carry
a run: if a requested case does not finish, the command fails and names it in
`casesNotFinished`.

### `extract-results`

Open a hidden ETABS session, extract requested tables to Parquet, then exit.

```powershell
etab-cli extract-results `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\results `
  --request '{"units":"US_Kip_Ft","tables":{"baseReactions":{"loadCases":["*"],"loadCombos":["*"]},"storyDefinitions":{}}}'
```

Returns `ExtractResultsData` with `tables` keyed by table slug:

```json
{
  "filePath": "D:\\Models\\tower.edb",
  "outputDir": "D:\\Out\\results",
  "tables": {
    "baseReactions": {
      "success": true,
      "outputFile": "D:\\Out\\results\\base_reactions.parquet",
      "rowCount": 120,
      "discardedRowCount": 0,
      "error": null,
      "extractionTimeMs": 2000
    }
  },
  "totalRowCount": 120,
  "succeededCount": 1,
  "failedCount": 0,
  "units": {
    "force": "kip",
    "length": "ft",
    "temperature": "F"
  },
  "extractionTimeMs": 2500
}
```

### `extract-materials`

Open a hidden ETABS session and extract one material/geometry table.

```powershell
etab-cli extract-materials `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\materials `
  --table-key "Material List by Story" `
  --units US_Kip_Ft
```

### Metadata Schema v2

`model-metadata.json` is written by combined-session commands. Existing v1
fields are preserved, and schema v2 adds richer catalogs for UX autocomplete and
configuration validation.

```json
{
  "schemaVersion": 2,
  "filePath": "D:\\Models\\tower.edb",
  "etabsVersion": "23.0.0",
  "isAnalyzed": true,
  "isLocked": true,
  "units": "kip/ft/F",
  "loadPatterns": [
    { "name": "DEAD", "loadType": "Dead", "selfWeightMultiplier": 1.0 }
  ],
  "loadCases": [
    { "name": "DEAD", "caseType": "LinStatic" }
  ],
  "loadCombinations": [
    {
      "name": "D+L",
      "comboType": "Linear Add",
      "constituentCases": ["DEAD", "LIVE"],
      "items": [
        { "name": "DEAD", "itemType": "LoadCase", "scaleFactor": 1.2, "modeNumber": 0 }
      ]
    }
  ],
  "stories": [
    { "name": "L1", "elevation": 14.0, "height": 14.0, "isMasterStory": true, "similarToStory": null }
  ],
  "groups": ["Core"],
  "groupDetails": [{ "name": "Core", "assignmentCount": 42 }],
  "materials": [
    { "name": "C5000", "materialType": "Concrete", "concreteFc": 5.0, "weightPerVolume": 0.15 }
  ],
  "frameSections": [{ "name": "C24x24", "sectionType": "ConcreteRectangular" }],
  "areaSections": [{ "name": "WALL-12", "propertyType": "Wall" }],
  "warnings": [{ "category": "groups", "message": "Could not read ..." }],
  "collectedAt": "2026-05-06T00:00:00Z"
}
```

### Run Metrics Schema v1

`run-metrics.json` is written by combined-session commands next to the command
outputs. It is intentionally small and is meant for finding slow models/tables.

```json
{
  "schemaVersion": 1,
  "command": "analyze-and-extract",
  "filePath": "D:\\Models\\tower.edb",
  "outputDir": "D:\\Out\\results",
  "totalElapsedMs": 93000,
  "phases": [
    { "name": "startEtabs", "elapsedMs": 25000, "success": true, "message": null },
    { "name": "openModel", "elapsedMs": 12000, "success": true, "message": null },
    { "name": "extractTables", "elapsedMs": 18000, "success": true, "message": null }
  ],
  "collectedAt": "2026-05-07T00:00:00Z"
}
```

### `analyze-and-extract`

Open one hidden ETABS session, run analysis, extract tables, collect metadata,
write `model-metadata.json`, then exit.

```powershell
etab-cli analyze-and-extract `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\results `
  --units US_Kip_Ft `
  --cases DEAD LIVE `
  --profile results
```

With request JSON:

```powershell
etab-cli analyze-and-extract `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\results `
  --request '{"units":"US_Kip_Ft","cases":["DEAD"],"tables":{"baseReactions":{"loadCases":["*"],"loadCombos":["*"]},"storyDefinitions":{}}}'
```

`--file` and `--output-dir` are always flat flags. `--request` supplies units,
cases, table selections, optional `metadataOutputPath`, optional
`metricsOutputPath`, and optional `extractionProfile`.

Returns `AnalyzeAndExtractData`:

```json
{
  "filePath": "D:\\Models\\tower.edb",
  "outputDir": "D:\\Out\\results",
  "casesRequested": ["DEAD"],
  "caseCount": 12,
  "finishedCaseCount": 12,
  "analysisTimeMs": 90000,
  "tables": {},
  "totalRowCount": 0,
  "succeededCount": 0,
  "failedCount": 0,
  "extractionTimeMs": 2500,
  "metadataPath": "D:\\Out\\results\\model-metadata.json",
  "metadata": { "schemaVersion": 2, "filePath": "D:\\Models\\tower.edb" },
  "metricsPath": "D:\\Out\\results\\run-metrics.json",
  "metrics": { "schemaVersion": 1, "command": "analyze-and-extract", "phases": [] },
  "units": {
    "force": "kip",
    "length": "ft",
    "temperature": "F"
  },
  "totalElapsedMs": 93000
}
```

### `snapshot-export`

Open one hidden ETABS session, export E2K, extract snapshot/material tables,
collect metadata, write `model-metadata.json`, then exit.

```powershell
etab-cli snapshot-export `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\v1 `
  --units US_Kip_Ft `
  --profile snapshot
```

With request JSON:

```powershell
etab-cli snapshot-export `
  --file D:\Models\tower.edb `
  --output-dir D:\Out\v1 `
  --request '{"units":"US_Kip_Ft","e2kFileName":"model.e2k","materialsDirName":"materials","metadataFileName":"model-metadata.json","metricsFileName":"run-metrics.json","extractionProfile":"snapshot","tables":{}}'
```

Returns `SnapshotExportData`:

```json
{
  "filePath": "D:\\Models\\tower.edb",
  "outputDir": "D:\\Out\\v1",
  "e2kFile": "D:\\Out\\v1\\model.e2k",
  "e2kSizeBytes": 2048,
  "materialsDir": "D:\\Out\\v1\\materials",
  "tables": {},
  "totalRowCount": 0,
  "succeededCount": 0,
  "failedCount": 0,
  "metadataPath": "D:\\Out\\v1\\model-metadata.json",
  "metadata": { "schemaVersion": 2, "filePath": "D:\\Models\\tower.edb" },
  "metricsPath": "D:\\Out\\v1\\run-metrics.json",
  "metrics": { "schemaVersion": 1, "command": "snapshot-export", "phases": [] },
  "units": { "force": "kip", "length": "ft", "temperature": "F" },
  "totalElapsedMs": 45000
}
```

## Known Workflow Gaps

- Timeout scaling and heartbeat cancellation are Rust-side concerns in the
  sibling `D:\Work\EtabExtension` workspace.
