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

    /// <summary>
    /// Every shape ETABS could answer with that is not a fully-qualified file — including
    /// the two a "has a last segment" rule alone would wave through, republishing
    /// <c>isModelOpen: true</c> with a value Rust can never match.
    /// </summary>
    [Theory]
    [InlineData(Folder)]                        // trailing separator — the observed defect
    [InlineData(@"D:\")]                        // drive root
    [InlineData(@"\\server\share")]             // UNC root
    [InlineData(@"D:\Models")]                  // separator-less folder: names "Models"
    [InlineData("sample_v2.EDB")]               // relative: names a file, resolves nowhere
    [InlineData(@"TestModel\sample_v2.EDB")]    // relative with a folder segment
    public void NothingButAFullyQualifiedFileNamesAnOpenModel(string reported)
    {
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(reported));
        Assert.False(EtabsCurrentModelPath.NamesAFile(reported));
        Assert.True(EtabsCurrentModelPath.ReportedWithoutFileName(reported));
    }

    [Fact]
    public void BlankNamesNoOpenModelFile()
    {
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(null));
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile(string.Empty));
        Assert.Null(EtabsCurrentModelPath.ResolveOpenFile("   "));

        // Blank means "nothing is loaded", not "ETABS answered something unusable".
        Assert.False(EtabsCurrentModelPath.ReportedWithoutFileName(null));
        Assert.False(EtabsCurrentModelPath.ReportedWithoutFileName("   "));
    }

    [Fact]
    public void AFullFilePathNamesTheOpenModelFile()
    {
        Assert.Equal(ModelFile, EtabsCurrentModelPath.ResolveOpenFile(ModelFile));
        Assert.True(EtabsCurrentModelPath.NamesAFile(ModelFile));
        Assert.False(EtabsCurrentModelPath.ReportedWithoutFileName(ModelFile));
    }

    // ── get-status ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Folder)]
    [InlineData(@"D:\Models")]
    public void StatusNeverReportsSomethingThatIsNotAFileAsTheOpenModel(string reported)
    {
        var data = Compose(reported);

        Assert.Null(data.OpenFilePath);
        Assert.False(data.IsModelOpen);
    }

    /// <summary>
    /// Asserts every field, not just the two under repair: <c>isLocked</c> and
    /// <c>isAnalyzed</c> are both <c>bool?</c> and are passed positionally, so a swap
    /// there is otherwise invisible.
    /// </summary>
    [Fact]
    public void StatusReportsTheFullFilePathOfTheOpenModelAndCarriesEveryFieldThrough()
    {
        var data = Compose(ModelFile);

        Assert.Equal(ModelFile, data.OpenFilePath);
        Assert.True(data.IsModelOpen);
        Assert.True(data.IsRunning);
        Assert.Equal(1234, data.Pid);
        Assert.Equal(EtabsInstanceOwnership.Managed, data.Ownership);
        Assert.Equal([1234], data.ObservedPids);
        Assert.Equal("23.3.0", data.EtabsVersion);
        Assert.True(data.IsLocked);
        Assert.False(data.IsAnalyzed);
        Assert.Null(data.UnitSystem);
    }

    private static GetStatusData Compose(string? reported) =>
        GetStatusService.ComposeStatus(
            reported,
            pid: 1234,
            EtabsInstanceOwnership.Managed,
            [1234],
            etabsVersion: "23.3.0",
            isLocked: true,
            isAnalyzed: false,
            unitSystem: null);

    // ── unlock-model ─────────────────────────────────────────────────────────

    [Fact]
    public void UnlockRefusesWhenEtabsNamesNoCurrentModelFile()
    {
        var error = UnlockModelService.ValidateRequestedFileIsOpen(Folder, ModelFile);

        Assert.NotNull(error);
        Assert.Contains("File not open in ETABS", error, StringComparison.Ordinal);
        Assert.Contains(Folder, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard's core safety property, and the only test that exercises the comparison
    /// itself: the refusal above reduces to <c>null</c> before any comparison runs, so
    /// weakening the equality to a null check alone would leave it passing while
    /// <c>unlock-model</c> unlocked whatever model happened to be loaded.
    /// </summary>
    [Theory]
    [InlineData(@"D:\Work\tadoEng\TestModel\other.EDB")]         // same folder, other model
    [InlineData(@"D:\Work\tadoEng\Archive\sample_v2.EDB")]       // same name, other folder
    public void UnlockRefusesAModelThatIsNotTheRequestedFile(string reportedOpenFile)
    {
        var error = UnlockModelService.ValidateRequestedFileIsOpen(reportedOpenFile, ModelFile);

        Assert.NotNull(error);
        Assert.Contains("File not open in ETABS", error, StringComparison.Ordinal);
        Assert.Contains(reportedOpenFile, error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlockAcceptsTheOpenModelReportedAsAFullFilePath()
    {
        // Deliberately lenient about separator and case — see PathsAreEqual.
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
