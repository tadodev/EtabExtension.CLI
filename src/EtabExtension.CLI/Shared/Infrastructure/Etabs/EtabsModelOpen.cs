// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Common;
using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// What the canonical model-open primitive proved: which path was requested, the
/// full path actually handed to CSI, and which model (if any) was loaded before.
/// </summary>
public sealed record ModelOpenOutcome(
    string RequestedPath,
    string ResolvedPath,
    string? PreviousFilePath);

/// <summary>
/// The one managed-app model-open boundary. Injected wherever a shared-session
/// command needs a model loaded so that <c>open-model</c> and <c>snapshot-export</c>
/// cannot grow divergent OpenFile implementations.
/// </summary>
public interface IEtabsModelOpener
{
    /// <summary>
    /// Opens <paramref name="filePath"/> into an already-owned ETABS application.
    /// Never connects, creates, hides, exits, or disposes anything — the caller owns
    /// the process lifecycle.
    /// </summary>
    Result<ModelOpenOutcome> Open(ETABSApplication app, string filePath, bool save);
}

/// <inheritdoc cref="IEtabsModelOpener"/>
public sealed class EtabsModelOpener : IEtabsModelOpener
{
    public Result<ModelOpenOutcome> Open(ETABSApplication app, string filePath, bool save) =>
        EtabsModelOpen.OpenOnApp(app, filePath, save);
}

/// <summary>
/// The canonical managed-app model-open primitive.
///
/// <para>Four commands open through here today: <c>open-model</c>,
/// <c>snapshot-export</c>, <c>analyze-and-extract</c> and
/// <c>read-model-metadata</c>. Among those, validation, return-code handling,
/// bounded COM diagnostics, current-file handling, and the post-open confirmation
/// cannot drift.</para>
///
/// <para>This is NOT yet every shared-session command. <c>extract-results</c>,
/// <c>extract-materials</c>, <c>generate-e2k</c> and <c>run-analysis</c> still call
/// <c>cFile.OpenFile</c> directly and still fail with the weaker, unbounded
/// <c>"OpenFile failed (ret=N)"</c> that names no CSI operation. That is deliberate:
/// converting them is broad serve-parity work explicitly deferred past Alpha, not an
/// oversight. Fold them in here when that work is scheduled — do not grow a second
/// open boundary for them.</para>
///
/// <para>It deliberately owns no process lifecycle: no ROT/PID attach, no
/// <c>CreateNew</c>, no <c>ApplicationExit</c>, no <c>Dispose</c>. It operates on an
/// application the caller already owns — the persistent one-daemon/one-managed-ETABS
/// architecture stays intact.</para>
///
/// <para>Cardex (<c>cFile.OpenFile</c>, ETABS v1 API) specifies the argument as
/// "the full path of a model file" and a zero return as the only success. A zero
/// return is necessary but not sufficient: it does not prove the requested model is
/// the one now loaded, so the primitive re-reads the current model file through
/// <c>cSapModel.GetModelFilename</c> — which Cardex documents as returning the full
/// path when <c>IncludePath</c> is true — and fails explicitly when a blank or
/// foreign model is current.</para>
/// </summary>
public static class EtabsModelOpen
{
    public const string ReadCurrentPathOperation = "cSapModel.GetModelFilename";
    public const string SaveOperation = "cFile.Save";
    public const string OpenOperation = "cFile.OpenFile";

    /// <summary>
    /// The shared pre-flight for every model-open caller. Returns null when the path
    /// is openable, otherwise the exact failure text.
    /// </summary>
    public static string? ValidateModelPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "A model file path is required";
        }

        if (!File.Exists(filePath))
        {
            return $"File not found: {filePath}";
        }

        return filePath.EndsWith(".edb", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Only .edb files can be opened";
    }

    /// <summary>Validates, then opens through the caller-owned application's CSI surface.</summary>
    public static Result<ModelOpenOutcome> OpenOnApp(
        ETABSApplication app,
        string filePath,
        bool save)
    {
        ArgumentNullException.ThrowIfNull(app);

        var validation = ValidateModelPath(filePath);
        if (validation is not null)
        {
            return Result.Fail<ModelOpenOutcome>(validation);
        }

        return OpenOnAttachedModel(
            filePath,
            save,
            // Read the current model with GetModelFilename, NOT GetModelFilepath.
            //
            // Cardex verifies only the call used here: cSapModel.GetModelFilename
            // returns the full path when IncludePath is true. It says nothing about
            // GetModelFilepath beyond "returns the filepath of the current model",
            // and EtabSharp's own XML doc calls that one the "full filepath" — so the
            // docs do not distinguish them.
            //
            // The distinction is an OBSERVED behavior, not a documented one. The
            // supervised live run of snapshot-export against
            // D:\Work\tadoEng\TestModel\sample_v2.EDB (ETABS 23.3.0) had this
            // primitive wired to GetModelFilepath and failed its own confirmation with
            // an empty opened= field; the same session's get-status reported
            // openFilePath as "D:\Work\tadoEng\TestModel\" — the FOLDER, with no
            // file name. Re-verify empirically before trusting GetModelFilepath as a
            // file path anywhere.
            () => app.Model.ModelInfo.GetModelFilename(includePath: true),
            currentPath => app.Model.Files.SaveFile(currentPath),
            targetPath => app.Model.Files.OpenFile(targetPath));
    }

    /// <summary>
    /// The COM sequence itself, expressed over injectable CSI calls so every stage
    /// failure is provable without ETABS. Path validation is the caller's job — this
    /// overload is also used by the one-shot attach paths, which validate earlier.
    /// </summary>
    internal static Result<ModelOpenOutcome> OpenOnAttachedModel(
        string filePath,
        bool save,
        Func<string?> getCurrentPath,
        Func<string, int> saveFile,
        Func<string, int> openFile)
    {
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception)
        {
            return Result.Fail<ModelOpenOutcome>(EtabsApiDiagnosticFormatter.Bounded(
                $"Model file path could not be resolved to a full path: {exception.Message}"));
        }

        var activeOperation = ReadCurrentPathOperation;
        try
        {
            var currentPath = getCurrentPath();
            var hasCurrentFile = !string.IsNullOrEmpty(currentPath);

            if (hasCurrentFile && save)
            {
                Console.Error.WriteLine("ℹ Saving current file...");
                activeOperation = SaveOperation;
                var saveReturnCode = saveFile(currentPath!);
                if (saveReturnCode != 0)
                {
                    return Result.Fail<ModelOpenOutcome>(
                        EtabsApiDiagnosticFormatter.ApiReturn(activeOperation, saveReturnCode));
                }
            }

            Console.Error.WriteLine($"ℹ Opening: {Path.GetFileName(resolvedPath)}");
            activeOperation = OpenOperation;
            var openReturnCode = openFile(resolvedPath);
            if (openReturnCode != 0)
            {
                return Result.Fail<ModelOpenOutcome>(
                    EtabsApiDiagnosticFormatter.ApiReturn(activeOperation, openReturnCode));
            }

            activeOperation = ReadCurrentPathOperation;
            var confirmation = ConfirmOpened(resolvedPath, getCurrentPath());
            if (confirmation is not null)
            {
                return Result.Fail<ModelOpenOutcome>(confirmation);
            }

            Console.Error.WriteLine($"✓ Opened: {Path.GetFileName(resolvedPath)}");
            return Result.Ok(new ModelOpenOutcome(
                filePath,
                resolvedPath,
                hasCurrentFile ? currentPath : null));
        }
        catch (Exception exception)
        {
            return Result.Fail<ModelOpenOutcome>(
                EtabsApiDiagnosticFormatter.Exception(activeOperation, exception));
        }
    }

    /// <summary>
    /// Turns "OpenFile returned zero" into "the requested model is loaded".
    ///
    /// <para>The packaged-RC defect this guards: ETABS started, the blank initialized
    /// model stayed loaded, and the export ran against nothing. A blank model reports
    /// no current file, so an empty answer is a hard failure — never a warning.</para>
    /// </summary>
    private static string? ConfirmOpened(string resolvedPath, string? openedPath)
    {
        var requestedName = Path.GetFileName(resolvedPath);
        var opened = openedPath?.Trim();
        if (string.IsNullOrEmpty(opened))
        {
            return NotConfirmed(
                requestedName,
                "(none)",
                "ETABS returned success but reports no current model file.");
        }

        var openedName = Path.GetFileName(opened);
        if (string.IsNullOrEmpty(openedName))
        {
            return NotConfirmed(
                requestedName,
                "(no file name)",
                $"ETABS returned success but names no current model file (reported '{opened}').");
        }

        if (!string.Equals(requestedName, openedName, StringComparison.OrdinalIgnoreCase))
        {
            return NotConfirmed(
                requestedName,
                openedName,
                "ETABS returned success but a different model is current.");
        }

        if (!string.Equals(opened, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            // Same file name, different spelling of the path (UNC, short path, mapped
            // drive). Not a failure — but it is worth seeing in the daemon log.
            Console.Error.WriteLine(
                $"⚠ ETABS reports the current model as '{opened}' for requested '{resolvedPath}'");
        }

        return null;
    }

    private static string NotConfirmed(string requestedName, string openedName, string detail) =>
        EtabsApiDiagnosticFormatter.Bounded(string.Join(
            "; ",
            EtabsApiErrorCodes.ModelOpenNotConfirmed,
            $"operation={OpenOperation}",
            $"requested={requestedName}",
            $"opened={openedName}",
            detail));
}
