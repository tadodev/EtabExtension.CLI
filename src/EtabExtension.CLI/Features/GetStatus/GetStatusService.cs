using EtabExtension.CLI.Features.GetStatus.Models;
using EtabExtension.CLI.Shared.Common;
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
        var openFilePath = app.Model.ModelInfo.GetModelFilepath();
        var isModelOpen = !string.IsNullOrEmpty(openFilePath);
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

        return new GetStatusData
        {
            IsRunning = true,
            Pid = pid,
            Ownership = ownership,
            ObservedPids = observedPids,
            EtabsVersion = app.FullVersion,
            OpenFilePath = isModelOpen ? openFilePath : null,
            IsModelOpen = isModelOpen,
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
