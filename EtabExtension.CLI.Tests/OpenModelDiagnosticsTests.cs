using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// The validated <c>open-model</c> daemon boundary. It is now nothing but the shared
/// <see cref="EtabsModelOpen"/> primitive plus a pure projection onto
/// <see cref="OpenModelData"/>, so these assertions describe the same COM boundary
/// that <c>snapshot-export</c> uses — the two diagnostics cannot drift apart.
/// </summary>
public sealed class OpenModelDiagnosticsTests
{
    private const string TargetPath = @"D:\Models\target.edb";
    private const string CurrentPath = @"D:\Models\current.edb";

    [Fact]
    public void GetModelFilenameExceptionPreservesActiveOperationAndBoundedComEvidence()
    {
        var saveCalls = 0;
        var openCalls = 0;
        var exception = new TestException(
            new string('g', 600) + "\r\n",
            unchecked((int)0x80004005),
            new InvalidOperationException("inner\tmessage"));

        var result = OpenModel(
            TargetPath,
            save: true,
            getCurrentPath: () => throw exception,
            saveFile: _ => { saveCalls++; return 0; },
            openFile: _ => { openCalls++; return 0; });

        Assert.False(result.Success);
        Assert.Contains(EtabsApiErrorCodes.ComOperationFailed, result.Error, StringComparison.Ordinal);
        Assert.Contains("operation=cSapModel.GetModelFilename", result.Error, StringComparison.Ordinal);
        Assert.Contains("hresult=0x80004005", result.Error, StringComparison.Ordinal);
        Assert.Contains("innerExceptionType=System.InvalidOperationException", result.Error, StringComparison.Ordinal);
        Assert.Contains("innerMessage=inner message", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Error!, char.IsControl);
        Assert.True(result.Error!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
        Assert.Equal(0, saveCalls);
        Assert.Equal(0, openCalls);
    }

    [Fact]
    public void SaveNonzeroReturnsExactBoundedApiDiagnosticAndDoesNotOpen()
    {
        var openCalls = 0;

        var result = OpenModel(
            TargetPath,
            save: true,
            getCurrentPath: () => CurrentPath,
            saveFile: path =>
            {
                Assert.Equal(CurrentPath, path);
                return 17;
            },
            openFile: _ => { openCalls++; return 0; });

        Assert.False(result.Success);
        Assert.Contains(EtabsApiErrorCodes.ApiCallFailed, result.Error, StringComparison.Ordinal);
        Assert.Contains("operation=cFile.Save", result.Error, StringComparison.Ordinal);
        Assert.Contains("returnCode=17", result.Error, StringComparison.Ordinal);
        Assert.True(result.Error!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
        Assert.Equal(0, openCalls);
    }

    [Fact]
    public void SaveExceptionPreservesSaveAsActiveOperation()
    {
        var exception = new TestException("save failed", unchecked((int)0x80070005));

        var result = OpenModel(
            TargetPath,
            save: true,
            getCurrentPath: () => CurrentPath,
            saveFile: _ => throw exception,
            openFile: _ => 0);

        Assert.False(result.Success);
        Assert.Contains("operation=cFile.Save", result.Error, StringComparison.Ordinal);
        Assert.Contains("hresult=0x80070005", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenNonzeroReturnsExactBoundedApiDiagnostic()
    {
        var result = OpenModel(
            TargetPath,
            save: false,
            getCurrentPath: () => CurrentPath,
            saveFile: _ => throw new InvalidOperationException("save must not run"),
            openFile: path =>
            {
                Assert.Equal(TargetPath, path);
                return 23;
            });

        Assert.False(result.Success);
        Assert.Contains(EtabsApiErrorCodes.ApiCallFailed, result.Error, StringComparison.Ordinal);
        Assert.Contains("operation=cFile.OpenFile", result.Error, StringComparison.Ordinal);
        Assert.Contains("returnCode=23", result.Error, StringComparison.Ordinal);
        Assert.True(result.Error!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
    }

    [Fact]
    public void OpenExceptionPreservesOpenAsActiveOperation()
    {
        var exception = new TestException("open failed", unchecked((int)0x8000FFFF));

        var result = OpenModel(
            TargetPath,
            save: false,
            getCurrentPath: () => null,
            saveFile: _ => throw new InvalidOperationException("save must not run"),
            openFile: _ => throw exception);

        Assert.False(result.Success);
        Assert.Contains("operation=cFile.OpenFile", result.Error, StringComparison.Ordinal);
        Assert.Contains("hresult=0x8000FFFF", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveFalseNeverSavesAndSuccessPreservesExistingResultData()
    {
        var saveCalls = 0;
        var openCalls = 0;
        var currentPath = CurrentPath;

        var result = OpenModel(
            TargetPath,
            save: false,
            getCurrentPath: () => currentPath,
            saveFile: _ => { saveCalls++; return 0; },
            openFile: path =>
            {
                Assert.Equal(TargetPath, path);
                openCalls++;
                currentPath = path;
                return 0;
            });

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, saveCalls);
        Assert.Equal(1, openCalls);
        Assert.Equal(TargetPath, result.Data!.FilePath);
        Assert.Equal(CurrentPath, result.Data.PreviousFilePath);
        Assert.Null(result.Data.Pid);
        Assert.False(result.Data.OpenedInNewInstance);
    }

    private static Result<OpenModelData> OpenModel(
        string filePath,
        bool save,
        Func<string?> getCurrentPath,
        Func<string, int> saveFile,
        Func<string, int> openFile) => OpenModelService.ToOpenModelData(
            EtabsModelOpen.OpenOnAttachedModel(filePath, save, getCurrentPath, saveFile, openFile));

    private sealed class TestException : Exception
    {
        public TestException(string message, int hresult, Exception? inner = null)
            : base(message, inner)
        {
            HResult = hresult;
        }
    }
}
