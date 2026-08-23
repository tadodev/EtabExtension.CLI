using System.Security.Cryptography;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using ETABSv1;

namespace EtabExtension.CLI.Features.Serve.Inspection;

public interface IServeInspectionService
{
    Result<GetModelStateData> GetModelState(
        IEtabsInspectionApi api,
        ManagedEtabsSessionRecord? identity);
    Result<ListWallPropertiesData> ListWallProperties(IEtabsInspectionApi api);
    Result<InspectWallPropertyData> InspectWallProperty(IEtabsInspectionApi api, string name);
    Result<ResolveAreaTargetsData> ResolveAreaTargets(IEtabsInspectionApi api, string sourceProperty);
}

public sealed class ServeInspectionService : IServeInspectionService
{
    private const eUnits ExecutionUnits = eUnits.kN_m_C;

    public Result<GetModelStateData> GetModelState(
        IEtabsInspectionApi api,
        ManagedEtabsSessionRecord? identity) => Execute(api, units =>
    {
        if (identity is null)
        {
            throw new InvalidOperationException(
                "Managed ETABS session record is unavailable; model identity cannot be established.");
        }

        var modelPath = api.GetModelFilename();
        var ret = api.GetCaseStatus(out var caseNames, out var statuses);
        RequireSuccess("cAnalyze.GetCaseStatus", ret);
        var finishedCaseCount = statuses.Count(status => status == 4);

        return new GetModelStateData(
            modelPath,
            units.Original,
            units.Original,
            units.Execution,
            api.GetModelIsLocked(),
            new AnalysisResultsStateData(
                finishedCaseCount > 0,
                Math.Max(caseNames.Length, statuses.Length),
                finishedCaseCount),
            FingerprintSavedFile(modelPath),
            new ManagedIdentityData(
                identity.SchemaVersion,
                identity.Pid,
                identity.ProcessStartTimeUtc,
                identity.ExecutablePath,
                identity.ManagedLaunchRecordId));
    });

    /// <summary>
    /// The wall properties of the model - and nothing else.
    ///
    /// <para><b>CLI #28.</b> This used to return whatever
    /// <c>cPropArea.GetNameList(PropType=1)</c> gave it and call the result "wall
    /// properties". PropType 1 is SHELL in the ETABS API (0=All, 1=Shell, 2=Plane,
    /// 3=Asolid); there is no wall value, so walls, slabs, mats and decks all came back
    /// together. In the sanctioned model that was 17 names of which only 7 were walls, and
    /// the other 10 failed the moment a caller passed them to
    /// <see cref="InspectWallProperty"/> - the paired command they were listed for.</para>
    ///
    /// <para><b>How wallness is decided.</b> ETABS has no call that labels a shell property
    /// as wall, slab or deck; the classification is expressed by WHICH typed accessor
    /// accepts the name. So the filter is <c>cPropArea.GetWall</c> itself - the API's own
    /// wall accessor, and the exact predicate the inspector uses. Listing and inspecting
    /// cannot disagree, because they are the same question asked once each.</para>
    ///
    /// <para>Deliberately NOT inferred from names. "W..." and "S..." are one project's
    /// convention, not a property of ETABS, and a model that names its walls differently
    /// would silently lose them.</para>
    /// </summary>
    public Result<ListWallPropertiesData> ListWallProperties(IEtabsInspectionApi api) =>
        Execute(api, units =>
        {
            var ret = api.GetShellPropertyNames(out var shellNames);
            RequireSuccess("cPropArea.GetNameList(PropType=1 Shell)", ret);

            var walls = new List<string>(shellNames.Length);
            foreach (var name in shellNames)
            {
                var classification = Classify(api, name);
                switch (classification)
                {
                    case AreaPropertyClass.Wall:
                        walls.Add(name);
                        break;
                    case AreaPropertyClass.NotAWall:
                        break;
                    default:
                        // A property nobody could classify must not be quietly dropped. The
                        // caller asked for THE wall properties; answering with a list that
                        // silently omits a wall whose probe failed is the same class of lie
                        // as answering with slabs in it.
                        throw Indeterminate(name, classification);
                }
            }

            return new ListWallPropertiesData(walls, units.Original, units.Execution);
        });

    public Result<InspectWallPropertyData> InspectWallProperty(
        IEtabsInspectionApi api,
        string name) => Execute(api, units =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var ret = api.GetWall(name, out var wall);
        if (ret != 0)
        {
            // CLI #28: say WHICH way it went wrong. "cPropArea.GetWall failed (ret=1)" is a
            // CSI return code, and a caller cannot tell from it whether it typed the name
            // wrong or asked about a slab. Those need different fixes, so they get
            // different answers - but only when the evidence actually supports one.
            throw Diagnose(api, name);
        }

        ret = api.GetModifiers(name, out var modifiers);
        RequireSuccess("cPropArea.GetModifiers", ret);
        if (modifiers.Length != 10)
        {
            throw new InvalidOperationException(
                $"cPropArea.GetModifiers returned {modifiers.Length} values; expected 10.");
        }

        ret = api.GetShellDesign(name, out var shellDesign);
        RequireSuccess("cPropArea.GetShellDesign", ret);

        return new InspectWallPropertyData(
            name,
            wall.WallPropType.ToString(),
            wall.ShellType.ToString(),
            wall.MaterialProperty,
            wall.Thickness,
            wall.Color,
            wall.Notes,
            wall.GlobalId,
            modifiers,
            new WallShellDesignData(
                shellDesign.MaterialProperty,
                shellDesign.SteelLayoutOption,
                shellDesign.DesignCoverTopDir1,
                shellDesign.DesignCoverTopDir2,
                shellDesign.DesignCoverBotDir1,
                shellDesign.DesignCoverBotDir2),
            units.Original,
            units.Execution);
    });

    public Result<ResolveAreaTargetsData> ResolveAreaTargets(
        IEtabsInspectionApi api,
        string sourceProperty) => Execute(api, units =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProperty);

        var ret = api.GetAreaNames(out var areaNames);
        RequireSuccess("cAreaObj.GetNameList", ret);
        var targets = new List<ResolvedAreaTargetData>();

        foreach (var name in areaNames)
        {
            ret = api.GetAreaProperty(name, out var propertyName);
            RequireSuccess($"cAreaObj.GetProperty('{name}')", ret);
            if (!string.Equals(propertyName, sourceProperty, StringComparison.Ordinal))
            {
                continue;
            }

            ret = api.GetAreaLabelAndStory(name, out _, out var story);
            RequireSuccess($"cAreaObj.GetLabelFromName('{name}')", ret);
            ret = api.GetAreaPier(name, out var pier);
            RequireSuccess($"cAreaObj.GetPier('{name}')", ret);
            ret = api.GetAreaGuid(name, out var globalId);
            RequireSuccess($"cAreaObj.GetGUID('{name}')", ret);
            ret = api.GetAreaDesignOrientation(name, out var orientation);
            RequireSuccess($"cAreaObj.GetDesignOrientation('{name}')", ret);

            targets.Add(new ResolvedAreaTargetData(
                name,
                story,
                pier,
                globalId,
                orientation.ToString()));
        }

        return new ResolveAreaTargetsData(
            sourceProperty,
            targets,
            units.Original,
            units.Execution);
    });

    private static Result<T> Execute<T>(
        IEtabsInspectionApi api,
        Func<UnitAudit, T> read)
    {
        try
        {
            return Result.Ok(WithPinnedUnits(api, read));
        }
        catch (Exception ex)
        {
            return Result.Fail<T>(ex.Message);
        }
    }

    private static T WithPinnedUnits<T>(IEtabsInspectionApi api, Func<UnitAudit, T> read)
    {
        var original = api.GetPresentUnits();
        if ((int)original == 0)
        {
            throw new InvalidOperationException("cSapModel.GetPresentUnits failed (returned 0).");
        }

        try
        {
            RequireSuccess(
                $"cSapModel.SetPresentUnits({ExecutionUnits})",
                api.SetPresentUnits(ExecutionUnits));
            return read(new UnitAudit(ToUnitData(original), ToUnitData(ExecutionUnits)));
        }
        finally
        {
            RequireSuccess(
                $"cSapModel.SetPresentUnits({original}) restore",
                api.SetPresentUnits(original));
        }
    }

    private static InspectionUnitData ToUnitData(eUnits units) => new(units.ToString(), (int)units);

    /// <summary>What the model can be shown to say about one area property.</summary>
    private enum AreaPropertyClass
    {
        /// <summary>
        /// The evidence itself could not be gathered: every typed accessor refused AND the
        /// property census failed. Nothing is known about the model. Never an answer;
        /// always a failure.
        /// </summary>
        IndeterminateNoEvidence,

        /// <summary>
        /// The census SUCCEEDED and lists this property, but no typed accessor claimed it.
        /// The property demonstrably exists; only its kind is unestablished — an ETABS
        /// shell property outside the wall/slab/deck accessors this build probes. Also
        /// never an answer, but a materially different situation from having no evidence,
        /// and the diagnostic must not confuse the two.
        /// </summary>
        IndeterminateUnclassified,

        /// <summary><c>cPropArea.GetWall</c> accepted it.</summary>
        Wall,

        /// <summary>A different typed accessor accepted it - slab or deck.</summary>
        NotAWall,

        /// <summary>A successful census of every area property does not contain the name.</summary>
        NotDefined
    }

    /// <summary>
    /// Classifies one area property from POSITIVE evidence only.
    ///
    /// <para>The distinction this method exists for: a nonzero CSI status is a probe that
    /// FAILED, not a fact about the model. Reading "GetWall returned 1" as "this is not a
    /// wall" silently converts an infrastructure failure into a semantic answer - a wall
    /// whose probe glitched would vanish from a successful-looking wall list, which is the
    /// unknown-as-clean conversion the Alpha contract refuses everywhere else.</para>
    ///
    /// <para>So each verdict needs its own affirmative evidence: another typed accessor
    /// accepting the name for <see cref="AreaPropertyClass.NotAWall"/>, and a census that
    /// itself succeeded for <see cref="AreaPropertyClass.NotDefined"/>. Anything less is
    /// <see cref="AreaPropertyClass.Indeterminate"/> and becomes a bounded failure.</para>
    /// </summary>
    private static AreaPropertyClass Classify(IEtabsInspectionApi api, string name)
    {
        if (api.GetWall(name, out _) == 0)
        {
            return AreaPropertyClass.Wall;
        }

        // Positive classification as something else. ETABS has no call that returns a
        // property's kind as a label, so the kind IS which accessor accepts it.
        if (api.ProbeSlab(name) == 0 || api.ProbeDeck(name) == 0)
        {
            return AreaPropertyClass.NotAWall;
        }

        // Absence has to be proven by a census that succeeded, not inferred from a probe
        // that did not.
        if (api.GetAllAreaPropertyNames(out var defined) == 0)
        {
            return defined.Contains(name, StringComparer.Ordinal)
                ? AreaPropertyClass.IndeterminateUnclassified
                : AreaPropertyClass.NotDefined;
        }

        return AreaPropertyClass.IndeterminateNoEvidence;
    }

    /// <summary>Turns a refused <c>GetWall</c> into the reason the evidence supports.</summary>
    private static InvalidOperationException Diagnose(IEtabsInspectionApi api, string name) =>
        Classify(api, name) switch
        {
            AreaPropertyClass.NotAWall => new InvalidOperationException(string.Join(
                "; ",
                InspectionErrorCodes.AreaPropertyNotAWall,
                $"name={name}",
                "the model defines this area property and classifies it as a slab or deck, " +
                "not a wall; list-wall-properties returns only the names that are walls")),

            AreaPropertyClass.NotDefined => new InvalidOperationException(string.Join(
                "; ",
                InspectionErrorCodes.AreaPropertyNotFound,
                $"name={name}",
                "a successful census of every area property in the model does not contain " +
                "this name")),

            // Includes the case where GetWall now succeeds on a retry: the first refusal was
            // then a transient failure, and reporting success off the back of it would be
            // answering from evidence this call did not actually gather.
            var other => Indeterminate(name, other)
        };

    /// <summary>
    /// The honest answer when the model could not be made to say anything definite.
    ///
    /// <para>Explicitly NOT one of the semantic codes: the caller must be able to tell "your
    /// name is wrong" from "ETABS would not answer me". And the two indeterminate cases get
    /// their own wording, because a diagnostic that claims the census failed when it
    /// succeeded is itself a false statement about the run — the same defect class as
    /// reporting a failed probe as a verdict, just one layer further out.</para>
    /// </summary>
    private static InvalidOperationException Indeterminate(
        string name,
        AreaPropertyClass reason) =>
        new(string.Join(
            "; ",
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            $"name={name}",
            reason == AreaPropertyClass.IndeterminateUnclassified
                ? "the area-property census succeeded and lists this property, but none of " +
                  "cPropArea.GetWall, GetSlab or GetDeck accepted it, so its kind could " +
                  "not be established"
                : "cPropArea refused every classification probe (GetWall, GetSlab, " +
                  "GetDeck) and the area-property census did not succeed either, so no " +
                  "evidence about this property could be gathered",
            "this is a CSI/classification failure, not a statement about the model"));

    private static void RequireSuccess(string member, int returnCode)
    {
        if (returnCode != 0)
        {
            throw new InvalidOperationException($"{member} failed (ret={returnCode}).");
        }
    }

    private static string? FingerprintSavedFile(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return null;
        }

        using var stream = File.OpenRead(modelPath);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}";
    }

    private sealed record UnitAudit(InspectionUnitData Original, InspectionUnitData Execution);
}
