// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// The one read of "which model file is loaded in this ETABS right now", plus the one
/// rule for deciding whether the answer actually names a file.
///
/// <para>Every Mode A command reads the current model through here so the choice of CSI
/// call cannot drift per feature: <c>get-status</c> publishes it as
/// <c>openFilePath</c>, <c>unlock-model</c> compares it against the requested file, and
/// <c>close-model</c> hands it to <c>cFile.Save</c>. All three are wrong in the same way
/// if the read is wrong.</para>
///
/// <para><b>Use <c>GetModelFilename(includePath: true)</c>, never
/// <c>GetModelFilepath()</c>.</b> The distinction is an OBSERVED behavior, not a
/// documented one — the documentation does not distinguish them, so do not resolve this
/// from docs. A supervised live run against
/// <c>D:\Work\tadoEng\TestModel\sample_v2.EDB</c> (ETABS 23.3.0) observed
/// <c>get-status</c> reporting <c>openFilePath</c> as <c>"D:\Work\tadoEng\TestModel\"</c>
/// — the folder, trailing separator, no file name — while the same session's model was
/// <c>sample_v2.EDB</c>. Re-verify empirically before trusting <c>GetModelFilepath</c>
/// as a file path anywhere.</para>
///
/// <para>Downstream, Rust compares this value against a working <c>.edb</c> file by
/// whole-path equality. A folder never matches a file, so a folder answer does not read
/// as "a different model" — it reads as "no model open", silently.</para>
/// </summary>
public static class EtabsCurrentModelPath
{
    /// <summary>The CSI operation named in diagnostics when this read fails.</summary>
    public const string ReadOperation = "cSapModel.GetModelFilename";

    /// <summary>
    /// Reads the current model's full file path from a caller-owned application. Owns no
    /// process lifecycle: no attach, no create, no exit, no dispose.
    /// </summary>
    public static string? Read(ETABSApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Model.ModelInfo.GetModelFilename(includePath: true);
    }

    /// <summary>
    /// Narrows a reported current-model value to one that names a file, or null when it
    /// names none — blank (no model loaded) or a bare folder (the defect signature).
    /// </summary>
    public static string? ResolveOpenFile(string? reported)
    {
        var trimmed = reported?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return string.IsNullOrEmpty(Path.GetFileName(trimmed)) ? null : trimmed;
    }

    /// <summary>True when the reported value names a model file.</summary>
    public static bool NamesAFile(string? reported) => ResolveOpenFile(reported) is not null;

    /// <summary>
    /// True when ETABS answered with something non-blank that still names no file. Worth
    /// reporting: it means the model state cannot be trusted, not that nothing is open.
    /// </summary>
    public static bool ReportedWithoutFileName(string? reported) =>
        !string.IsNullOrWhiteSpace(reported) && ResolveOpenFile(reported) is null;

    /// <summary>The reported value as it should appear in operator-facing diagnostics.</summary>
    public static string Describe(string? reported) =>
        string.IsNullOrWhiteSpace(reported) ? "(none)" : reported.Trim();
}
