using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.Serve.Inspection;

public sealed record RawWallProperty(
    eWallPropType WallPropType,
    eShellType ShellType,
    string MaterialProperty,
    double Thickness,
    int Color,
    string Notes,
    string GlobalId);

public sealed record RawShellDesign(
    string MaterialProperty,
    int SteelLayoutOption,
    double DesignCoverTopDir1,
    double DesignCoverTopDir2,
    double DesignCoverBotDir1,
    double DesignCoverBotDir2);

public interface IEtabsInspectionApi
{
    eUnits GetPresentUnits();
    int SetPresentUnits(eUnits units);
    string GetModelFilename();
    bool GetModelIsLocked();
    int GetCaseStatus(out string[] caseNames, out int[] statuses);
    int GetWall(string name, out RawWallProperty property);
    int GetModifiers(string name, out double[] modifiers);
    int GetShellDesign(string name, out RawShellDesign shellDesign);
    /// <summary>
    /// Every SHELL area property in the model - walls, slabs, decks and mats alike.
    ///
    /// <para>Named for what <c>cPropArea.GetNameList(PropType=1)</c> actually returns. It
    /// was previously called <c>GetWallPropertyNames</c>, which is where CLI #28 came from:
    /// PropType 1 is "Shell" in the ETABS API (0=All, 1=Shell, 2=Plane, 3=Asolid) and there
    /// is no wall value at all, so the name promised a filter the API cannot perform.</para>
    /// </summary>
    int GetShellPropertyNames(out string[] names);

    /// <summary>
    /// <c>cPropArea.GetTypeOAPI</c>. Used only as an EXISTENCE probe: it reports Shell,
    /// Plane or Asolid, so it cannot tell a wall from a slab, but it returns nonzero for a
    /// name the model does not define. That is what separates "no such property" from
    /// "exists, but is not a wall".
    /// </summary>
    int GetAreaPropertyType(string name, out int propertyType);
    int GetAreaNames(out string[] names);
    int GetAreaProperty(string name, out string propertyName);
    int GetAreaLabelAndStory(string name, out string label, out string story);
    int GetAreaPier(string name, out string pier);
    int GetAreaGuid(string name, out string globalId);
    int GetAreaDesignOrientation(string name, out eAreaDesignOrientation orientation);
}

public interface IEtabsInspectionApiFactory
{
    IEtabsInspectionApi Create(ETABSApplication application);
}

public sealed class EtabsInspectionApiFactory : IEtabsInspectionApiFactory
{
    public IEtabsInspectionApi Create(ETABSApplication application) => new EtabsInspectionApi(application);
}

internal sealed class EtabsInspectionApi(ETABSApplication application) : IEtabsInspectionApi
{
    private cSapModel Model => application.SapModel;

    public eUnits GetPresentUnits() => Model.GetPresentUnits();

    public int SetPresentUnits(eUnits units) => Model.SetPresentUnits(units);

    public string GetModelFilename() => Model.GetModelFilename(true);

    public bool GetModelIsLocked() => Model.GetModelIsLocked();

    public int GetCaseStatus(out string[] caseNames, out int[] statuses)
    {
        var count = 0;
        caseNames = [];
        statuses = [];
        var ret = Model.Analyze.GetCaseStatus(ref count, ref caseNames, ref statuses);
        caseNames ??= [];
        statuses ??= [];
        return ret;
    }

    public int GetWall(string name, out RawWallProperty property)
    {
        var wallPropType = default(eWallPropType);
        var shellType = default(eShellType);
        var materialProperty = string.Empty;
        var thickness = 0d;
        var color = 0;
        var notes = string.Empty;
        var globalId = string.Empty;
        var ret = Model.PropArea.GetWall(
            name,
            ref wallPropType,
            ref shellType,
            ref materialProperty,
            ref thickness,
            ref color,
            ref notes,
            ref globalId);
        property = new(
            wallPropType,
            shellType,
            materialProperty ?? string.Empty,
            thickness,
            color,
            notes ?? string.Empty,
            globalId ?? string.Empty);
        return ret;
    }

    public int GetModifiers(string name, out double[] modifiers)
    {
        modifiers = [];
        var ret = Model.PropArea.GetModifiers(name, ref modifiers);
        modifiers ??= [];
        return ret;
    }

    public int GetShellDesign(string name, out RawShellDesign shellDesign)
    {
        var materialProperty = string.Empty;
        var steelLayoutOption = 0;
        var designCoverTopDir1 = 0d;
        var designCoverTopDir2 = 0d;
        var designCoverBotDir1 = 0d;
        var designCoverBotDir2 = 0d;
        var ret = Model.PropArea.GetShellDesign(
            name,
            ref materialProperty,
            ref steelLayoutOption,
            ref designCoverTopDir1,
            ref designCoverTopDir2,
            ref designCoverBotDir1,
            ref designCoverBotDir2);
        shellDesign = new(
            materialProperty ?? string.Empty,
            steelLayoutOption,
            designCoverTopDir1,
            designCoverTopDir2,
            designCoverBotDir1,
            designCoverBotDir2);
        return ret;
    }

    public int GetShellPropertyNames(out string[] names)
    {
        var count = 0;
        names = [];
        // PropType 1 = Shell. Not wall - the enum has no wall value.
        var ret = Model.PropArea.GetNameList(ref count, ref names, 1);
        names ??= [];
        return ret;
    }

    public int GetAreaPropertyType(string name, out int propertyType)
    {
        propertyType = 0;
        return Model.PropArea.GetTypeOAPI(name, ref propertyType);
    }

    public int GetAreaNames(out string[] names)
    {
        var count = 0;
        names = [];
        var ret = Model.AreaObj.GetNameList(ref count, ref names);
        names ??= [];
        return ret;
    }

    public int GetAreaProperty(string name, out string propertyName)
    {
        propertyName = string.Empty;
        var ret = Model.AreaObj.GetProperty(name, ref propertyName);
        propertyName ??= string.Empty;
        return ret;
    }

    public int GetAreaLabelAndStory(string name, out string label, out string story)
    {
        label = string.Empty;
        story = string.Empty;
        var ret = Model.AreaObj.GetLabelFromName(name, ref label, ref story);
        label ??= string.Empty;
        story ??= string.Empty;
        return ret;
    }

    public int GetAreaPier(string name, out string pier)
    {
        pier = string.Empty;
        var ret = Model.AreaObj.GetPier(name, ref pier);
        pier ??= string.Empty;
        return ret;
    }

    public int GetAreaGuid(string name, out string globalId)
    {
        globalId = string.Empty;
        var ret = Model.AreaObj.GetGUID(name, ref globalId);
        globalId ??= string.Empty;
        return ret;
    }

    public int GetAreaDesignOrientation(string name, out eAreaDesignOrientation orientation)
    {
        orientation = default;
        return Model.AreaObj.GetDesignOrientation(name, ref orientation);
    }
}
