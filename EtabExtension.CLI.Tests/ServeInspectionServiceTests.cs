using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EtabExtension.CLI.Features.Serve.Inspection;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using ETABSv1;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class ServeInspectionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-inspection-tests", Guid.NewGuid().ToString("N"));
    private readonly ServeInspectionService _service = new();

    [Fact]
    public void GetModelStateReturnsFingerprintAnalysisUnitsAndManagedIdentity()
    {
        Directory.CreateDirectory(_directory);
        var modelPath = Path.Combine(_directory, "study.edb");
        File.WriteAllBytes(modelPath, Encoding.UTF8.GetBytes("saved-edb"));
        var api = new FakeInspectionApi
        {
            CurrentUnits = eUnits.kip_ft_F,
            ModelPath = modelPath,
            IsLocked = true,
            CaseNames = ["DEAD", "MODAL", "WIND"],
            CaseStatuses = [4, 1, 4]
        };
        var launchId = Guid.NewGuid();
        var identity = new ManagedEtabsSessionRecord(
            1,
            42,
            new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero),
            @"C:\ETABS\ETABS.exe",
            launchId,
            DateTimeOffset.UtcNow);

        var result = _service.GetModelState(api, identity);

        Assert.True(result.Success);
        Assert.Equal(modelPath, result.Data!.ModelPath);
        Assert.Equal(new InspectionUnitData("kip_ft_F", 4), result.Data.PresentUnits);
        Assert.Equal(new InspectionUnitData("kN_m_C", 6), result.Data.ExecutionUnits);
        Assert.True(result.Data.IsLocked);
        Assert.True(result.Data.AnalysisResults.HasResults);
        Assert.Equal(3, result.Data.AnalysisResults.CaseCount);
        Assert.Equal(2, result.Data.AnalysisResults.FinishedCaseCount);
        Assert.Equal(
            $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant()}",
            result.Data.SavedFileFingerprint);
        Assert.Equal(42, result.Data.Identity.Pid);
        Assert.Equal(launchId, result.Data.Identity.ManagedLaunchRecordId);
        Assert.Equal([eUnits.kN_m_C, eUnits.kip_ft_F], api.SetUnitsCalls);
        Assert.Equal(eUnits.kip_ft_F, api.CurrentUnits);
    }

    [Fact]
    public void InspectWallPropertyReturnsCompleteNormalizedDefinition()
    {
        var api = new FakeInspectionApi
        {
            Wall = new RawWallProperty(
                eWallPropType.Specified,
                eShellType.ShellThick,
                "C60",
                1.5,
                123,
                "tier one",
                "wall-guid"),
            Modifiers = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0],
            ShellDesign = new RawShellDesign("A615Gr60", 2, 0.05, 0.06, 0.07, 0.08)
        };

        var result = _service.InspectWallProperty(api, "Tier1_W1500_C60");

        Assert.True(result.Success);
        Assert.Equal("Specified", result.Data!.WallPropType);
        Assert.Equal("ShellThick", result.Data.ShellType);
        Assert.Equal("C60", result.Data.MaterialProperty);
        Assert.Equal(1.5, result.Data.Thickness);
        Assert.Equal(Enumerable.Range(1, 10).Select(value => value / 10d), result.Data.Modifiers);
        Assert.Equal("A615Gr60", result.Data.ShellDesign.MaterialProperty);
        Assert.Equal(0.08, result.Data.ShellDesign.DesignCoverBotDir2);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        Assert.Equal("Tier1_W1500_C60", json.RootElement.GetProperty("name").GetString());
        Assert.Equal(10, json.RootElement.GetProperty("modifiers").GetArrayLength());
        Assert.Equal("kN_m_C", json.RootElement.GetProperty("executionUnits").GetProperty("name").GetString());
    }

    /// <summary>
    /// A refused GetWall still restores the caller's units and still skips the rest of the
    /// read - that half is unchanged. What changed with CLI #28 is the ANSWER: the raw
    /// CSI return code is gone, replaced by a coded reason. A caller could not act on
    /// "ret=17"; it can act on "that name is not a wall".
    /// </summary>
    [Fact]
    public void ARefusedGetWallIsReportedByCodeAndUnitsAreStillRestored()
    {
        var api = new FakeInspectionApi
        {
            CurrentUnits = eUnits.lb_in_F,
            GetWallReturnCode = 17
        };

        var result = _service.InspectWallProperty(api, "MissingOrBroken");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyNotAWall,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ret=17", result.Error, StringComparison.Ordinal);
        Assert.Equal([eUnits.kN_m_C, eUnits.lb_in_F], api.SetUnitsCalls);
        Assert.Equal(eUnits.lb_in_F, api.CurrentUnits);
        Assert.Equal(0, api.GetModifiersCalls);
    }

    /// <summary>
    /// CLI #28. The shell list is not the wall list.
    ///
    /// <para>ETABS returns walls, slabs, mats and decks from
    /// <c>GetNameList(PropType=1)</c> because PropType 1 means SHELL - the enum has no wall
    /// value. Advertising all of them as wall properties is what made 10 of the sanctioned
    /// model's 17 listed names unusable.</para>
    /// </summary>
    [Fact]
    public void ListWallPropertiesReturnsOnlyGenuineWalls()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["S8_C5.75-PT", "W20_C6", "Deck1", "M6ft_C5.0", "W40_C8"],
            WallNames = new(StringComparer.Ordinal) { "W20_C6", "W40_C8" }
        };

        var result = _service.ListWallProperties(api);

        Assert.True(result.Success);
        Assert.Equal(["W20_C6", "W40_C8"], result.Data!.Names);
        Assert.Equal(1, api.GetShellPropertyNamesCalls);
    }

    /// <summary>
    /// THE property the pair exists for, stated as an executable assertion: everything the
    /// listing advertises can actually be inspected.
    ///
    /// <para>This is the test that would have failed on the shipped candidate. It walks the
    /// listing's own output through the paired command, so the two cannot drift apart again
    /// without a red test - whatever the filter is later reimplemented as.</para>
    /// </summary>
    [Fact]
    public void EveryListedWallPropertyCanBeInspected()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames =
            [
                "S8_C5.75-PT", "S12_C5.75_RC", "M12ft_C5.0", "Deck1",
                "W20_C6", "W40_C8", "W30_C8", "FNW12_C5"
            ],
            WallNames = new(StringComparer.Ordinal)
            {
                "W20_C6", "W40_C8", "W30_C8", "FNW12_C5"
            }
        };

        var listed = _service.ListWallProperties(api);
        Assert.True(listed.Success);
        Assert.NotEmpty(listed.Data!.Names);

        foreach (var name in listed.Data.Names)
        {
            var inspected = _service.InspectWallProperty(api, name);
            Assert.True(
                inspected.Success,
                $"listed '{name}' but could not inspect it: {inspected.Error}");
            Assert.Equal(name, inspected.Data!.Name);
        }
    }

    /// <summary>
    /// A property the model really has, which simply is not a wall. The caller needs to know
    /// that is what happened - not a CSI return code, which cannot be acted on.
    /// </summary>
    [Fact]
    public void InspectingANonWallAreaPropertySaysSoInsteadOfLeakingAReturnCode()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["S8_C5.75-PT", "W20_C6"],
            WallNames = new(StringComparer.Ordinal) { "W20_C6" },
            DefinedAreaProperties = new(StringComparer.Ordinal) { "S8_C5.75-PT", "W20_C6" }
        };

        var result = _service.InspectWallProperty(api, "S8_C5.75-PT");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyNotAWall,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("name=S8_C5.75-PT", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ret=", result.Error, StringComparison.Ordinal);
    }

    /// <summary>And a name the model does not define at all is a different answer.</summary>
    [Fact]
    public void InspectingAnUnknownAreaPropertyIsDistinctFromInspectingANonWall()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["W20_C6"],
            WallNames = new(StringComparer.Ordinal) { "W20_C6" },
            DefinedAreaProperties = new(StringComparer.Ordinal) { "W20_C6" }
        };

        var result = _service.InspectWallProperty(api, "W20_C7");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyNotFound,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotAWall,
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The filter is ETABS's own accessor, never the name. A model whose walls are not
    /// called "W..." must still get its walls listed.
    /// </summary>
    [Fact]
    public void WallnessIsDecidedByTheApiAndNotByNamingConvention()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["CoreShaft-300", "W20_C6", "Podium_Perimeter"],
            WallNames = new(StringComparer.Ordinal) { "CoreShaft-300", "Podium_Perimeter" }
        };

        var result = _service.ListWallProperties(api);

        Assert.True(result.Success);
        Assert.Equal(["CoreShaft-300", "Podium_Perimeter"], result.Data!.Names);
    }

    [Fact]
    public void ResolveAreaTargetsFiltersOnlyBySourcePropertyWithoutStoryFiltering()
    {
        var api = new FakeInspectionApi
        {
            AreaNames = ["A-L1", "A-UNASSIGNED", "A-L99", "A-OTHER"]
        };
        api.Areas["A-L1"] = new("Tier1_W1500_C60", "L1", "P1", "guid-1", eAreaDesignOrientation.Wall);
        api.Areas["A-UNASSIGNED"] = new("None", "L1", "None", "guid-2", eAreaDesignOrientation.Wall);
        api.Areas["A-L99"] = new("Tier1_W1500_C60", "L99", "P9", "guid-3", eAreaDesignOrientation.Wall);
        api.Areas["A-OTHER"] = new("OtherWall", "L5", "P5", "guid-4", eAreaDesignOrientation.Wall);

        var result = _service.ResolveAreaTargets(api, "Tier1_W1500_C60");

        Assert.True(result.Success);
        Assert.Equal(["A-L1", "A-L99"], result.Data!.Targets.Select(target => target.Name));
        Assert.Equal(["L1", "L99"], result.Data.Targets.Select(target => target.Story));
        Assert.Equal(["P1", "P9"], result.Data.Targets.Select(target => target.Pier));
        Assert.All(result.Data.Targets, target => Assert.Equal("Wall", target.DesignOrientation));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record FakeArea(
        string Property,
        string Story,
        string Pier,
        string GlobalId,
        eAreaDesignOrientation Orientation);

    private sealed class FakeInspectionApi : IEtabsInspectionApi
    {
        public eUnits CurrentUnits { get; set; } = eUnits.kip_ft_F;
        public List<eUnits> SetUnitsCalls { get; } = [];
        public string ModelPath { get; init; } = string.Empty;
        public bool IsLocked { get; init; }
        public string[] CaseNames { get; init; } = [];
        public int[] CaseStatuses { get; init; } = [];
        public int GetWallReturnCode { get; init; }
        public RawWallProperty Wall { get; init; } = new(
            eWallPropType.Specified,
            eShellType.ShellThin,
            "C40",
            0.2,
            0,
            string.Empty,
            string.Empty);
        public double[] Modifiers { get; init; } = Enumerable.Repeat(1d, 10).ToArray();
        public RawShellDesign ShellDesign { get; init; } = new("Rebar", 0, 0, 0, 0, 0);
        public string[] ShellPropertyNames { get; init; } = [];
        public int GetShellPropertyNamesCalls { get; private set; }
        public int GetModifiersCalls { get; private set; }
        public string[] AreaNames { get; init; } = [];
        public Dictionary<string, FakeArea> Areas { get; } = new(StringComparer.Ordinal);

        public eUnits GetPresentUnits() => CurrentUnits;

        public int SetPresentUnits(eUnits units)
        {
            SetUnitsCalls.Add(units);
            CurrentUnits = units;
            return 0;
        }

        public string GetModelFilename() => ModelPath;
        public bool GetModelIsLocked() => IsLocked;

        public int GetCaseStatus(out string[] caseNames, out int[] statuses)
        {
            caseNames = CaseNames;
            statuses = CaseStatuses;
            return 0;
        }

        /// <summary>
        /// The names <c>cPropArea.GetWall</c> accepts. Everything else in
        /// <see cref="ShellPropertyNames"/> is a slab, mat or deck, exactly as a real
        /// model's shell list is - which is the situation CLI #28 exists for.
        /// </summary>
        public HashSet<string> WallNames { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Area property names the model defines at all, wall or not.</summary>
        public HashSet<string> DefinedAreaProperties { get; init; } = new(StringComparer.Ordinal);

        public List<string> GetWallCalls { get; } = [];

        public int GetWall(string name, out RawWallProperty property)
        {
            GetWallCalls.Add(name);
            property = Wall;
            if (WallNames.Count > 0)
            {
                return WallNames.Contains(name) ? 0 : 1;
            }

            return GetWallReturnCode;
        }

        public int GetAreaPropertyType(string name, out int propertyType)
        {
            propertyType = 1;
            if (DefinedAreaProperties.Count > 0)
            {
                return DefinedAreaProperties.Contains(name) ? 0 : 1;
            }

            return 0;
        }

        public int GetModifiers(string name, out double[] modifiers)
        {
            GetModifiersCalls++;
            modifiers = Modifiers;
            return 0;
        }

        public int GetShellDesign(string name, out RawShellDesign shellDesign)
        {
            shellDesign = ShellDesign;
            return 0;
        }

        public int GetShellPropertyNames(out string[] names)
        {
            GetShellPropertyNamesCalls++;
            names = ShellPropertyNames;
            return 0;
        }

        public int GetAreaNames(out string[] names)
        {
            names = AreaNames;
            return 0;
        }

        public int GetAreaProperty(string name, out string propertyName)
        {
            propertyName = Areas[name].Property;
            return 0;
        }

        public int GetAreaLabelAndStory(string name, out string label, out string story)
        {
            label = name;
            story = Areas[name].Story;
            return 0;
        }

        public int GetAreaPier(string name, out string pier)
        {
            pier = Areas[name].Pier;
            return 0;
        }

        public int GetAreaGuid(string name, out string globalId)
        {
            globalId = Areas[name].GlobalId;
            return 0;
        }

        public int GetAreaDesignOrientation(string name, out eAreaDesignOrientation orientation)
        {
            orientation = Areas[name].Orientation;
            return 0;
        }
    }
}
