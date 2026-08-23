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
                var verdict = Classify(api, name);
                switch (verdict.Class)
                {
                    case AreaPropertyClass.Wall:
                        walls.Add(name);
                        break;

                    case AreaPropertyClass.NotAWall:
                        break;

                    case AreaPropertyClass.NotDefined:
                        // This name came FROM the shell census moments ago, and the
                        // all-property census has just denied it exists. One of them is
                        // wrong and this code cannot tell which, so it reports the
                        // disagreement rather than picking a winner.
                        throw Indeterminate(name, IndeterminateReason.ContradictoryEvidence);

                    default:
                        // A property nobody could classify must not be quietly dropped. The
                        // caller asked for THE wall properties; answering with a list that
                        // silently omits a wall whose probe failed is the same class of lie
                        // as answering with slabs in it.
                        throw Indeterminate(name, verdict.Reason);
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
        /// <summary>Nothing could be established. Carries an <see cref="IndeterminateReason"/>.</summary>
        Indeterminate,

        /// <summary><c>cPropArea.GetWall</c> accepted it.</summary>
        Wall,

        /// <summary>A different typed accessor accepted it - slab or deck.</summary>
        NotAWall,

        /// <summary>A successful census of every area property does not contain the name.</summary>
        NotDefined
    }

    /// <summary>
    /// Why a classification could not be reached.
    ///
    /// <para>A SEPARATE domain from <see cref="AreaPropertyClass"/> on purpose. When the two
    /// shared one enum, the failure diagnostic accepted every value - including the semantic
    /// ones - and described whatever it was handed as "all probes refused and the census
    /// failed". Two live paths reached it that way and printed a false account of the run.
    /// Splitting the types makes those calls not compile rather than not happen.</para>
    /// </summary>
    private enum IndeterminateReason
    {
        /// <summary>Every typed accessor refused AND the census failed. Nothing is known.</summary>
        NoEvidence,

        /// <summary>
        /// The census succeeded and lists this property, but no typed accessor claimed it -
        /// an ETABS shell property outside the wall/slab/deck accessors this build probes.
        /// The property demonstrably exists; only its kind is unestablished.
        /// </summary>
        Unclassified,

        /// <summary>
        /// The evidence contradicted itself within one request: a probe that refused and
        /// then accepted, or two censuses that disagree about whether the property exists.
        /// Nothing here is safe to report as a fact about the model.
        /// </summary>
        ContradictoryEvidence
    }

    /// <summary>
    /// One classification outcome. The reason is meaningful only when <see cref="Class"/> is
    /// <see cref="AreaPropertyClass.Indeterminate"/>, and the factories below are the only
    /// way to build one, so a semantic verdict cannot smuggle a reason along with it.
    /// </summary>
    private readonly record struct AreaPropertyVerdict(
        AreaPropertyClass Class,
        IndeterminateReason Reason)
    {
        internal static AreaPropertyVerdict Wall { get; } =
            new(AreaPropertyClass.Wall, IndeterminateReason.NoEvidence);

        internal static AreaPropertyVerdict NotAWall { get; } =
            new(AreaPropertyClass.NotAWall, IndeterminateReason.NoEvidence);

        internal static AreaPropertyVerdict NotDefined { get; } =
            new(AreaPropertyClass.NotDefined, IndeterminateReason.NoEvidence);

        internal static AreaPropertyVerdict Unknown(IndeterminateReason reason) =>
            new(AreaPropertyClass.Indeterminate, reason);
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
    private static AreaPropertyVerdict Classify(IEtabsInspectionApi api, string name)
    {
        if (api.GetWall(name, out _) == 0)
        {
            return AreaPropertyVerdict.Wall;
        }

        // Positive classification as something else. ETABS has no call that returns a
        // property's kind as a label, so the kind IS which accessor accepts it.
        if (api.ProbeSlab(name) == 0 || api.ProbeDeck(name) == 0)
        {
            return AreaPropertyVerdict.NotAWall;
        }

        // Absence has to be proven by a census that succeeded, not inferred from a probe
        // that did not.
        if (api.GetAllAreaPropertyNames(out var defined) == 0)
        {
            return defined.Contains(name, StringComparer.Ordinal)
                ? AreaPropertyVerdict.Unknown(IndeterminateReason.Unclassified)
                : AreaPropertyVerdict.NotDefined;
        }

        return AreaPropertyVerdict.Unknown(IndeterminateReason.NoEvidence);
    }

    /// <summary>Turns a refused <c>GetWall</c> into the reason the evidence supports.</summary>
    private static InvalidOperationException Diagnose(IEtabsInspectionApi api, string name)
    {
        var verdict = Classify(api, name);
        return verdict.Class switch
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

            // GetWall refused a moment ago and has just accepted. The model's answer is
            // not stable within one request, so nothing here is safe to report as a fact -
            // least of all a success built on the probe that already failed once.
            AreaPropertyClass.Wall =>
                Indeterminate(name, IndeterminateReason.ContradictoryEvidence),

            _ => Indeterminate(name, verdict.Reason)
        };
    }

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
        IndeterminateReason reason) =>
        new(string.Join(
            "; ",
            InspectionErrorCodes.AreaPropertyClassificationFailed,
            $"name={name}",
            reason switch
            {
                IndeterminateReason.Unclassified =>
                    "the area-property census succeeded and lists this property, but none " +
                    "of cPropArea.GetWall, GetSlab or GetDeck accepted it, so its kind " +
                    "could not be established",

                IndeterminateReason.ContradictoryEvidence =>
                    "cPropArea gave inconsistent answers about this property within one " +
                    "request - a probe that refused and then accepted, or two censuses " +
                    "that disagree about whether it exists - so no verdict was taken",

                _ =>
                    "cPropArea refused every classification probe (GetWall, GetSlab, " +
                    "GetDeck) and the area-property census did not succeed either, so no " +
                    "evidence about this property could be gathered"
            },
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
