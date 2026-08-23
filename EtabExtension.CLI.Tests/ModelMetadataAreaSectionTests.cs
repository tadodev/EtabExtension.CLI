using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Metadata;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// CLI #29. <c>read-model-metadata</c> labelled every area property "Wall": it read
/// <c>cPropArea.GetTypeOAPI</c>'s PropType (1=Shell, 2=Plane, 3=Asolid) through an enum
/// whose 1 means Wall, and every ETABS area property is a Shell. One live run exported 17
/// area sections all labelled "Wall" while the E2K written by that same run recorded 7
/// walls, 9 slabs and one deck.
///
/// <para>These tests hold the replacement to the rule that produced it: a kind may only
/// come from a typed accessor that accepted the name, and anything else is "Unknown" with
/// the evidence attached.</para>
/// </summary>
public sealed class ModelMetadataAreaSectionTests
{
    /// <summary>
    /// Walls, slabs and decks in one census, each labelled by the accessor that accepted
    /// it - and the property nobody would claim reported as unknown rather than guessed.
    /// </summary>
    [Fact]
    public void EachAreaPropertyIsLabelledByTheAccessorThatAcceptedIt()
    {
        var probes = new FakeAreaPropertyProbes()
            .Wall("W300")
            .Slab("Slab250")
            .Deck("Deck1");
        var names = new[] { "W300", "Slab250", "Deck1", "Mystery" };

        var sections = Classify(names, probes);

        Assert.Equal(
            [
                new AreaSectionInfo("W300", "Wall"),
                new AreaSectionInfo("Slab250", "Slab"),
                new AreaSectionInfo("Deck1", "Deck"),
                new AreaSectionInfo("Mystery", "Unknown")
            ],
            sections);

        // The unclassifiable one is reported, not dropped, and it carries evidence.
        var warning = Assert.Single(EtabsSessionHelpers.CollectedMetadataWarnings);
        Assert.Equal("areaSections", warning.Category);
        Assert.Contains("Mystery", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cPropArea.GetWall ret=1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cPropArea.GetSlab ret=1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cPropArea.GetDeck ret=1", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live symptom, in the shape the live run produced it: the sanctioned model's 17
    /// area properties are 7 walls, 9 slabs and one deck named Deck1, and the export called
    /// all 17 of them walls.
    /// </summary>
    [Fact]
    public void ADeckIsNotReportedAsAWall()
    {
        var walls = Enumerable.Range(1, 7).Select(i => $"Wall{i}").ToArray();
        var slabs = Enumerable.Range(1, 9).Select(i => $"Slab{i}").ToArray();
        var probes = new FakeAreaPropertyProbes();
        foreach (var wall in walls)
        {
            probes.Wall(wall);
        }

        foreach (var slab in slabs)
        {
            probes.Slab(slab);
        }

        probes.Deck("Deck1");

        var sections = Classify([.. walls, .. slabs, "Deck1"], probes);

        var deck = Assert.Single(sections, section => section.Name == "Deck1");
        Assert.NotEqual("Wall", deck.PropertyType);
        Assert.Equal("Deck", deck.PropertyType);

        Assert.Equal(7, sections.Count(section => section.PropertyType == "Wall"));
        Assert.Equal(9, sections.Count(section => section.PropertyType == "Slab"));
        Assert.Equal(1, sections.Count(section => section.PropertyType == "Deck"));
        Assert.Empty(EtabsSessionHelpers.CollectedMetadataWarnings);
    }

    /// <summary>
    /// A refused probe is a probe that FAILED. Reading "GetWall returned 7" as "so it is
    /// not a wall" - or, worse, as any kind at all - converts an infrastructure failure into
    /// a statement about the model.
    /// </summary>
    [Fact]
    public void WhenEveryProbeIsRefusedTheTypeIsUnknownAndTheStatusesAreReported()
    {
        var probes = new FakeAreaPropertyProbes().Answers("Opaque", wall: 7, slab: 9, deck: 11);

        var section = Assert.Single(Classify(["Opaque"], probes));

        Assert.Equal("Unknown", section.PropertyType);
        var warning = Assert.Single(EtabsSessionHelpers.CollectedMetadataWarnings);
        Assert.Contains("cPropArea.GetWall ret=7", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cPropArea.GetSlab ret=9", warning.Message, StringComparison.Ordinal);
        Assert.Contains("cPropArea.GetDeck ret=11", warning.Message, StringComparison.Ordinal);
        Assert.Contains("refused probe", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two accessors claiming one property means the evidence disagrees with itself. There
    /// is no precedence rule that could resolve that without inventing one, so the answer is
    /// unknown and the disagreement is reported.
    /// </summary>
    [Fact]
    public void WhenTwoAccessorsClaimTheSamePropertyNeitherKindIsReported()
    {
        var probes = new FakeAreaPropertyProbes().Answers("Ambiguous", wall: 0, slab: 0, deck: 1);

        var section = Assert.Single(Classify(["Ambiguous"], probes));

        Assert.Equal("Unknown", section.PropertyType);
        var warning = Assert.Single(EtabsSessionHelpers.CollectedMetadataWarnings);
        Assert.Contains("GetWall and GetSlab", warning.Message, StringComparison.Ordinal);
        Assert.Contains("contradicted itself", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name is a label a modeller typed, not evidence. "W..." and "S..." are one project's
    /// convention; a model that names its walls differently must classify identically.
    /// </summary>
    [Fact]
    public void TheNameIsNeverEvidenceOfTheKind()
    {
        var probes = new FakeAreaPropertyProbes()
            .Slab("W300")
            .Wall("SlabTypical")
            .Wall("Deck9");

        var sections = Classify(["W300", "SlabTypical", "Deck9"], probes);

        Assert.Equal(
            [
                new AreaSectionInfo("W300", "Slab"),
                new AreaSectionInfo("SlabTypical", "Wall"),
                new AreaSectionInfo("Deck9", "Wall")
            ],
            sections);
        Assert.Empty(EtabsSessionHelpers.CollectedMetadataWarnings);
    }

    /// <summary>
    /// All three accessors are asked about every property. Stopping at the first acceptance
    /// would be cheaper and would hide the contradiction case, and dropping an accessor
    /// would quietly make that whole kind unclassifiable.
    /// </summary>
    [Fact]
    public void EveryAccessorIsAskedAboutEveryProperty()
    {
        var probes = new FakeAreaPropertyProbes().Wall("W300").Deck("Deck1");

        Classify(["W300", "Deck1"], probes);

        Assert.Equal(
            [
                "GetWall(W300)",
                "GetSlab(W300)",
                "GetDeck(W300)",
                "GetWall(Deck1)",
                "GetSlab(Deck1)",
                "GetDeck(Deck1)"
            ],
            probes.Calls);
    }

    private static List<AreaSectionInfo> Classify(
        IEnumerable<string> names,
        IAreaPropertyProbes probes)
    {
        EtabsSessionHelpers.BeginMetadataWarnings();
        return EtabsSessionHelpers.ClassifyAreaSections(names, probes);
    }

    /// <summary>
    /// The typed accessors as ETABS answers them: a status per accessor per name, defaulting
    /// to "refused" so a test only states the acceptances it means.
    /// </summary>
    private sealed class FakeAreaPropertyProbes : IAreaPropertyProbes
    {
        private const int Accepts = 0;
        private const int Refuses = 1;

        private readonly Dictionary<string, ProbeStatuses> _statuses = new(StringComparer.Ordinal);

        internal List<string> Calls { get; } = [];

        internal FakeAreaPropertyProbes Wall(string name) =>
            Answers(name, wall: Accepts, slab: Refuses, deck: Refuses);

        internal FakeAreaPropertyProbes Slab(string name) =>
            Answers(name, wall: Refuses, slab: Accepts, deck: Refuses);

        internal FakeAreaPropertyProbes Deck(string name) =>
            Answers(name, wall: Refuses, slab: Refuses, deck: Accepts);

        internal FakeAreaPropertyProbes Answers(string name, int wall, int slab, int deck)
        {
            _statuses[name] = new ProbeStatuses(wall, slab, deck);
            return this;
        }

        public int ProbeWall(string name)
        {
            Calls.Add($"GetWall({name})");
            return Status(name).Wall;
        }

        public int ProbeSlab(string name)
        {
            Calls.Add($"GetSlab({name})");
            return Status(name).Slab;
        }

        public int ProbeDeck(string name)
        {
            Calls.Add($"GetDeck({name})");
            return Status(name).Deck;
        }

        private ProbeStatuses Status(string name) =>
            _statuses.TryGetValue(name, out var statuses)
                ? statuses
                : new ProbeStatuses(Refuses, Refuses, Refuses);

        private readonly record struct ProbeStatuses(int Wall, int Slab, int Deck);
    }
}
