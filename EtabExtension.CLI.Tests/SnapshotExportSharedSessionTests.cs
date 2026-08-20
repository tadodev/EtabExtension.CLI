using EtabExtension.CLI.Features.SnapshotExport;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// Stage-level contract for the shared-session <c>snapshot-export</c> path: it must
/// open the requested model through the one canonical model-open primitive, stop at
/// the first failed stage with a bounded diagnostic, and never claim success for an
/// absent or empty E2K export.
/// </summary>
public sealed class SnapshotExportSharedSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-snapshot-export-tests", Guid.NewGuid().ToString("N"));

    public SnapshotExportSharedSessionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string CreateModel(string name = "sample.edb")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "edb-fixture");
        return path;
    }

    private string OutputDir(string name = "out")
    {
        var path = Path.Combine(_directory, name);
        return path;
    }

    [Fact]
    public async Task SharedSessionExportStopsAtTheModelOpenStageWithTheCanonicalDiagnostic()
    {
        var model = CreateModel();
        var outputDir = OutputDir();
        var opener = new FakeModelOpener(Result.Fail<ModelOpenOutcome>(
            EtabsApiDiagnosticFormatter.ApiReturn("cFile.OpenFile", 23)));
        var service = new SnapshotExportService(null!, null!, null!, opener);

        var result = await service.SnapshotExportOnAppAsync(
            null!, model, outputDir, new SnapshotExportRequest());

        Assert.False(result.Success);
        Assert.Contains("operation=cFile.OpenFile", result.Error, StringComparison.Ordinal);
        Assert.Contains("returnCode=23", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, opener.Calls);
        Assert.Equal(model, opener.FilePath);
        Assert.False(opener.Save);
        Assert.False(File.Exists(Path.Combine(outputDir, "model.e2k")));
        Assert.False(File.Exists(Path.Combine(outputDir, "run-metrics.json")));
    }

    [Fact]
    public async Task SharedSessionExportBoundsAnUnexpectedComFailure()
    {
        var model = CreateModel();
        var opener = new FakeModelOpener(new InvalidOperationException(new string('x', 900)));
        var service = new SnapshotExportService(null!, null!, null!, opener);

        var result = await service.SnapshotExportOnAppAsync(
            null!, model, OutputDir(), new SnapshotExportRequest());

        Assert.False(result.Success);
        Assert.Contains("snapshot-export", result.Error, StringComparison.Ordinal);
        Assert.Contains("openModel", result.Error, StringComparison.Ordinal);
        Assert.True(result.Error!.Length <= EtabsApiDiagnosticFormatter.TotalLimit);
        Assert.DoesNotContain(result.Error, char.IsControl);
    }

    [Fact]
    public void PreparePlacesEveryArtifactPathInsideTheRequestedOutputDirectory()
    {
        var model = CreateModel();
        var outputDir = OutputDir("fresh");

        var prep = SnapshotExportService.Prepare(model, outputDir, new SnapshotExportRequest());

        Assert.Null(prep.Error);
        var root = Path.GetFullPath(outputDir) + Path.DirectorySeparatorChar;
        foreach (var path in new[] { prep.E2kFile, prep.MaterialsDir, prep.MetadataPath, prep.MetricsPath })
        {
            Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("model.e2k", Path.GetFileName(prep.E2kFile));
        Assert.Equal("model-metadata.json", Path.GetFileName(prep.MetadataPath));
        Assert.Equal("run-metrics.json", Path.GetFileName(prep.MetricsPath));
    }

    [Fact]
    public void PrepareRejectsAMissingModelAnEmptyOutputDirectoryAndUnknownUnits()
    {
        Assert.StartsWith(
            "File not found:",
            SnapshotExportService.Prepare(
                Path.Combine(_directory, "missing.edb"),
                OutputDir(),
                new SnapshotExportRequest()).Error!,
            StringComparison.Ordinal);

        Assert.Contains(
            "OutputDir",
            SnapshotExportService.Prepare(CreateModel(), "   ", new SnapshotExportRequest()).Error!,
            StringComparison.Ordinal);

        Assert.NotNull(SnapshotExportService.Prepare(
            CreateModel(),
            OutputDir(),
            new SnapshotExportRequest { Units = "NOT_A_PRESET" }).Error);
    }

    [Fact]
    public void ExportedE2KMustBePresentAndNonEmpty()
    {
        Directory.CreateDirectory(OutputDir());
        var missing = Path.Combine(OutputDir(), "missing.e2k");
        var empty = Path.Combine(OutputDir(), "empty.e2k");
        File.WriteAllText(empty, string.Empty);
        var populated = Path.Combine(OutputDir(), "model.e2k");
        File.WriteAllText(populated, "$ ETABS");

        var nonZeroReturn = SnapshotExportService.ValidateExportedE2K(9, populated);
        Assert.False(nonZeroReturn.Success);
        Assert.Contains("operation=cFile.ExportFile", nonZeroReturn.Error, StringComparison.Ordinal);
        Assert.Contains("returnCode=9", nonZeroReturn.Error, StringComparison.Ordinal);

        var absent = SnapshotExportService.ValidateExportedE2K(0, missing);
        Assert.False(absent.Success);
        Assert.Contains("missing.e2k", absent.Error, StringComparison.Ordinal);

        var blank = SnapshotExportService.ValidateExportedE2K(0, empty);
        Assert.False(blank.Success);
        Assert.Contains("empty", blank.Error, StringComparison.OrdinalIgnoreCase);

        var ok = SnapshotExportService.ValidateExportedE2K(0, populated);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(new FileInfo(populated).Length, ok.Data);
    }

    private sealed class FakeModelOpener : IEtabsModelOpener
    {
        private readonly Result<ModelOpenOutcome>? _result;
        private readonly Exception? _throw;

        public FakeModelOpener(Result<ModelOpenOutcome> result) => _result = result;

        public FakeModelOpener(Exception failure) => _throw = failure;

        public int Calls { get; private set; }
        public string? FilePath { get; private set; }
        public bool Save { get; private set; }

        public Result<ModelOpenOutcome> Open(ETABSApplication app, string filePath, bool save)
        {
            Calls++;
            FilePath = filePath;
            Save = save;
            if (_throw is not null) throw _throw;
            return _result!;
        }
    }
}
