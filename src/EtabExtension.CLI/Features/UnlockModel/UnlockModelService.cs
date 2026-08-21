// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.UnlockModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.UnlockModel;

public class UnlockModelService : IUnlockModelService
{
    public async Task<Result<UnlockModelData>> UnlockModelAsync(string filePath)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            app = ETABSWrapper.Connect();
            if (app is null)
                return Result.Fail<UnlockModelData>("ETABS is not running.");

            return UnlockOnApp(app, filePath);
        }
        catch (Exception ex)
        {
            return Result.Fail<UnlockModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    public Task<Result<UnlockModelData>> UnlockModelOnAppAsync(ETABSApplication app, string filePath)
    {
        try { return Task.FromResult(UnlockOnApp(app, filePath)); }
        catch (Exception ex) { return Task.FromResult(Result.Fail<UnlockModelData>($"ETABS COM error: {ex.Message}")); }
    }

    private static Result<UnlockModelData> UnlockOnApp(ETABSApplication app, string filePath)
    {
            // Guard: file must already be open
            var notOpen = ValidateRequestedFileIsOpen(EtabsCurrentModelPath.Read(app), filePath);
            if (notOpen is not null)
            {
                return Result.Fail<UnlockModelData>(notOpen);
            }

            bool wasLocked = app.Model.ModelInfo.IsLocked();
            Console.Error.WriteLine($"ℹ Lock status: {(wasLocked ? "locked" : "not locked")}");

            if (wasLocked)
            {
                app.Model.ModelInfo.SetLocked(false);

                // Verify it cleared
                if (app.Model.ModelInfo.IsLocked())
                    return Result.Fail<UnlockModelData>("SetLocked(false) call succeeded but model is still locked.");

                Console.Error.WriteLine("✓ Lock cleared");
            }

            return Result.Ok(new UnlockModelData
            {
                FilePath = filePath,
                WasLocked = wasLocked
            });
        }

    /// <summary>
    /// Returns null when the requested file is the model ETABS currently has open,
    /// otherwise the operator-facing refusal.
    ///
    /// <para>The comparison is against a value that names a FILE. A reported folder can
    /// never equal the requested <c>.edb</c>, so before this was fixed the guard refused
    /// every call and <c>unlock-model</c> was dead in serve mode.</para>
    /// </summary>
    internal static string? ValidateRequestedFileIsOpen(string? reportedPath, string filePath)
    {
        var currentFile = EtabsCurrentModelPath.ResolveOpenFile(reportedPath);
        if (PathsAreEqual(currentFile, filePath))
        {
            return null;
        }

        return $"File not open in ETABS. Currently open: '{EtabsCurrentModelPath.Describe(reportedPath)}'. " +
            $"Open the file first with: etab-cli open-model --file \"{filePath}\"";
    }

    private static bool PathsAreEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
    }
}
