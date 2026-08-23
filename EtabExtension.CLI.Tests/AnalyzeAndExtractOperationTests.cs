using System.Text.Json;
using EtabExtension.CLI.Features.AnalyzeAndExtract;
using EtabExtension.CLI.Features.AnalyzeAndExtract.Models;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Features.Serve;
using EtabExtension.CLI.Features.Serve.Operations;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabExtension.CLI.Shared.Infrastructure.Etabs.Session;
using EtabSharp.Core;
using Xunit;

namespace EtabExtension.CLI.Tests;

public sealed class AnalyzeAndExtractOperationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "etab-cli-analyze-operation-tests", Guid.NewGuid().ToString("N"));
    private OperationManager? _manager;

    [Fact]
    public async Task Forwards_the_existing_flattened_payload_through_the_generic_envelope()
    {
        var session = new FakeSession();
        var analyze = new FakeAnalyzeService();
        var definition = new AnalyzeAndExtractOperation(
            session,
            analyze,
            new FakeStatusService(),
            new CachedSessionStatus());
        _manager = new OperationManager(
            new StaExecutionWorker(),
            new OperationEventJournalFactory(_directory),
            new SystemOperationClock(),
            WorkEnvelopeFixtures.Consented(session),
            [definition]);
        var payload = JsonSerializer.Deserialize<JsonElement>(
            """{"filePath":"C:\\v1\\model.edb","outputDir":"C:\\v1\\results","units":"US_Kip_Ft","cases":["DEAD"],"tables":{"baseReactions":{"loadCases":["*"]}}}""");

        var started = _manager.Start("analyze-and-extract", payload);
        var result = Assert.IsType<Result<AnalyzeAndExtractData>>(await _manager.WaitAsync(
            started.Data!.OperationId,
            TestContext.Current.CancellationToken));

        Assert.True(result.Success);
        Assert.Equal(@"C:\v1\model.edb", analyze.FilePath);
        Assert.Equal(@"C:\v1\results", analyze.OutputDir);
        Assert.Equal("US_Kip_Ft", analyze.Request!.Units);
        Assert.NotNull(analyze.Progress);
        Assert.Equal(1, session.GetOrStartCalls);
        // CLI #22: analyze-and-extract is background work. It uses the shared session and
        // never asks for it to be put on screen.
        Assert.Equal(0, session.RevealCalls);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeAnalyzeService : IAnalyzeAndExtractService
    {
        public string? FilePath { get; private set; }
        public string? OutputDir { get; private set; }
        public AnalyzeAndExtractRequest? Request { get; private set; }
        public IEtabsOperationProgress? Progress { get; private set; }

        public Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractAsync(
            string filePath,
            string outputDir,
            AnalyzeAndExtractRequest request) => throw new NotSupportedException();

        public Task<Result<AnalyzeAndExtractData>> AnalyzeAndExtractOnAppAsync(
            ETABSApplication app,
            string filePath,
            string outputDir,
            AnalyzeAndExtractRequest request,
            IEtabsOperationProgress? progress = null)
        {
            FilePath = filePath;
            OutputDir = outputDir;
            Request = request;
            Progress = progress;
            return Task.FromResult(Result.Ok(new AnalyzeAndExtractData
            {
                FilePath = filePath,
                OutputDir = outputDir
            }));
        }
    }

    private sealed class FakeStatusService : IGetStatusService
    {
        public Task<Result<GetStatusData>> GetStatusAsync() => throw new NotSupportedException();
        public Result<GetStatusData> GetStatusOnApp(ETABSApplication app, int? pid = null) =>
            Result.Ok(new GetStatusData { IsRunning = true, Pid = pid });
    }

    private sealed class FakeSession : IEtabsSession
    {
        /// <summary>CLI #24 certification the test can force to fail.</summary>
        public Result ExposureCertification { get; set; } = Result.Ok();

        public List<string> Stages { get; } = [];

        public Result CertifyNoUnconsentedExposure() => ExposureCertification;

        public void MarkVisibilityStage(string stage) => Stages.Add(stage);

        public int GetOrStartCalls { get; private set; }
        public bool IsStarted => true;
        public int? ProcessId => 42;
        public ETABSApplication GetOrStart()
        {
            GetOrStartCalls++;
            return null!;
        }
        public IManagedEtabsApplication GetOrStartOwned() => throw new NotSupportedException();
        public int RevealCalls { get; private set; }
        public Result RevealForExplicitUserRequest()
        {
            RevealCalls++;
            return Result.Ok();
        }
        public ManagedEtabsShutdownResult Shutdown() => throw new NotSupportedException();
        public void Dispose() { }
    }
}
