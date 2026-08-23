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
    /// read - that half is unchanged.
    ///
    /// <para>The ANSWER is the part that had to be repaired twice. First the raw CSI return
    /// code was replaced by a coded reason; then the coded reason had to stop being invented
    /// from a failure. Here CSI refuses everything - GetWall, the slab and deck probes, and
    /// the census - so nothing about the model has been established, and the only honest
    /// answer is that the classification failed. Not "not a wall". Not "not found".</para>
    /// </summary>
    [Fact]
    public void WhenEveryProbeIsRefusedTheAnswerIsAnInfrastructureFailureNotAVerdict()
    {
        var api = new FakeInspectionApi
        {
            CurrentUnits = eUnits.lb_in_F,
            GetWallReturnCode = 17,
            AreaPropertyCensusReturnCode = 9
        };

        var result = _service.InspectWallProperty(api, "MissingOrBroken");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotAWall,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotFound,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ret=17", result.Error, StringComparison.Ordinal);
        Assert.Equal([eUnits.kN_m_C, eUnits.lb_in_F], api.SetUnitsCalls);
        Assert.Equal(eUnits.lb_in_F, api.CurrentUnits);
        Assert.Equal(0, api.GetModifiersCalls);
    }

    /// <summary>
    /// THE listing hazard the re-review named: a genuine wall whose probe glitches must not
    /// quietly vanish from a successful-looking wall list.
    ///
    /// <para>An incomplete list is worse than a failed one, because nothing downstream can
    /// tell it is incomplete. The engineer would simply not see that wall and have no reason
    /// to suspect it exists.</para>
    /// </summary>
    [Fact]
    public void AShellPropertyThatCannotBeClassifiedFailsTheListingInsteadOfBeingDropped()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["W20_C6", "W40_C8", "W30_C8"],
            // W40_C8 is a wall in the model, but every probe on it is refused.
            WallNames = new(StringComparer.Ordinal) { "W20_C6", "W30_C8" },
            AreaPropertyCensusReturnCode = 9
        };

        var result = _service.ListWallProperties(api);

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("name=W40_C8", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the existence half: a property the model really defines, whose probes all fail,
    /// must not be reported as absent. "I could not ask" is not "it is not there".
    /// </summary>
    [Fact]
    public void AnExistingPropertyWhoseProbesAllFailIsNotReportedAsMissing()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["Unclassifiable"],
            DefinedAreaProperties = new(StringComparer.Ordinal) { "Unclassifiable" },
            GetWallReturnCode = 1
        };

        var result = _service.InspectWallProperty(api, "Unclassifiable");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotFound,
            result.Error,
            StringComparison.Ordinal);

        // And the diagnostic must not claim the census failed - it succeeded, and said
        // this property exists. A failure message that misstates what happened during the
        // run is the same defect class as a probe failure reported as a verdict.
        Assert.DoesNotContain(
            "census did not succeed",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "census succeeded",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The other indeterminate case, so the two cannot collapse into one wording again:
    /// here the census really did fail, and the diagnostic is allowed - required - to say
    /// so.
    /// </summary>
    [Fact]
    public void WhenTheCensusItselfFailedTheDiagnosticSaysThatAndNotThatThePropertyExists()
    {
        var api = new FakeInspectionApi
        {
            GetWallReturnCode = 1,
            AreaPropertyCensusReturnCode = 9
        };

        var result = _service.InspectWallProperty(api, "Unknowable");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "census did not succeed",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "census succeeded",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The transient-probe path. GetWall refuses, the diagnosis re-probes, and this time it
    /// succeeds.
    ///
    /// <para>Reporting that as a wall would be answering from evidence the first probe
    /// contradicts; reporting it as "every probe failed and the census failed too" would be
    /// describing a run that did not happen - the census was never even reached. Both are
    /// false accounts, so the answer is that the evidence contradicted itself.</para>
    /// </summary>
    [Fact]
    public void AProbeThatRefusesThenSucceedsIsReportedAsContradictoryNotAsAWall()
    {
        var api = new FakeInspectionApi { GetWallRefusesOnlyOnce = true };

        var result = _service.InspectWallProperty(api, "Flickering");

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("inconsistent answers", result.Error, StringComparison.Ordinal);

        // It must not claim probes and census failed - the census was never reached.
        Assert.DoesNotContain(
            "census did not succeed",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "refused every classification probe",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The disagreeing-census path. The shell census advertised this name; the
    /// all-properties census then succeeded and denied it exists.
    ///
    /// <para>The listing must not report that as "the census did not succeed" - it did - and
    /// must not report NOT_FOUND for a name ETABS itself just handed over. It reports the
    /// disagreement.</para>
    /// </summary>
    [Fact]
    public void CensusesThatDisagreeAreReportedAsInconsistentEvidence()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["W20_C6", "Ghost"],
            WallNames = new(StringComparer.Ordinal) { "W20_C6" },
            CensusesDisagree = true
        };

        var result = _service.ListWallProperties(api);

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("name=Ghost", result.Error, StringComparison.Ordinal);
        Assert.Contains("disagree", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "census did not succeed",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotFound,
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The listing's diagnostic is held to the same standard: the shell it could not
    /// classify demonstrably exists, so the message must not blame the census.
    /// </summary>
    [Fact]
    public void TheListingFailureAlsoDescribesTheEvidenceItActuallyHad()
    {
        var api = new FakeInspectionApi
        {
            ShellPropertyNames = ["W20_C6", "Oddity"],
            WallNames = new(StringComparer.Ordinal) { "W20_C6" },
            DefinedAreaProperties = new(StringComparer.Ordinal) { "W20_C6", "Oddity" }
        };

        var result = _service.ListWallProperties(api);

        Assert.False(result.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains("name=Oddity", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "census did not succeed",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// NOT_FOUND requires a census that SUCCEEDED and does not contain the name. A census
    /// that failed proves nothing, and must not be spent as proof of absence.
    /// </summary>
    [Fact]
    public void NotFoundRequiresASuccessfulCensusThatOmitsTheName()
    {
        var proven = new FakeInspectionApi
        {
            DefinedAreaProperties = new(StringComparer.Ordinal) { "W20_C6" },
            GetWallReturnCode = 1
        };
        var provenResult = _service.InspectWallProperty(proven, "NoSuchProperty");
        Assert.False(provenResult.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyNotFound,
            provenResult.Error,
            StringComparison.Ordinal);

        var unproven = new FakeInspectionApi
        {
            DefinedAreaProperties = new(StringComparer.Ordinal) { "W20_C6" },
            GetWallReturnCode = 1,
            AreaPropertyCensusReturnCode = 9
        };
        var unprovenResult = _service.InspectWallProperty(unproven, "NoSuchProperty");
        Assert.False(unprovenResult.Success);
        Assert.Contains(
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            unprovenResult.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InspectionErrorCodes.AreaPropertyNotFound,
            unprovenResult.Error,
            StringComparison.Ordinal);
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
            WallNames = new(StringComparer.Ordinal) { "W20_C6", "W40_C8" },
            SlabNames = new(StringComparer.Ordinal) { "S8_C5.75-PT", "M6ft_C5.0" },
            DeckNames = new(StringComparer.Ordinal) { "Deck1" }
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
            },
            SlabNames = new(StringComparer.Ordinal)
            {
                "S8_C5.75-PT", "S12_C5.75_RC", "M12ft_C5.0"
            },
            DeckNames = new(StringComparer.Ordinal) { "Deck1" }
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
            SlabNames = new(StringComparer.Ordinal) { "S8_C5.75-PT" },
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
            // The census succeeds and does not contain W20_C7 — positive evidence of absence.
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
            WallNames = new(StringComparer.Ordinal) { "CoreShaft-300", "Podium_Perimeter" },
            // A "W..." name that is actually a slab, so the test fails if anyone ever
            // reintroduces a naming heuristic in either direction.
            SlabNames = new(StringComparer.Ordinal) { "W20_C6" }
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

        /// <summary>
        /// Makes the FIRST GetWall refuse and every later one accept, reproducing a
        /// transient CSI failure inside a single request.
        /// </summary>
        public bool GetWallRefusesOnlyOnce { get; init; }

        public int GetWall(string name, out RawWallProperty property)
        {
            GetWallCalls.Add(name);
            property = Wall;
            if (GetWallRefusesOnlyOnce)
            {
                return GetWallCalls.Count == 1 ? 1 : 0;
            }

            if (WallNames.Count > 0)
            {
                return WallNames.Contains(name) ? 0 : 1;
            }

            return GetWallReturnCode;
        }

        /// <summary>
        /// Lets the two censuses disagree: the SHELL list advertises a name that the
        /// ALL-properties list then denies. One of them is wrong and the service cannot
        /// know which.
        /// </summary>
        public bool CensusesDisagree { get; init; }

        /// <summary>Names cPropArea.GetSlab accepts.</summary>
        public HashSet<string> SlabNames { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Names cPropArea.GetDeck accepts.</summary>
        public HashSet<string> DeckNames { get; init; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Lets a test make the existence census itself fail, which is the case that must
        /// never be readable as "the property does not exist".
        /// </summary>
        public int AreaPropertyCensusReturnCode { get; init; }

        public int ProbeSlab(string name) => SlabNames.Contains(name) ? 0 : 1;

        public int ProbeDeck(string name) => DeckNames.Contains(name) ? 0 : 1;

        public int GetAllAreaPropertyNames(out string[] names)
        {
            names = CensusesDisagree ? [] : [.. DefinedAreaProperties];
            return AreaPropertyCensusReturnCode;
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
