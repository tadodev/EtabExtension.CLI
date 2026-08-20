using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.ExtractResults.Tables;
using EtabExtension.CLI.Features.RunAnalysis.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Unit;
using EtabExtension.CLI.Shared.Infrastructure.Parquet;
using EtabSharp.Core;
using EtabSharp.System.Models;
using ETABSv1;
using System.Diagnostics;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

internal static class EtabsSessionHelpers
{
    internal static void HideIfVisible(ETABSApplication app)
    {
        if (app.Application.Visible()) app.Application.Hide();
    }

    /// <summary>
    /// Opens a model on an already-owned session through the one canonical
    /// <see cref="EtabsModelOpen"/> primitive. This helper owns no OpenFile call of
    /// its own, so combined-session commands cannot drift from the validated
    /// <c>open-model</c> boundary.
    /// </summary>
    internal static async Task<Result> OpenFileAsync(ETABSApplication app, string filePath)
    {
        await Task.CompletedTask;

        var opened = EtabsModelOpen.OpenOnApp(app, filePath, save: false);
        return opened.Success
            ? Result.Ok()
            : Result.Fail(opened.Error ?? "OpenFile failed");
    }

    /// <summary>
    /// Applies an already-resolved <see cref="Units"/> preset to the open ETABS model.
    /// The caller is responsible for resolving and validating the preset before ETABS starts.
    /// </summary>
    internal static async Task<UnitSnapshot> NormaliseUnitsAsync(
        ETABSApplication app,
        Units targetUnits)
    {
        var unitService = new EtabsUnitService(app);
        var unitSnapshot = await unitService.ReadAndNormaliseAsync(targetUnits);
        Console.Error.WriteLine(EtabsUnitService.FormatSnapshot(unitSnapshot));
        return unitSnapshot;
    }

    internal static async Task<Result<RunAnalysisData>> RunAnalysisOnOpenModelAsync(
        ETABSApplication app,
        string filePath,
        List<string>? cases,
        UnitSnapshot unitSnapshot)
    {
        await Task.CompletedTask;

        var hasSpecificCases = cases is { Count: > 0 };
        var stopwatch = Stopwatch.StartNew();

        if (app.Model.ModelInfo.IsLocked())
        {
            Console.Error.WriteLine("ℹ Clearing analysis lock...");
            app.Model.ModelInfo.SetLocked(false);
        }

        if (hasSpecificCases)
        {
            app.Model.Analyze.SetRunCaseFlag(caseName: string.Empty, run: false, all: true);
            Console.Error.WriteLine("ℹ Set all cases to skip");

            var notFound = new List<string>();
            foreach (var caseName in cases!)
            {
                try
                {
                    app.Model.Analyze.SetRunCaseFlag(caseName: caseName, run: true, all: false);
                    Console.Error.WriteLine($"  ✓ '{caseName}' set to run");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ⚠ '{caseName}' not found: {ex.Message}");
                    notFound.Add(caseName);
                }
            }

            if (notFound.Count == cases!.Count)
            {
                return Result.Fail<RunAnalysisData>(
                    $"None of the requested cases were found: {string.Join(", ", notFound)}");
            }
        }
        else
        {
            Console.Error.WriteLine("ℹ Running all cases (default)");
        }

        Console.Error.WriteLine("ℹ Running analysis...");

        int analysisRet = hasSpecificCases
            ? RunSpecificCases(app)
            : app.Model.Analyze.RunCompleteAnalysis();

        stopwatch.Stop();

        if (analysisRet != 0)
        {
            return Result.Fail<RunAnalysisData>($"Analysis failed (ret={analysisRet})")
                with
            {
                Data = new RunAnalysisData
                {
                    FilePath = filePath,
                    CasesRequested = hasSpecificCases ? cases : null,
                    AnalysisTimeMs = stopwatch.ElapsedMilliseconds
                }
            };
        }

        var caseStatuses = app.Model.Analyze.GetCaseStatus();
        var finishedCount = caseStatuses.Count(cs => cs.IsFinished);

        Console.Error.WriteLine(
            $"✓ Analysis complete ({caseStatuses.Count} cases, {finishedCount} finished, {stopwatch.ElapsedMilliseconds} ms)");

        if (finishedCount == 0)
        {
            return Result.Fail<RunAnalysisData>(
                "Analysis completed, but no cases were marked finished. Check ETABS run-case selections.")
                with
            {
                Data = new RunAnalysisData
                {
                    FilePath = filePath,
                    CasesRequested = hasSpecificCases ? cases : null,
                    CaseCount = caseStatuses.Count,
                    FinishedCaseCount = finishedCount,
                    AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
                    Units = unitSnapshot.Active
                }
            };
        }

        Console.Error.WriteLine(
            "ℹ Results written to sidecar files — skipping SaveFile() to preserve them");

        return Result.Ok(new RunAnalysisData
        {
            FilePath = filePath,
            CasesRequested = hasSpecificCases ? cases : null,
            CaseCount = caseStatuses.Count,
            FinishedCaseCount = finishedCount,
            AnalysisTimeMs = stopwatch.ElapsedMilliseconds,
            Units = unitSnapshot.Active
        });
    }

    internal static async Task<Dictionary<string, TableExtractionOutcome>> ExtractTablesOnOpenModelAsync(
        ETABSApplication app,
        TableSelections tables,
        string outputDir,
        bool isAnalyzed,
        bool isLocked,
        IEtabsTableServicesFactory tableFactory,
        TableExtractorRegistry registry,
        IParquetService parquet)
    {
        var planned = registry.Entries
            .Where(e => e.FilterSelector(tables) is not null)
            .ToList();

        var queryService = tableFactory.CreateQueryService(app);
        var outcomes = new Dictionary<string, TableExtractionOutcome>();

        try
        {
            foreach (var entry in registry.Entries)
            {
                var filter = entry.FilterSelector(tables);
                if (filter is null)
                {
                    continue;
                }

                Console.Error.WriteLine(
                    $"ℹ [{outcomes.Count + 1}/{planned.Count}] Extracting: {entry.Extractor.Label}");

                TableExtractionOutcome outcome;
                // Result tables require analysis RESULTS (≥1 finished case), not a
                // locked UI state. The lock half of this gate previously skipped
                // extraction when ETABS did not leave the model locked post-run.
                if (entry.Extractor.RequiresAnalysis && !isAnalyzed)
                {
                    outcome = TableExtractionOutcome.Fail(
                        "Model has no analysis results. Run analysis first (run-analysis command).");
                    Console.Error.WriteLine("  ⚠ Skipped — model not analyzed");
                }
                else
                {
                    outcome = await entry.Extractor.ExtractAsync(
                        filter, outputDir, queryService, parquet);
                }

                outcomes[entry.Extractor.Slug] = outcome;
                var status = outcome.Success
                    ? $"✓ {outcome.RowCount} rows → {Path.GetFileName(outcome.OutputFile ?? "(empty)")} ({outcome.ExtractionTimeMs} ms)"
                    : $"✗ FAILED: {outcome.Error}";
                Console.Error.WriteLine($"  {status}");
            }

            return outcomes;
        }
        finally
        {
            await queryService.ResetSelectionAsync();
        }
    }

    internal static async Task<ModelMetadata> CollectModelMetadataAsync(
        ETABSApplication app,
        string filePath,
        UnitSnapshot unitSnapshot)
    {
        await Task.CompletedTask;

        Console.Error.WriteLine("ℹ Collecting model metadata...");
        _warnings = [];
        var groups = ReadGroups(app);

        var metadata = new ModelMetadata
        {
            SchemaVersion = 2,
            FilePath = filePath,
            EtabsVersion = app.FullVersion,
            IsAnalyzed = ReadOrDefault("analysis state", () => app.Model.Analyze.GetCaseStatus().Any(cs => cs.IsFinished), false),
            IsLocked = ReadOrDefault("lock state", () => app.Model.ModelInfo.IsLocked(), false),
            Units = ModelMetadataUnits.FormatDisplay(unitSnapshot.Active),
            LoadPatterns = ReadLoadPatterns(app),
            LoadCases = ReadLoadCases(app),
            LoadCombinations = ReadLoadCombinations(app),
            Stories = ReadStories(app),
            Groups = groups.Names,
            GroupDetails = groups.Details,
            Materials = ReadMaterials(app),
            FrameSections = ReadFrameSections(app),
            AreaSections = ReadAreaSections(app),
            Warnings = _metadataWarnings,
            CollectedAt = DateTimeOffset.UtcNow
        };

        Console.Error.WriteLine(
            $"✓ Metadata collected ({metadata.LoadCases.Count} cases, {metadata.LoadCombinations.Count} combos, {metadata.Stories.Count} stories)");

        return metadata;
    }

    [ThreadStatic]
    private static List<MetadataWarning>? _warnings;

    private static List<MetadataWarning> _metadataWarnings
    {
        get
        {
            _warnings ??= [];
            return _warnings;
        }
    }

    private static int RunSpecificCases(ETABSApplication app)
    {
        app.SapModel.Analyze.CreateAnalysisModel();
        return app.SapModel.Analyze.RunAnalysis();
    }

    private static List<LoadPatternInfo> ReadLoadPatterns(ETABSApplication app)
    {
        try
        {
            var result = new List<LoadPatternInfo>();
            foreach (var name in app.Model.LoadPatterns.GetNameList())
            {
                var loadType = ReadItemOrDefault(
                    "loadPatterns",
                    $"load pattern '{name}' type",
                    () => app.Model.LoadPatterns.GetLoadType(name).ToString(),
                    "Unknown");
                var selfWeight = ReadItemOrDefault(
                    "loadPatterns",
                    $"load pattern '{name}' self-weight multiplier",
                    () => app.Model.LoadPatterns.GetSelfWeightMultiplier(name),
                    0.0);

                result.Add(new LoadPatternInfo(name, loadType, selfWeight));
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("loadPatterns", $"Could not read load patterns: {ex.Message}");
            return [];
        }
    }

    private static List<LoadCaseInfo> ReadLoadCases(ETABSApplication app)
    {
        try
        {
            var result = new List<LoadCaseInfo>();
            foreach (var name in app.Model.LoadCases.GetNameList())
            {
                try
                {
                    var (caseType, _) = app.Model.LoadCases.GetTypeOAPI(name);
                    result.Add(new LoadCaseInfo(name, caseType.ToString()));
                }
                catch (Exception ex)
                {
                    Warn("loadCases", $"Could not read load case '{name}' type: {ex.Message}");
                    result.Add(new LoadCaseInfo(name, "Unknown"));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("loadCases", $"Could not read load cases: {ex.Message}");
            return [];
        }
    }

    private static List<LoadComboInfo> ReadLoadCombinations(ETABSApplication app)
    {
        try
        {
            var result = new List<LoadComboInfo>();
            foreach (var name in app.Model.LoadCombinations.GetNameList())
            {
                var comboType = "Unknown";
                var cases = new List<string>();
                var items = new List<LoadComboItemInfo>();

                try
                {
                    comboType = ComboTypeLabel(app.Model.LoadCombinations.GetComboType(name));
                }
                catch (Exception ex)
                {
                    Warn("loadCombinations", $"Could not read combo '{name}' type: {ex.Message}");
                }

                try
                {
                    var comboItems = app.Model.LoadCombinations.GetCaseList(name);
                    items = comboItems
                        .Where(c => !string.IsNullOrWhiteSpace(c.CaseName))
                        .Select(c => new LoadComboItemInfo(
                            c.CaseName,
                            c.CaseType.ToString(),
                            c.ScaleFactor,
                            c.ModeNumber))
                        .ToList();
                    cases = items
                        .Select(i => i.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Warn("loadCombinations", $"Could not read combo '{name}' cases: {ex.Message}");
                }

                result.Add(new LoadComboInfo(name, comboType, cases, items));
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("loadCombinations", $"Could not read load combinations: {ex.Message}");
            return [];
        }
    }

    private static List<StoryInfo> ReadStories(ETABSApplication app)
    {
        try
        {
            var result = new List<StoryInfo>();
            var stories = app.Model.Story.GetStories();
            for (var i = 0; i < stories.NumberStories; i++)
            {
                result.Add(new StoryInfo(
                    stories.StoryNames[i],
                    stories.StoryElevations[i],
                    SafeArrayValue(stories.StoryHeights, i),
                    SafeArrayValue(stories.IsMasterStory, i),
                    SafeArrayValue(stories.SimilarToStory, i)));
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("stories", $"Could not read rich stories: {ex.Message}");
            return ReadStoryNamesAndElevations(app);
        }
    }

    private static List<StoryInfo> ReadStoryNamesAndElevations(ETABSApplication app)
    {
        try
        {
            var result = new List<StoryInfo>();
            foreach (var name in app.Model.Story.GetNameList())
            {
                try
                {
                    result.Add(new StoryInfo(name, app.Model.Story.GetElevation(name)));
                }
                catch (Exception ex)
                {
                    Warn("stories", $"Could not read story '{name}' elevation: {ex.Message}");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("stories", $"Could not read stories: {ex.Message}");
            return [];
        }
    }

    private static (List<string> Names, List<GroupInfo> Details) ReadGroups(ETABSApplication app)
    {
        try
        {
            var names = app.Model.Groups.GetNameList().ToList();
            var details = names
                .Select(name =>
                {
                    var count = ReadItemOrDefault(
                        "groups",
                        $"group '{name}' assignment count",
                        () => app.Model.Groups.GetAssignmentCount(name),
                        0);
                    return new GroupInfo(name, count);
                })
                .ToList();

            return (names, details);
        }
        catch (Exception ex)
        {
            Warn("groups", $"Could not read groups: {ex.Message}");
            return ([], []);
        }
    }

    private static List<MaterialInfo> ReadMaterials(ETABSApplication app)
    {
        try
        {
            var result = new List<MaterialInfo>();
            foreach (var name in app.Model.Materials.GetNameList())
            {
                result.Add(ReadMaterial(app, name));
            }

            return result;
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read materials: {ex.Message}");
            return [];
        }
    }

    private static MaterialInfo ReadMaterial(ETABSApplication app, string name)
    {
        var materialType = "Unknown";
        string? symType = null;
        string? guid = null;
        double? e = null;
        double? u = null;
        double? a = null;
        double? g = null;
        double? weight = null;
        double? mass = null;
        double? concreteFc = null;
        double? steelFy = null;
        double? steelFu = null;
        double? rebarFy = null;
        double? rebarFu = null;
        eMatType? etabsMaterialType = null;

        try
        {
            var basic = app.Model.Materials.GetMaterial(name);
            materialType = basic.MatType.ToString();
            guid = string.IsNullOrWhiteSpace(basic.GUID) ? null : basic.GUID;
            etabsMaterialType = basic.MatType;
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read material '{name}' basic info: {ex.Message}");
        }

        try
        {
            var type = app.Model.Materials.GetTypeOAPI(name);
            etabsMaterialType = type.MatType;
            materialType = type.MatType.ToString();
            symType = type.SymType.ToString();
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read material '{name}' OAPI type: {ex.Message}");
        }

        try
        {
            var iso = app.Model.Materials.GetMPIsotropic(name);
            e = iso.E;
            u = iso.U;
            a = iso.A;
            g = iso.G;
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read material '{name}' isotropic properties: {ex.Message}");
        }

        try
        {
            var wm = app.Model.Materials.GetWeightAndMass(name);
            weight = wm.W;
            mass = wm.M;
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read material '{name}' weight/mass: {ex.Message}");
        }

        try
        {
            switch (etabsMaterialType)
            {
                case eMatType.Concrete:
                    concreteFc = app.Model.Materials.GetConcreteMaterial(name).Fc;
                    break;
                case eMatType.Steel:
                    var steel = app.Model.Materials.GetSteelMaterial(name);
                    steelFy = steel.Fy;
                    steelFu = steel.Fu;
                    break;
                case eMatType.Rebar:
                    var rebar = app.Model.Materials.GetRebarMaterial(name);
                    rebarFy = rebar.Fy;
                    rebarFu = rebar.Fu;
                    break;
            }
        }
        catch (Exception ex)
        {
            Warn("materials", $"Could not read material '{name}' design properties: {ex.Message}");
        }

        return new MaterialInfo
        {
            Name = name,
            MaterialType = materialType,
            SymType = symType,
            MaterialGuid = guid,
            ElasticModulus = e,
            PoissonRatio = u,
            ThermalExpansion = a,
            ShearModulus = g,
            WeightPerVolume = weight,
            MassPerVolume = mass,
            ConcreteFc = concreteFc,
            SteelFy = steelFy,
            SteelFu = steelFu,
            RebarFy = rebarFy,
            RebarFu = rebarFu
        };
    }

    private static List<FrameSectionInfo> ReadFrameSections(ETABSApplication app)
    {
        try
        {
            return app.Model.PropFrame.GetNameList()
                .Select(name =>
                {
                    var sectionType = ReadItemOrDefault(
                        "frameSections",
                        $"frame section '{name}' type",
                        () => app.Model.PropFrame.GetSectionType(name).ToString(),
                        "Unknown");
                    return new FrameSectionInfo(name, sectionType);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Warn("frameSections", $"Could not read frame sections: {ex.Message}");
            return [];
        }
    }

    private static List<AreaSectionInfo> ReadAreaSections(ETABSApplication app)
    {
        try
        {
            return app.Model.PropArea.GetNameList()
                .Select(name =>
                {
                    var propertyType = ReadItemOrDefault(
                        "areaSections",
                        $"area section '{name}' type",
                        () => app.Model.PropArea.GetPropertyType(name).ToString(),
                        "Unknown");
                    return new AreaSectionInfo(name, propertyType);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Warn("areaSections", $"Could not read area sections: {ex.Message}");
            return [];
        }
    }

    private static T ReadOrDefault<T>(string label, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Warn(label, $"Could not read {label}: {ex.Message}");
            return fallback;
        }
    }

    private static T ReadItemOrDefault<T>(string category, string label, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Warn(category, $"Could not read {label}: {ex.Message}");
            return fallback;
        }
    }

    private static void Warn(string category, string message)
    {
        Console.Error.WriteLine($"⚠ {message}");
        _metadataWarnings.Add(new MetadataWarning(category, message));
    }

    private static T? SafeArrayValue<T>(T[]? values, int index) =>
        values is not null && index >= 0 && index < values.Length
            ? values[index]
            : default;

    private static string ComboTypeLabel(int comboType) => comboType switch
    {
        0 => "Linear Add",
        1 => "Envelope",
        2 => "Absolute Add",
        3 => "SRSS",
        4 => "Range Add",
        _ => $"Unknown ({comboType})"
    };
}
