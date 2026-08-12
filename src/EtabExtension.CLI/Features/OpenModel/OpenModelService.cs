// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Features.OpenModel.Models;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using EtabSharp.Core;

namespace EtabExtension.CLI.Features.OpenModel;

public class OpenModelService : IOpenModelService
{
    private static async Task<int?> WaitForNewPidAsync(HashSet<int> existingPids)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);

        while (true)
        {
            var instances = ETABSWrapper.GetAllRunningInstances();
            var candidates = instances.Where(i => !existingPids.Contains(i.ProcessId)).ToList();
            if (candidates.Count == 1) return candidates[0].ProcessId;
            if (candidates.Count > 1) return null;

            if (DateTime.UtcNow >= deadline)
                return null;

            await Task.Delay(200);
        }
    }

    public async Task<Result<OpenModelData>> OpenModelAsync(
        string filePath, bool save, bool newInstance)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<OpenModelData>($"File not found: {filePath}");

        if (!filePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<OpenModelData>("Only .edb files can be opened");

        return newInstance
            ? await OpenInNewInstanceAsync(filePath)
            : await OpenInRunningInstanceAsync(filePath, save);
    }

    // ── Daemon — open into the shared serve-session instance ─────────────────
    // Like Mode A but against a caller-owned app: no Connect, no Dispose.

    public async Task<Result<OpenModelData>> OpenModelOnAppAsync(
        ETABSApplication app, string filePath, bool save)
    {
        await Task.CompletedTask;

        if (!File.Exists(filePath))
            return Result.Fail<OpenModelData>($"File not found: {filePath}");

        if (!filePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<OpenModelData>("Only .edb files can be opened");

        return OpenOnAttachedModel(
            filePath,
            save,
            () => app.Model.ModelInfo.GetModelFilepath(),
            currentPath => app.Model.Files.SaveFile(currentPath),
            targetPath => app.Model.Files.OpenFile(targetPath));
    }

    internal static Result<OpenModelData> OpenOnAttachedModel(
        string filePath,
        bool save,
        Func<string?> getCurrentPath,
        Func<string, int> saveFile,
        Func<string, int> openFile)
    {
        var activeOperation = "cSapModel.GetModelFilename";
        try
        {
            var currentPath = getCurrentPath();
            var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

            if (hasCurrentFile && save)
            {
                Console.Error.WriteLine("ℹ Saving current file...");
                activeOperation = "cFile.Save";
                var saveReturnCode = saveFile(currentPath!);
                if (saveReturnCode != 0)
                {
                    return Result.Fail<OpenModelData>(
                        EtabsApiDiagnosticFormatter.ApiReturn(
                            activeOperation,
                            saveReturnCode));
                }
            }

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            activeOperation = "cFile.OpenFile";
            var openReturnCode = openFile(filePath);
            if (openReturnCode != 0)
            {
                return Result.Fail<OpenModelData>(
                    EtabsApiDiagnosticFormatter.ApiReturn(
                        activeOperation,
                        openReturnCode));
            }

            Console.Error.WriteLine($"✓ Opened: {Path.GetFileName(filePath)}");
            return Result.Ok(new OpenModelData
            {
                FilePath = filePath,
                PreviousFilePath = hasCurrentFile ? currentPath : null,
                Pid = null,
                OpenedInNewInstance = false
            });
        }
        catch (Exception exception)
        {
            return Result.Fail<OpenModelData>(
                EtabsApiDiagnosticFormatter.Exception(
                    activeOperation,
                    exception));
        }
    }

    // ── Mode A — open in the user's running ETABS ────────────────────────────

    private static async Task<Result<OpenModelData>> OpenInRunningInstanceAsync(
        string filePath, bool save)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            var instances = ETABSWrapper.GetAllRunningInstances();
            if (instances.Count != 1)
                return Result.Fail<OpenModelData>(
                    $"Expected exactly one ETABS instance, found {instances.Count}. Start one instance or use etab-cli serve.");
            var pid = instances[0].ProcessId;
            app = ETABSWrapper.ConnectToProcess(pid);
            if (app is null)
                return Result.Fail<OpenModelData>("ETABS process identity was selected but COM attach failed.");

            var currentPath = app.Model.ModelInfo.GetModelFilepath();
            var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

            Console.Error.WriteLine(
                $"ℹ Currently open: {(hasCurrentFile ? Path.GetFileName(currentPath) : "(none)")}");

            if (hasCurrentFile && save)
            {
                Console.Error.WriteLine("ℹ Saving current file...");
                int saveRet = app.Model.Files.SaveFile(currentPath!);
                if (saveRet != 0)
                    Console.Error.WriteLine($"⚠ SaveFile returned {saveRet} — continuing");
                else
                    Console.Error.WriteLine("✓ Saved");
            }

            // OpenFile() closes the current model and opens the new one atomically.
            // No InitializeNewModel needed — OpenFile handles the transition cleanly.
            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<OpenModelData>($"OpenFile failed (ret={openRet})");

            Console.Error.WriteLine($"✓ Opened: {Path.GetFileName(filePath)}");

            return Result.Ok(new OpenModelData
            {
                FilePath = filePath,
                PreviousFilePath = hasCurrentFile ? currentPath : null,
                Pid = pid,
                OpenedInNewInstance = false
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<OpenModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            app?.Dispose(); // Mode A: release COM only — ETABS keeps running
        }
    }

    // ── Mode B variant — spawn a new visible ETABS instance ──────────────────
    // startApplication=true so ETABS window appears (user-visible, not hidden).
    // We do NOT call ApplicationExit — user controls this instance going forward.

    private static async Task<Result<OpenModelData>> OpenInNewInstanceAsync(string filePath)
    {
        await Task.CompletedTask;

        ETABSApplication? app = null;
        try
        {
            var existingPids = ETABSWrapper.GetAllRunningInstances()
                .Select(instance => instance.ProcessId)
                .ToHashSet();
            Console.Error.WriteLine("ℹ Starting new ETABS instance...");
            app = ETABSWrapper.CreateNew(startApplication: true);
            if (app is null)
                return Result.Fail<OpenModelData>("Failed to start new ETABS instance.");

            // Do NOT hide — user asked for a visible new instance
            Console.Error.WriteLine($"✓ New ETABS instance started (v{app.FullVersion})");

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(filePath)}");
            int openRet = app.Model.Files.OpenFile(filePath);
            if (openRet != 0)
                return Result.Fail<OpenModelData>($"OpenFile failed (ret={openRet})");

            var pid = await WaitForNewPidAsync(existingPids);
            if (pid is null)
                return Result.Fail<OpenModelData>(
                    "ETABS opened the file, but the new process PID could not be confirmed within 3 seconds.");

            Console.Error.WriteLine($"✓ Opened in new instance (PID {pid}): {Path.GetFileName(filePath)}");

            return Result.Ok(new OpenModelData
            {
                FilePath = filePath,
                PreviousFilePath = null,
                Pid = pid,
                OpenedInNewInstance = true
            });
        }
        catch (Exception ex)
        {
            return Result.Fail<OpenModelData>($"ETABS COM error: {ex.Message}");
        }
        finally
        {
            // New instance (Mode B): Release the COM RCW immediately without Dispose.
            // Dispose would prematurely terminate ETABS; we let it run independently.
            // However, we must release the RCW (Runtime Callable Wrapper) to avoid a multi-second
            // hang from COM finalization. Marshal.ReleaseComObject forces immediate cleanup of the
            // proxy while leaving the out-of-process server untouched.
            if (app is not null)
            {
                try
                {
                    // Marshal.ReleaseComObject is Windows-only; sidecar only runs on Windows
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                        System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(app);
                    }
                }
                catch
                {
                    // Best-effort: if release fails, continue anyway
                }
                app = null;
            }
        }
    }
}
