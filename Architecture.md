# EtabExtension.CLI — Architecture & Developer Reference

> **Purpose of this document:** Provide an AI assistant (or new developer) with enough context to continue development of this codebase without prior session history. Read this before touching any file.

---

## 1. What this project is

`EtabExtension.CLI` is a **C# sidecar process** called by a **Rust orchestrator**. Its sole job is to speak COM with ETABS — the structural engineering application — which has no native non-Windows API. The Rust side handles all business logic (reading Excel configs, coordinating workflows, outputting reports). The C# side handles everything that requires a live ETABS COM connection.

```
Rust CLI
  └─ spawns ──► etab-cli.exe <command> [flags]
                    │  stdout → one JSON object (success or failure)
                    │  stderr → progress / debug lines (Rust ignores these)
                    │  exit 0 = success,  exit 1 = failure
                    └─ COM ──► ETABS.exe
```

**Contract:** Every command writes exactly one JSON object to **stdout** and exits. All `Console.Error.WriteLine` progress lines go to **stderr**. `Console.SetOut(stderr)` is set in `Program.cs` so accidental `Console.WriteLine` calls don't corrupt the JSON stream.

---

## 2. Solution structure

```
EtabExtension.CLI/
├── Program.cs                          ← entry point, DI registration, command routing
│
├── Features/                           ← one folder per CLI command
│   ├── CloseModel/
│   ├── ExtractMaterials/
│   ├── ExtractResults/
│   │   ├── Tables/                     ← one extractor per ETABS results table
│   ├── GenerateE2K/
│   ├── GetStatus/
│   ├── OpenModel/
│   ├── RunAnalysis/
│   └── UnlockModel/
│
└── Shared/
    ├── Common/
    │   ├── Result.cs / ResultT.cs      ← Result<T> return type
    │   └── JsonExtensions.cs           ← ExitWithResult() — writes JSON + returns exit code
    └── Infrastructure/
        ├── Etabs/
        │   ├── EtabsExtensions.cs      ← AddEtabsInfrastructure() DI registration
        │   ├── Table/
        │   │   ├── IEtabsTableQueryService.cs   ← TableQueryRequest / TableQueryResult
        │   │   ├── EtabsTableQueryService.cs
        │   │   ├── IEtabsTableEditingService.cs
        │   │   ├── EtabsTableEditingService.cs
        │   │   └── IEtabsTableServicesFactory.cs / EtabsTableServicesFactory.cs
        │   └── Unit/
        │       ├── EtabsUnitPreset.cs   ← maps preset string → EtabSharp.System.Models.Units
        │       ├── EtabsUnitService.cs  ← ReadAndNormaliseAsync / RestoreAsync
        │       └── IEtabsUnitService.cs ← UnitSnapshot / UnitInfo types
        └── Parquet/
            ├── IParquetService.cs
            └── ParquetService.cs
```

### EtabSharp dependency

`EtabSharp` is a thin wrapper library over the raw `ETABSv1` COM interop. Key types used:

| EtabSharp type | What it wraps |
|---|---|
| `ETABSApplication` | Root COM object. Created via `ETABSWrapper.Connect()` (Mode A) or `ETABSWrapper.CreateNew()` (Mode B) |
| `IUnitSystem` | `app.Model.Units` — `SetPresentUnits(Units)` / `GetPresentUnits()` |
| `EtabSharp.System.Models.Units` | Struct with `eForce Force`, `eLength Length`, `eTemperature Temperature`, `bool IsUS`, `bool IsMetric` |
| `DatabaseTables` | `app.Model.DatabaseTables` — `GetTableForDisplayArray`, `SetLoadCasesSelectedForDisplay`, etc. |

---

## 3. ETABS connection modes

Every command uses exactly one of two modes. **Never mix them.**

| Mode | How started | ETABS visible? | On exit |
|------|-------------|----------------|---------|
| **Mode A** — attach | `ETABSWrapper.Connect()` | Yes (user's instance) | `app.Dispose()` — COM released, ETABS keeps running |
| **Mode B** — hidden | `ETABSWrapper.CreateNew()` + `app.Application.Hide()` | No | `app.Application.ApplicationExit(false)` then `app.Dispose()` |

**Mode A commands:** `get-status`, `open-model`, `close-model`, `unlock-model`
**Mode B commands:** `generate-e2k`, `extract-materials`, `run-analysis`, `extract-results`

Mode B always follows this skeleton:

```csharp
ETABSApplication? app = null;
try
{
    app = ETABSWrapper.CreateNew();
    app.Application.Hide();
    app.Model.Files.OpenFile(filePath);

    var (targetUnits, _) = EtabsUnitPreset.Resolve(request.Units);
    var unitService  = new EtabsUnitService(app);
    var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);

    // ... do work ...

    return Result.Ok(data);
}
catch (Exception ex) { return Result.Fail<T>($"Fatal error: {ex.Message}"); }
finally
{
    app?.Application.ApplicationExit(false);
    app?.Dispose();
}
```

---

## 4. Result pattern

Every service returns `Result<T>`. Every command calls `result.ExitWithResult()` which writes JSON to stdout and returns the exit code.

```csharp
// Success
return Result.Ok(new MyData { ... });

// Failure
return Result.Fail<MyData>("Descriptive error message");
```

**JSON output shape:**
```json
// Success
{ "success": true, "data": { ... }, "timestamp": "..." }

// Failure
{ "success": false, "error": "...", "timestamp": "..." }
```

`Result<T>` with partial failures (e.g. `extract-results`) uses `Result.Ok` even when some tables failed — the failures are encoded inside `data.tables[slug].success = false`. A top-level `Result.Fail` is reserved for fatal errors (ETABS won't start, file not found).

---

## 5. Unit system handling

**Problem:** ETABS models can be saved in any unit system. All extracted numeric values must be in a known, consistent unit for Rust to interpret them correctly.

**Solution:** Every Mode B command accepts a `--units` flag (or `"units"` JSON field). The service resolves it via `EtabsUnitPreset.Resolve()` and normalises ETABS before any extraction. The active units are reported back in `result.data.units`.

```csharp
var (targetUnits, error) = EtabsUnitPreset.Resolve(request.Units);
// targetUnits is EtabSharp.System.Models.Units — passed directly to SetPresentUnits
```

**Why not a `set-units` command:** A persistent global config file would create hidden shared state that both Rust and C# must read and agree on. Instead units are passed explicitly per-invocation from the Rust/Excel config.

**Valid preset strings** (case-insensitive):

| Preset | Force | Length | Temp |
|--------|-------|--------|------|
| `US_Kip_Ft` *(default)* | kip | ft | F |
| `US_Kip_In` | kip | in | F |
| `US_Lb_Ft` | lb | ft | F |
| `US_Lb_In` | lb | in | F |
| `SI_kN_m` | kN | m | C |
| `SI_kN_mm` | kN | mm | C |
| `SI_N_m` | N | m | C |
| `SI_N_mm` | N | mm | C |
| `SI_kgf_m` | kgf | m | C |
| `SI_tonf_m` | tonf | m | C |

---

## 6. Commands reference

### `get-status`
Attaches to the running user ETABS (Mode A). Returns PID, version, open file path, lock/analysis status, and active unit system.

```
etab-cli get-status
```

**No flags.** Returns `GetStatusData` — `isRunning`, `pid`, `etabsVersion`, `openFilePath`, `isLocked`, `isAnalyzed`, `unitSystem`.

---

### `open-model`
Opens an `.edb` in ETABS (Mode A by default, Mode B variant with `--new-instance`).

```
etab-cli open-model -f <path.edb> [--save] [--new-instance]
```

| Flag | Description |
|------|-------------|
| `-f` / `--file` | Path to `.edb` (required) |
| `--save` | Save currently open model before switching (Mode A only) |
| `--new-instance` | Spawn a new **visible** ETABS window and open the file in it |

---

### `close-model`
Clears the ETABS workspace without closing ETABS (Mode A). Uses `InitializeNewModel()` internally — never triggers a save dialog.

```
etab-cli close-model [--save]
```

---

### `unlock-model`
Clears the post-analysis lock on the currently open model (Mode A). The file must already be open.

```
etab-cli unlock-model -f <path.edb>
```

---

### `generate-e2k`
Exports `.edb` → `.e2k` text format using a hidden ETABS instance (Mode B).

```
etab-cli generate-e2k -f <path.edb> -o <output.e2k> [--overwrite]
```

---

### `run-analysis`
Runs ETABS analysis on an `.edb` using a hidden instance (Mode B). Results are written to ETABS sidecar files (`.Y*`, `.K_*`, `.msh`) — **`SaveFile()` is never called** because it would delete those sidecar files.

```
etab-cli run-analysis -f <path.edb> [-c CASE1 CASE2 ...] [-u US_Kip_Ft]
```

| Flag | Description |
|------|-------------|
| `-f` / `--file` | Path to `.edb` (required) |
| `-c` / `--cases` | Specific load case names to run. Omit = all cases |
| `-u` / `--units` | Unit preset (default: `US_Kip_Ft`) |

Returns `RunAnalysisData` — `caseCount`, `finishedCaseCount`, `casesRun`,
`casesRunFinishedCount`, `casesNotFinished`, `analysisTimeMs`, `units`.

The run selection is always stated, never inherited: a default run restores the
all-cases selection, a `--cases` run deselects everything before selecting what
it asked for, and both read the flags back from the model before analysing.
Success is judged against `casesRun` only — `finishedCaseCount` counts finished
cases across the whole model, stale ones included, and cannot make a run whose
own cases failed successful. A `--cases` run is all-or-nothing: any requested case
the model does not define fails the command before any flag is touched, rather
than running the rest and reporting success. Selection and verdict live in
`Shared/Infrastructure/Etabs/Analysis`, shared with `analyze-and-extract`.

---

### `extract-materials`
Extracts one ETABS database table (default: `"Material List by Story"`) to a `.parquet` file using a hidden ETABS instance (Mode B). No load filter — material tables have no load dependency.

```
etab-cli extract-materials -f <path.edb> -o <outputDir> [-t "Table Key"] [-u US_Kip_Ft]
```

| Flag | Description |
|------|-------------|
| `-f` / `--file` | Path to `.edb` (required) |
| `-o` / `--output-dir` | Output directory (required). File = `{tableSlug}.parquet` |
| `-t` / `--table-key` | ETABS table key (default: `"Material List by Story"`) |
| `-u` / `--units` | Unit preset (default: `US_Kip_Ft`) |
| `--field-keys` | Specific columns to include (space-separated). Default: all |

Returns `ExtractMaterialsData` — `outputFile` (null if 0 rows), `rowCount`, `discardedRowCount`, `units`, `extractionTimeMs`.

---

### `extract-results`
Extracts multiple ETABS results/material tables to `.parquet` files in a single hidden ETABS session (Mode B). Partial failures are tolerated — all requested tables are attempted.

```
etab-cli extract-results -f <path.edb> -o <outputDir> -r '<requestJson>'
```

| Flag | Description |
|------|-------------|
| `-f` / `--file` | Path to `.edb` (required) |
| `-o` / `--output-dir` | Output directory (required) |
| `-r` / `--request` | JSON blob with optional `"units"` and required `"tables"` (see below) |

**Request JSON format:**
```json
{
  "units": "US_Kip_Ft",
  "tables": {
    "materialListByStory":         {},
    "materialPropertiesConcreteData": {},
    "groupAssignments":            {},
    "storyDefinitions":            {},
    "pierSectionProperties":       { "groups": ["Piers"] },
    "baseReactions":               { "loadCases": ["*"], "loadCombos": ["*"] },
    "storyForces":                 { "loadCases": ["DEAD", "LIVE", "EQX", "EQY"] },
    "jointDrifts":                 { "loadCases": ["EQX", "EQY"], "groups": ["DriftJoints"] },
    "pierForces":                  { "loadCombos": ["ENV-LRFD-MAX", "ENV-LRFD-MIN"], "groups": ["Piers"] },
    "modalParticipatingMassRatios": {}
  }
}
```

**TableFilter load selection rules** (same for `loadCases` and `loadCombos`):

| Value | Behaviour |
|-------|-----------|
| omitted / `null` | Select **nothing** — no rows for that category |
| `["*"]` | Select **all** items of that category from the model |
| `["X", "Y"]` | Select **exactly** those named items |

**Output files** — one `.parquet` per table, named by slug:

| `tables` key | Slug / filename | `RequiresAnalysis` | ETABS table key |
|---|---|---|---|
| `materialListByStory` | `material_list_by_story.parquet` | false | `"Material List by Story"` |
| `materialPropertiesConcreteData` | `material_properties_concrete_data.parquet` | false | `"Material Properties - Concrete Data"` |
| `groupAssignments` | `group_assignments.parquet` | false | `"Group Assignments"` |
| `storyDefinitions` | `story_definitions.parquet` | false | `"Story Definitions"` |
| `pierSectionProperties` | `pier_section_properties.parquet` | false | `"Pier Section Properties"` |
| `baseReactions` | `base_reactions.parquet` | **true** | `"Base Reactions"` |
| `storyForces` | `story_forces.parquet` | **true** | `"Story Forces"` |
| `jointDrifts` | `joint_drifts.parquet` | **true** | `"Joint Drifts"` |
| `pierForces` | `pier_forces.parquet` | **true** | `"Pier Forces"` |
| `modalParticipatingMassRatios` | `modal_participating_mass_ratios.parquet` | **true** | `"Modal Participating Mass Ratios"` |

Tables with `RequiresAnalysis = true` are **skipped** (not failed) when the model has not been analyzed/locked, with a clear error message. Geometry tables (`false`) always run.

Returns `ExtractResultsData` — `tables` (dict of slug → `TableExtractionOutcome`), `succeededCount`, `failedCount`, `totalRowCount`, `units`, `extractionTimeMs`.

---

## 7. Feature folder convention

Every feature is self-contained. Adding a new command means:

```
Features/MyFeature/
├── MyFeatureCommand.cs      ← System.CommandLine option parsing → calls service → Environment.Exit(result.ExitWithResult())
├── MyFeatureExtensions.cs   ← AddMyFeatureFeature() extension method
├── IMyFeatureService.cs     ← interface with one method returning Task<Result<MyFeatureData>>
├── MyFeatureService.cs      ← implementation
└── Models/
    └── MyFeatureData.cs     ← record with [JsonPropertyName] attributes
```

Then in `Program.cs`:
1. `services.AddMyFeatureFeature()` — DI registration
2. `rootCommand.Subcommands.Add(MyFeatureCommand.Create(app.Services))` — command routing

No other files need touching.

---

## 8. Table query infrastructure

`IEtabsTableQueryService` is the unified API for reading any ETABS database table. It is **not** a DI singleton — it requires a live `ETABSApplication`. Create it via `IEtabsTableServicesFactory`:

```csharp
var queryService = _tableFactory.CreateQueryService(app);
var result = await queryService.QueryAsync(new TableQueryRequest("Base Reactions")
{
    LoadCases  = ["DEAD", "LIVE"],          // null = nothing, ["*"] = all
    LoadCombos = null,
    Groups     = ["Piers"],                 // null = whole model
    FieldKeys  = null,                      // null = all columns
});
```

**Internal flow per query:**
1. Resolve load selection → call `SetLoadCasesSelectedForDisplay` / `SetLoadCombinationsSelectedForDisplay`
2. Fetch table once per group (or once whole-model if no groups)
3. Merge rows across groups, deduplicate
4. Discard rows where every value is empty
5. **Always** reset ETABS display selection back to all-selected in `finally`

**`TableQueryRequest.Wildcard`** = `"*"` — the single sentinel string for "select all". This constant lives in the infrastructure layer. `TableFilter.Wildcard` in `ExtractResults.Models` mirrors it.

`IEtabsTableEditingService` follows the same factory pattern and is used for writing back to ETABS tables (e.g. RSA scale factor updates).

---

## 9. Adding a new results table to `extract-results`

Three steps, no other files need changing:

**Step 1 — Create the extractor:**
```csharp
// Features/ExtractResults/Tables/MyTableExtractor.cs
public class MyTableExtractor : TableExtractorBase
{
    public MyTableExtractor(ILogger<MyTableExtractor> logger) : base(logger) { }

    public override string Slug  => "my_table";
    public override string Label => "My Table";
    // Override to false if this is a geometry table (no analysis required):
    // public override bool RequiresAnalysis => false;

    protected override string EtabsTableKey => "My Table";   // exact ETABS key

    protected override TableQueryRequest BuildRequest(TableFilter filter) =>
        new(EtabsTableKey)
        {
            LoadCases  = filter.LoadCases,
            LoadCombos = filter.LoadCombos,
            Groups     = filter.Groups,
            FieldKeys  = filter.FieldKeys,
        };
}
```

**Step 2 — Add property to `TableSelections`:**
```csharp
// Features/ExtractResults/Models/ExtractResultsRequest.cs
[JsonPropertyName("myTable")]
public TableFilter? MyTable { get; init; }
```

**Step 3 — Register in `TableExtractorRegistry`:**
```csharp
// Features/ExtractResults/Tables/TableExtractorRegistry.cs — constructor + DI param
new(s => s.MyTable, myTableExtractor),
```

Also register in `ExtractResultsExtensions.cs`:
```csharp
services.AddSingleton<MyTableExtractor>();
```

---

## 10. Session poisoning — critical ETABS behaviour

ETABS maintains a **global display selection state** for load cases and combos. If you call `SetLoadCasesSelectedForDisplay(["EQX"])` and then query a different table **without resetting**, that second table will also only show EQX rows — silently.

`EtabsTableQueryService` defends against this by always calling `ResetSelectionAsync()` in its `finally` block after every `QueryAsync` call. **Never call `GetTableForDisplayArray` directly** without setting up and tearing down the display selection yourself.

Similarly: if a table query returns `ReturnCode: 1` (ETABS internal error) on an unanalyzed model, ETABS can enter a corrupted display state that poisons all subsequent queries in the session. `ExtractResultsService` defends against this by checking `AreAllCasesFinished()` and `IsLocked()` upfront and skipping `RequiresAnalysis = true` tables when the model is not ready.

---

## 11. `run-analysis` — why `SaveFile()` is never called

ETABS writes analysis results to **sidecar files** alongside the `.edb`:
- `.Y`, `.Y01`, `.Y03`, `.Y09`, `.Y0A` — displacement/force result sets
- `.K_I`, `.K_J`, `.K_M` — stiffness matrices
- `.msh` — mesh data

These are written directly to disk by ETABS during analysis. If `SaveFile()` is called after analysis on a hidden instance, ETABS overwrites the `.edb` from its **in-memory model state** — which does not include the sidecar files — effectively **deleting all results**. The correct exit is `ApplicationExit(false)`, which lets ETABS clean up normally and preserves the sidecars.

---

## 12. Parquet output format

All tables are written as Parquet files with **all-string columns**. ETABS `DatabaseTables` returns all values as strings; no numeric type inference is performed. Rust is responsible for casting columns to appropriate types.

Column names are sanitised (non-alphanumeric → `_`) and deduplicated (duplicate `Name` → `Name_2`). This matches the EtabSharp `MakeUniqueFieldNames` convention.

---

## 13. VisualTest harness

`VisualTest/Program.cs` is an interactive TUI that calls the real service classes **in-process** (no binary needed). It is the fastest way to test a new feature against a live ETABS instance during development. Run it with F5 in the `VisualTest` project.

It reuses the same DI container as production and surfaces every command as a numbered menu option. All progress output is captured and coloured. On failure it shows the full JSON error object.

---

## 14. Key invariants — do not break

1. **One JSON object to stdout, nothing else.** `Console.SetOut(stderr)` is set in `Program.cs`. Never open stdout yourself except via `ExitWithResult()`.
2. **Always call `ApplicationExit(false)` before `Dispose()` in Mode B.** Never call `SaveFile()` in Mode B after analysis.
3. **Always reset ETABS load selection after every table query.** `QueryAsync` does this automatically. If using `GetTableArrayAsync` directly, call `ClearLoadSelectionAsync()` in a `finally` block.
4. **`null` filter = skip that table entirely.** A non-null `TableFilter` with all-null fields (`{}`) means "run this table with no load filter" (valid for geometry tables). The distinction matters.
5. **Unit preset resolved before ETABS starts.** All services call `EtabsUnitPreset.Resolve()` and return `Result.Fail` on bad input before any COM call. Fast failure, clear error message.
6. **`TableQueryRequest.Wildcard = "*"` is the source of truth.** `TableFilter.Wildcard` in ExtractResults.Models mirrors it. Do not define a third copy.
