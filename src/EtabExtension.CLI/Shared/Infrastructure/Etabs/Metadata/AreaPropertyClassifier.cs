using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;

/// <summary>
/// The three typed <c>cPropArea</c> accessors, used purely as classification probes: a zero
/// status means the model let that accessor read the property.
///
/// <para>This exists because ETABS has no call that returns an area property's kind as a
/// label. <c>cPropArea.GetTypeOAPI</c> looks like one and is not: its PropType is
/// 1=Shell, 2=Plane, 3=Asolid (ETABS 23.3 API reference), and since every ETABS area
/// property is a Shell it answers 1 for all of them. The kind IS which typed accessor
/// accepts the name, so the probes are the seam.</para>
/// </summary>
internal interface IAreaPropertyProbes
{
    /// <summary><c>cPropArea.GetWall</c>. Zero means the model read it as a wall.</summary>
    int ProbeWall(string name);

    /// <summary><c>cPropArea.GetSlab</c>. Zero means the model read it as a slab.</summary>
    int ProbeSlab(string name);

    /// <summary><c>cPropArea.GetDeck</c>. Zero means the model read it as a deck.</summary>
    int ProbeDeck(string name);
}

/// <summary>The probes as ETABS actually answers them, over the live <c>cSapModel</c>.</summary>
internal sealed class CsiAreaPropertyProbes(cSapModel model) : IAreaPropertyProbes
{
    public int ProbeWall(string name)
    {
        var wallPropType = default(eWallPropType);
        var shellType = default(eShellType);
        var materialProperty = string.Empty;
        var thickness = 0d;
        var color = 0;
        var notes = string.Empty;
        var globalId = string.Empty;
        return model.PropArea.GetWall(
            name,
            ref wallPropType,
            ref shellType,
            ref materialProperty,
            ref thickness,
            ref color,
            ref notes,
            ref globalId);
    }

    public int ProbeSlab(string name)
    {
        var slabType = default(eSlabType);
        var shellType = default(eShellType);
        var materialProperty = string.Empty;
        var thickness = 0d;
        var color = 0;
        var notes = string.Empty;
        var globalId = string.Empty;
        return model.PropArea.GetSlab(
            name,
            ref slabType,
            ref shellType,
            ref materialProperty,
            ref thickness,
            ref color,
            ref notes,
            ref globalId);
    }

    public int ProbeDeck(string name)
    {
        var deckType = default(eDeckType);
        var shellType = default(eShellType);
        var materialProperty = string.Empty;
        var thickness = 0d;
        var color = 0;
        var notes = string.Empty;
        var globalId = string.Empty;
        return model.PropArea.GetDeck(
            name,
            ref deckType,
            ref shellType,
            ref materialProperty,
            ref thickness,
            ref color,
            ref notes,
            ref globalId);
    }
}

/// <summary>
/// Decides what kind of area property a name refers to, from POSITIVE evidence only.
///
/// <para><b>CLI #29.</b> <c>read-model-metadata</c> used to label every area property
/// "Wall". It read <c>cPropArea.GetTypeOAPI</c>'s PropType - 1=Shell, 2=Plane, 3=Asolid -
/// through an enum whose 1 means Wall, and since every ETABS area property is a Shell, the
/// answer was "Wall" for all of them. One live run exported 17 area sections all labelled
/// "Wall" while the E2K written by the same run recorded 7 walls, 9 slabs and one deck,
/// including the deck the metadata called a wall.</para>
///
/// <para><b>How the kind is decided.</b> By which typed accessor the model lets read the
/// property, which is the only thing ETABS will actually say on the subject. Each accessor
/// documents its Name parameter as "an existing wall/slab/deck property", so acceptance is
/// evidence and all three are asked every time - short-circuiting on the first acceptance
/// would hide the case where two of them claim the same property.</para>
///
/// <para><b>What is deliberately NOT done.</b> No inference from the name: "W..." and
/// "S..." are one project's convention, not a property of ETABS. No reading of a nonzero
/// status as a verdict: a refused probe is a probe that FAILED, and turning "GetWall
/// returned 1" into "so it is a slab" is the same unknown-as-fact conversion that produced
/// the defect. A property nobody could classify is reported as unknown, with the return
/// codes as evidence, and never as a guess.</para>
/// </summary>
internal static class AreaPropertyClassifier
{
    internal const string Wall = "Wall";
    internal const string Slab = "Slab";
    internal const string Deck = "Deck";

    /// <summary>The label for a property whose kind could not be established.</summary>
    internal const string Unknown = "Unknown";

    /// <summary>
    /// The kind of one area property, or an exception carrying the evidence when the model
    /// would not establish one. The caller turns that into <see cref="Unknown"/> plus a
    /// metadata warning; it must never turn it into a type.
    /// </summary>
    internal static string Classify(IAreaPropertyProbes probes, string name)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var wall = probes.ProbeWall(name);
        var slab = probes.ProbeSlab(name);
        var deck = probes.ProbeDeck(name);

        var accepted = new List<string>(capacity: 3);
        if (wall == 0)
        {
            accepted.Add(Wall);
        }

        if (slab == 0)
        {
            accepted.Add(Slab);
        }

        if (deck == 0)
        {
            accepted.Add(Deck);
        }

        if (accepted.Count == 1)
        {
            return accepted[0];
        }

        var evidence =
            $"cPropArea.GetWall ret={wall}, cPropArea.GetSlab ret={slab}, cPropArea.GetDeck ret={deck}";

        throw new InvalidOperationException(accepted.Count == 0
            ? $"none of cPropArea.GetWall, GetSlab or GetDeck accepted it ({evidence}); a " +
              "nonzero status is a refused probe, not a statement about the model, so the " +
              "kind of this area property is unknown"
            : $"cPropArea contradicted itself - {string.Join(" and ", accepted.Select(kind => $"Get{kind}"))} " +
              $"each accepted it ({evidence}) - so the kind of this area property could " +
              "not be established");
    }
}
