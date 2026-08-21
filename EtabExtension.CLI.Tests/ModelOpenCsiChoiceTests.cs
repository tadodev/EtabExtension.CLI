using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// Guards the one CSI choice that no behavioral test can reach.
///
/// <para>Everything else about the model-open boundary is proven through
/// <see cref="IEtabsModelFileApi"/> with a fake. The single line that picks the real
/// CSI call sits on the far side of that seam, against a COM object no test can
/// construct — and picking the wrong one there is exactly the defect that shipped
/// and had to be caught by a supervised live run. These tests fail on the revert.</para>
/// </summary>
public sealed class ModelOpenCsiChoiceTests
{
    /// <summary>
    /// <c>GetModelFilepath()</c> returns the model's FOLDER (observed, ETABS 23.3.0).
    /// Nothing on the model-open path may call it — a folder cannot be saved back and
    /// cannot confirm which model is loaded.
    /// </summary>
    [Theory]
    [InlineData(@"src\EtabExtension.CLI\Shared\Infrastructure\Etabs\EtabsModelOpen.cs")]
    [InlineData(@"src\EtabExtension.CLI\Shared\Infrastructure\Etabs\EtabsSessionHelpers.cs")]
    [InlineData(@"src\EtabExtension.CLI\Features\OpenModel\OpenModelService.cs")]
    [InlineData(@"src\EtabExtension.CLI\Features\SnapshotExport\SnapshotExportService.cs")]
    public void ModelOpenPathNeverCallsTheFolderReturningApi(string relativePath)
    {
        var source = ReadRepositoryFile(relativePath);

        var calls = Regex.Matches(source, @"\.\s*GetModelFilepath\s*\(", RegexOptions.None);

        Assert.True(
            calls.Count == 0,
            $"{relativePath} calls GetModelFilepath(). That call returns the model's " +
            "FOLDER, not its file path — it cannot be handed to cFile.Save and cannot " +
            "confirm which model is open. Use GetModelFilename(includePath: true) " +
            "through IEtabsModelFileApi.");
    }

    /// <summary>
    /// The seam's only production implementation must read the current model with
    /// <c>GetModelFilename(includePath: true)</c>. Reverting that line is invisible to
    /// every fake-driven test, so it is asserted directly.
    /// </summary>
    [Fact]
    public void TheModelFileApiReadsTheCurrentModelWithGetModelFilename()
    {
        var source = ReadRepositoryFile(
            @"src\EtabExtension.CLI\Shared\Infrastructure\Etabs\EtabsModelOpen.cs");

        var implementation = Between(
            source,
            "public string? GetModelFilename() =>",
            ";");

        Assert.Contains("ModelInfo.GetModelFilename(", implementation, StringComparison.Ordinal);
        Assert.Contains("includePath: true", implementation, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Could not find '{start}' — did the seam get renamed?");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find '{end}' after '{start}'.");
        return source[from..to];
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"Expected source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Anchored to this file's own compile-time location, so it does not depend on the
    /// working directory or on where the test assembly was copied.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
}
