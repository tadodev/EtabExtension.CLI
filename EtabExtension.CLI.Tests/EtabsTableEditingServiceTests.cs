using EtabExtension.CLI.Shared.Infrastructure.Etabs.Table;
using Xunit;

namespace EtabExtension.CLI.Tests;

public class EtabsTableEditingServiceTests
{
    [Fact]
    public void BuildRowLookupUsesCaseInsensitiveFirstRowWinsSemantics()
    {
        var first = new Dictionary<string, string>
        {
            ["Case"] = "DBE_X",
            ["Scale"] = "1.0"
        };
        var duplicate = new Dictionary<string, string>
        {
            ["Case"] = "dbe_x",
            ["Scale"] = "2.0"
        };
        var other = new Dictionary<string, string>
        {
            ["Case"] = "DBE_Y",
            ["Scale"] = "3.0"
        };
        var missingKey = new Dictionary<string, string>
        {
            ["Scale"] = "4.0"
        };
        var nullKey = new Dictionary<string, string>
        {
            ["Case"] = null!,
            ["Scale"] = "5.0"
        };

        var lookup = EtabsTableEditingService.BuildRowLookup(
            [first, duplicate, other, missingKey, nullKey],
            "Case");

        Assert.Equal(2, lookup.Count);
        Assert.Same(first, lookup["DBE_X"]);
        Assert.Same(first, lookup["dbe_x"]);
        Assert.Same(other, lookup["dbe_y"]);
        Assert.False(lookup.ContainsKey(string.Empty));
    }
}
