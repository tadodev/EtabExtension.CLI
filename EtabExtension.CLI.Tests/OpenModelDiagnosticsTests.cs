using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class OpenModelDiagnosticsTests
{
    private const string TargetPath = @"D:\Models\target.edb";
    private const string CurrentPath = @"D:\Models\current.edb";

    [Fact]
    public void Get_model_filename_exception_preserves_active_operation_and_bounded_com_evidence()
    {
        var saveCalls = 0;
        var openCalls = 0;
        var exception = new TestException(
            new string('g', 600) + "\r\n",
            unchecked((int)0x80004005),
            new InvalidOperationException("inner\tmessage"));

        var result = OpenModelService.OpenOnAttachedModel(
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
    public void Save_nonzero_returns_exact_bounded_api_diagnostic_and_does_not_open()
    {
        var openCalls = 0;

        var result = OpenModelService.OpenOnAttachedModel(
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
    public void Save_exception_preserves_save_as_active_operation()
    {
        var exception = new TestException("save failed", unchecked((int)0x80070005));

        var result = OpenModelService.OpenOnAttachedModel(
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
    public void Open_nonzero_returns_exact_bounded_api_diagnostic()
    {
        var result = OpenModelService.OpenOnAttachedModel(
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
    public void Open_exception_preserves_open_as_active_operation()
    {
        var exception = new TestException("open failed", unchecked((int)0x8000FFFF));

        var result = OpenModelService.OpenOnAttachedModel(
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
    public void Save_false_never_saves_and_success_preserves_existing_result_data()
    {
        var saveCalls = 0;
        var openCalls = 0;

        var result = OpenModelService.OpenOnAttachedModel(
            TargetPath,
            save: false,
            getCurrentPath: () => CurrentPath,
            saveFile: _ => { saveCalls++; return 0; },
            openFile: path =>
            {
                Assert.Equal(TargetPath, path);
                openCalls++;
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

    private sealed class TestException : Exception
    {
        public TestException(string message, int hresult, Exception? inner = null)
            : base(message, inner)
        {
            HResult = hresult;
        }
    }
}
