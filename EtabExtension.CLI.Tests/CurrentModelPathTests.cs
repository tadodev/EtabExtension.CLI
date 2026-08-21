// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using ETABSv1;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The Mode A commands must never treat a value that names no file as an open model.
///
/// <para>The value used here as <c>Folder</c> is the one a supervised live run against
/// <c>D:\Work\tadoEng\TestModel\sample_v2.EDB</c> (ETABS 23.3.0) actually observed
/// <c>get-status</c> reporting as <c>openFilePath</c>: the folder, with a trailing
/// separator and no file name.</para>
/// </summary>
public sealed class CurrentModelPathTests
{
    private const string Folder = @"D:\Work\tadoEng\TestModel\";
    private const string ModelFile = @"D:\Work\tadoEng\TestModel\sample_v2.EDB";

    [Fact]
    public void AFolderNamesNoOpenModelFile()
    {
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(Folder));
        Assert.False(EtabsCurrentModelPath.NamesAFile(Folder));
    }

    [Fact]
    public void BlankNamesNoOpenModelFile()
    {
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(null));
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(string.Empty));
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile("   "));
    }

    [Fact]
    public void AFullFilePathNamesTheOpenModelFile()
    {
        Assert.Equal(ModelFile, EtabsCurrentModelPath.ResolveOpenFile(ModelFile));
        Assert.True(EtabsCurrentModelPath.NamesAFile(ModelFile));
    }

    // ── get-status ───────────────────────────────────────────────────────────

    [Fact]
    public void StatusNeverReportsAFolderAsTheOpenModel()
    {
        var data = GetStatusService.ComposeStatus(
            Folder,
            pid: 1234,
            EtabsInstanceOwnership.Managed,
            [1234],
            etabsVersion: "23.3.0",
            isLocked: true,
            isAnalyzed: false,
            unitSystem: null);

        Assert.Null(data.OpenFilePath);
        Assert.False(data.IsModelOpen);
    }

    [Fact]
    public void StatusReportsTheFullFilePathOfTheOpenModel()
    {
        var data = GetStatusService.ComposeStatus(
            ModelFile,
            pid: 1234,
            EtabsInstanceOwnership.Managed,
            [1234],
            etabsVersion: "23.3.0",
            isLocked: true,
            isAnalyzed: false,
            unitSystem: null);

        Assert.Equal(ModelFile, data.OpenFilePath);
        Assert.True(data.IsModelOpen);
    }

    // ── unlock-model ─────────────────────────────────────────────────────────

    [Fact]
    public void UnlockRefusesWhenEtabsNamesNoCurrentModelFile()
    {
        var error = UnlockModelService.ValidateRequestedFileIsOpen(Folder, ModelFile);

        Assert.NotNull(error);
        Assert.Contains("File not open in ETABS", error, StringComparison.Ordinal);
        Assert.Contains(Folder, error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlockAcceptsTheOpenModelReportedAsAFullFilePath()
    {
        Assert.Null(UnlockModelService.ValidateRequestedFileIsOpen(
            ModelFile,
            @"D:/Work/tadoEng/TestModel/SAMPLE_V2.edb"));
    }

    // ── close-model ──────────────────────────────────────────────────────────

    [Fact]
    public void CloseNeverHandsAValueThatNamesNoFileToSaveFile()
    {
        var savedPaths = new List<string>();
        var initializeCount = 0;

        var result = CloseModelService.CompleteClose(
            Folder,
            save: true,
            path =>
            {
                savedPaths.Add(path);
                return 0;
            },
            _ =>
            {
                initializeCount++;
                return 0;
            });

        Assert.False(result.Success);
        Assert.Empty(savedPaths);
        Assert.Equal(0, initializeCount);
        Assert.Contains(Folder, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseWithoutSavingClearsAWorkspaceThatNamesNoFile()
    {
        var result = CloseModelService.CompleteClose(
            Folder,
            save: false,
            _ => 0,
            _ => 0);

        Assert.True(result.Success);
        Assert.Null(result.Data!.ClosedFilePath);
        Assert.False(result.Data.WasSaved);
    }

    [Fact]
    public void CloseSavesToTheFullFilePathAndReportsIt()
    {
        string? savedPath = null;
        eUnits? initializedUnits = null;

        var result = CloseModelService.CompleteClose(
            ModelFile,
            save: true,
            path =>
            {
                savedPath = path;
                return 0;
            },
            units =>
            {
                initializedUnits = units;
                return 0;
            });

        Assert.True(result.Success);
        Assert.Equal(ModelFile, savedPath);
        Assert.Equal(eUnits.kip_ft_F, initializedUnits);
        Assert.Equal(ModelFile, result.Data!.ClosedFilePath);
        Assert.True(result.Data.WasSaved);
    }
}
