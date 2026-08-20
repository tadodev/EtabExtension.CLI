using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.SnapshotExport.Models;
using Xunit;

namespace EtabExtension.CLI.Tests;

// Guards the serve request contract against the Rust ext-sidecar client's
// `request_from_args` (crates/ext-sidecar/src/client.rs): for analyze/snapshot it
// emits filePath/outputDir + the per-command request fields FLATTENED at the top
// level (not nested), and open-model uses `saveOnClose`. A mismatch here silently
// breaks the real Rust<->C# call even when both sides' own tests pass.
public class ServeContractTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Flattened_analyze_request_yields_locator_and_request_from_the_same_object()
    {
        // Exactly what request_from_args produces for "analyze-and-extract".
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"filePath":"C:\\v1\\model.edb","outputDir":"C:\\v1\\results","units":"US_Kip_Ft","cases":["DEAD"],"tables":{"baseReactions":{"loadCases":["*"]}},"extractionProfile":"full"}""");

        var locator = element.Deserialize<ServeFileLocator>(Opts)!;
        var request = element.Deserialize<AnalyzeAndExtractRequest>(Opts)!;

        Assert.Equal(@"C:\v1\model.edb", locator.FilePath);
        Assert.Equal(@"C:\v1\results", locator.OutputDir);
        Assert.Equal("US_Kip_Ft", request.Units);
    }

    [Fact]
    public void FlattenedSnapshotExportRequestYieldsLocatorAndRequestFromTheSameObject()
    {
        // Exactly what request_from_args produces for "snapshot-export" — the shape the
        // desktop Commit path sends.
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"filePath":"D:\\Work\\tadoEng\\TestModel\\sample_v2.EDB","outputDir":"D:\\snapshot","units":"US_Kip_Ft","e2kFileName":"model.e2k","materialsDirName":"materials","metadataFileName":"model-metadata.json","metricsFileName":"run-metrics.json","extractionProfile":"snapshot","tables":{}}""");

        var locator = element.Deserialize<ServeFileLocator>(Opts)!;
        var request = element.Deserialize<SnapshotExportRequest>(Opts)!;

        Assert.Equal(@"D:\Work\tadoEng\TestModel\sample_v2.EDB", locator.FilePath);
        Assert.Equal(@"D:\snapshot", locator.OutputDir);
        Assert.Equal("US_Kip_Ft", request.Units);
        Assert.Equal("model.e2k", request.E2KFileName);
        Assert.Equal("materials", request.MaterialsDirName);
        Assert.Equal("model-metadata.json", request.MetadataFileName);
        Assert.Equal("run-metrics.json", request.MetricsFileName);
        Assert.Equal("snapshot", request.ExtractionProfile);
    }

    [Fact]
    public void Open_model_reads_the_save_on_close_flag()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"filePath":"C:\\v1\\model.edb","saveOnClose":true,"newInstance":false}""");

        var request = element.Deserialize<ServeOpenModelRequest>(Opts)!;

        Assert.Equal(@"C:\v1\model.edb", request.FilePath);
        Assert.True(request.SaveOnClose);
    }

    [Fact]
    public void Run_analysis_reads_the_flattened_shared_session_request()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"filePath":"C:\\v1\\model.edb","cases":["DEAD"],"units":"SI_kN_m_C"}""");

        var request = element.Deserialize<ServeRunAnalysisRequest>(Opts)!;

        Assert.Equal(@"C:\v1\model.edb", request.FilePath);
        Assert.Equal(["DEAD"], request.Cases);
        Assert.Equal("SI_kN_m_C", request.Units);
    }
}
