using EtabExtension.CLI.Features.CloseModel;
using ETABSv1;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class CloseModelServiceTests
{
    [Fact]
    public void FailedSaveLeavesTheModelOpen()
    {
        var initializeCount = 0;

        var result = CloseModelService.CompleteClose(
            @"C:\models\tower.edb",
            save: true,
            _ => 7,
            _ =>
            {
                initializeCount++;
                return 0;
            });

        Assert.False(result.Success);
        Assert.Contains("SaveFile failed (ret=7)", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, initializeCount);
    }

    [Fact]
    public void SuccessfulSaveClearsTheModelAndReportsTheSavedPath()
    {
        const string path = @"C:\models\tower.edb";
        string? savedPath = null;
        eUnits? initializedUnits = null;

        var result = CloseModelService.CompleteClose(
            path,
            save: true,
            value =>
            {
                savedPath = value;
                return 0;
            },
            units =>
            {
                initializedUnits = units;
                return 0;
            });

        Assert.True(result.Success);
        Assert.Equal(path, savedPath);
        Assert.Equal(eUnits.kip_ft_F, initializedUnits);
        Assert.Equal(path, result.Data!.ClosedFilePath);
        Assert.True(result.Data.WasSaved);
    }

    [Fact]
    public void NoSaveClearsTheModelWithoutCallingSave()
    {
        var saveCount = 0;
        var initializeCount = 0;

        var result = CloseModelService.CompleteClose(
            @"C:\models\tower.edb",
            save: false,
            _ =>
            {
                saveCount++;
                return 0;
            },
            _ =>
            {
                initializeCount++;
                return 0;
            });

        Assert.True(result.Success);
        Assert.Equal(0, saveCount);
        Assert.Equal(1, initializeCount);
        Assert.False(result.Data!.WasSaved);
    }
}
