using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.GetStatus;

public class GetStatusService : IGetStatusService
{
    // One-shot: probe whatever ETABS is running and COM-attach to report on it.
    public async Task<Result<GetStatusData>> GetStatusAsync()
    {
        await Task.CompletedTask;

        if (!ETABSWrapper.IsRunning())
            return Result.Ok(new GetStatusData
            {
                IsRunning = false,
                Ownership = EtabsInstanceOwnership.None
            });

        var instances = ETABSWrapper.GetAllRunningInstances();
        if (instances.Count != 1)
            return Result.Fail<GetStatusData>(
                $"Expected exactly one ETABS instance, found {instances.Count}. Use etab-cli serve for managed ownership.");
        var pid = instances[0].ProcessId;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.ConnectToProcess(pid);
            if (app is null)
                return Result.Fail<GetStatusData>(
                    "ETABS is running but COM attach failed. Try restarting ETABS.");

            Console.Error.WriteLine($"✓ Connected to ETABS v{app.FullVersion} (PID {pid})");
            return Result.Ok(BuildStatusData(
                app,
                pid,
                EtabsInstanceOwnership.External,
                [pid]));
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    // Daemon: report on the shared serve-session instance (no attach, no dispose).
    public Result<GetStatusData> GetStatusOnApp(ETABSApplication app, int? pid = null)
    {
        try
        {
            return Result.Ok(BuildStatusData(
                app,
                pid,
                pid.HasValue ? EtabsInstanceOwnership.Managed : EtabsInstanceOwnership.External,
                pid.HasValue ? [pid.Value] : []));
        }
        catch (Exception ex)
        {
            return Result.Fail<GetStatusData>($"ETABS COM error: {ex.Message}");
        }
    }

    private static GetStatusData BuildStatusData(
        ETABSApplication app,
        int? pid,
        EtabsInstanceOwnership ownership,
        IReadOnlyList<int> observedPids)
    {
        var reportedModelPath = EtabsCurrentModelPath.Read(app);
        var isLocked = app.Model.ModelInfo.IsLocked();
        var isAnalyzed = app.Model.Analyze.GetCaseStatus().Any(cs => cs.IsFinished);

        UnitSystemInfo? unitSystem = null;
        try
        {
            var units = app.Model.Units.GetPresentUnits();
            unitSystem = new UnitSystemInfo
            {
                Force = ToForceSymbol(units.Force),
                Length = ToLengthSymbol(units.Length),
                Temperature = ToTemperatureSymbol(units.Temperature),
                IsUs = units.IsUS,
                IsMetric = units.IsMetric
            };
            Console.Error.WriteLine(
                $"ℹ Units: {unitSystem.Force}/{unitSystem.Length}/{unitSystem.Temperature}  isUS={units.IsUS}  isMetric={units.IsMetric}");
        }
        catch (Exception ex)
        {
            // Not fatal — unit read failing should not block status
            Console.Error.WriteLine($"⚠ Could not read units: {ex.Message}");
        }

        return ComposeStatus(
            reportedModelPath,
            pid,
            ownership,
            observedPids,
            app.FullVersion,
            isLocked,
            isAnalyzed,
            unitSystem);
    }

    /// <summary>
    /// Turns what ETABS reported into the published status.
    ///
    /// <para><c>isModelOpen</c> is derived from the reported value naming a FILE, never
    /// from that value merely being non-empty: a folder answer is non-empty and would
    /// otherwise publish "a model is open" while naming no model. Rust compares
    /// <c>openFilePath</c> against a working <c>.edb</c> by whole-path equality, so a
    /// value that names no file must be published as no value at all.</para>
    /// </summary>
    internal static GetStatusData ComposeStatus(
        string? reportedModelPath,
        int? pid,
        EtabsInstanceOwnership ownership,
        IReadOnlyList<int> observedPids,
        string? etabsVersion,
        bool? isLocked,
        bool? isAnalyzed,
        UnitSystemInfo? unitSystem)
    {
        var openFilePath = EtabsCurrentModelPath.ResolveOpenFile(reportedModelPath);
        if (EtabsCurrentModelPath.ReportedWithoutFileName(reportedModelPath))
        {
            Console.Error.WriteLine(
                $"⚠ ETABS names no current model file (reported '{EtabsCurrentModelPath.Describe(reportedModelPath)}')");
        }

        return new GetStatusData
        {
            IsRunning = true,
            Pid = pid,
            Ownership = ownership,
            ObservedPids = observedPids,
            EtabsVersion = etabsVersion,
            OpenFilePath = openFilePath,
            IsModelOpen = openFilePath is not null,
            IsLocked = isLocked,
            IsAnalyzed = isAnalyzed,
            UnitSystem = unitSystem
        };
    }

    // ── Unit helpers — copied verbatim from demo script ───────────────────────

    private static string ToForceSymbol(eForce force) => force switch
    {
        eForce.lb => "lb",
        eForce.kip => "kip",
        eForce.N => "N",
        eForce.kN => "kN",
        eForce.kgf => "kgf",
        eForce.tonf => "tonf",
        _ => force.ToString()
    };

    private static string ToLengthSymbol(eLength length) => length switch
    {
        eLength.inch => "in",
        eLength.ft => "ft",
        eLength.mm => "mm",
        eLength.cm => "cm",
        eLength.m => "m",
        _ => length.ToString()
    };

    private static string ToTemperatureSymbol(eTemperature temperature) => temperature switch
    {
        eTemperature.F => "F",
        eTemperature.C => "C",
        _ => temperature.ToString()
    };
}
