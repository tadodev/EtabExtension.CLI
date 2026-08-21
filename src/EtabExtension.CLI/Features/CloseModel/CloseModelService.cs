// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Features.CloseModel;

public class CloseModelService : ICloseModelService
{
    public async Task<Result<CloseModelData>> CloseModelAsync(bool save)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<CloseModelData>("ETABS is not running.");

            return CloseOnApp(app, save);
        }
        catch (Exception ex)
        {
            return Result.Fail<CloseModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    public Task<Result<CloseModelData>> CloseModelOnAppAsync(ETABSApplication app, bool save)
    {
        try { return Task.FromResult(CloseOnApp(app, save)); }
        catch (Exception ex) { return Task.FromResult(Result.Fail<CloseModelData>($"ETABS COM error: {ex.Message}")); }
    }

    private static Result<CloseModelData> CloseOnApp(ETABSApplication app, bool save)
    {
        var reportedPath = EtabsCurrentModelPath.Read(app);
        var currentFile = EtabsCurrentModelPath.ResolveOpenFile(reportedPath);

        Console.Error.WriteLine(
            $"ℹ Currently open: {(currentFile is null ? "(none)" : Path.GetFileName(currentFile))}");

        return CompleteClose(
            reportedPath,
            save,
            path =>
            {
                Console.Error.WriteLine("ℹ Saving...");
                var saveRet = app.Model.Files.SaveFile(path);
                if (saveRet == 0)
                {
                    Console.Error.WriteLine("✓ Saved");
                }
                return saveRet;
            },
            units =>
            {
                // InitializeNewModel() confirmed: clears workspace without triggering
                // Save dialog even on modified models. Rust decides save/no-save.
                var initRet = app.Model.ModelInfo.InitializeNewModel(units);
                if (initRet == 0)
                {
                    Console.Error.WriteLine("✓ Workspace cleared");
                }
                return initRet;
            });
    }

    /// <summary>
    /// The close sequence over injectable CSI calls.
    ///
    /// <para><paramref name="reportedPath"/> is what ETABS answered for "which model is
    /// current", and only a value that names a FILE may reach <c>cFile.Save</c>. Saving
    /// to a folder is not a harmless no-op: at best the call fails, at worst ETABS writes
    /// an extensionless file named after the folder.</para>
    ///
    /// <para>A non-blank answer that names no file therefore fails a requested save
    /// outright rather than silently skipping it, while an unsaved close still clears the
    /// workspace, because that is the recovery path and it does not depend on the path. A
    /// BLANK answer is the separate, long-standing case and keeps its behavior: nothing is
    /// loaded, so a requested save has nothing to write and the close succeeds with
    /// <c>wasSaved: false</c>.</para>
    /// </summary>
    internal static Result<CloseModelData> CompleteClose(
        string? reportedPath,
        bool save,
        Func<string, int> saveFile,
        Func<eUnits, int> initializeNewModel)
    {
        var currentFile = EtabsCurrentModelPath.ResolveOpenFile(reportedPath);
        if (EtabsCurrentModelPath.ReportedWithoutFileName(reportedPath))
        {
            var reported = EtabsCurrentModelPath.Describe(reportedPath);
            if (save)
            {
                return Result.Fail<CloseModelData>(EtabsApiDiagnosticFormatter.Bounded(
                    $"Save requested but ETABS names no current model file (reported '{reported}'); " +
                    "model remains open"));
            }

            Console.Error.WriteLine($"⚠ ETABS names no current model file (reported '{reported}')");
        }

        var hasFile = currentFile is not null;
        if (save && hasFile)
        {
            var saveRet = saveFile(currentFile!);
            if (saveRet != 0)
            {
                return Result.Fail<CloseModelData>(
                    $"SaveFile failed (ret={saveRet}); model remains open");
            }
        }

        var initRet = initializeNewModel(eUnits.kip_ft_F);
        if (initRet != 0)
        {
            return Result.Fail<CloseModelData>($"InitializeNewModel failed (ret={initRet})");
        }

        return Result.Ok(new CloseModelData
        {
            ClosedFilePath = currentFile,
            WasSaved = save && hasFile
        });
    }
}
