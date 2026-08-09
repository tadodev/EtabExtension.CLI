// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.CloseModel.Models;
using EtabExtension.CLI.Shared.Common;
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
        var currentPath = app.Model.ModelInfo.GetModelFilepath();
        var hasFile = !string.IsNullOrWhiteSpace(currentPath);

        Console.Error.WriteLine($"ℹ Currently open: {(hasFile ? Path.GetFileName(currentPath) : "(none)")}");

        return CompleteClose(
            currentPath,
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

    internal static Result<CloseModelData> CompleteClose(
        string? currentPath,
        bool save,
        Func<string, int> saveFile,
        Func<eUnits, int> initializeNewModel)
    {
        var hasFile = !string.IsNullOrWhiteSpace(currentPath);
        if (save && hasFile)
        {
            var saveRet = saveFile(currentPath!);
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
            ClosedFilePath = hasFile ? currentPath : null,
            WasSaved = save && hasFile
        });
    }
}
