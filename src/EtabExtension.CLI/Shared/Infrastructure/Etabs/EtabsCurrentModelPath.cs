// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// The Mode A read of "which model file is loaded in this ETABS right now", plus the one
/// rule for deciding whether the answer actually names a file.
///
/// <para><c>get-status</c>, <c>unlock-model</c> and <c>close-model</c> all read through
/// here so the choice of CSI call cannot drift per feature: status publishes the value as
/// <c>openFilePath</c>, unlock compares it against the requested file, and close hands it
/// to <c>cFile.Save</c>. All three are wrong in the same way if the read is wrong. (The
/// model-open boundary, <c>EtabsModelOpen</c>, owns the same read for the commands that
/// open a model; folding it onto this type is deliberate follow-up work, not an
/// oversight — see the allow-list in <c>EtabsModelPathWiringTests</c> for the full set of
/// types permitted to make this call.)</para>
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
/// whole-path equality. Anything that is not a fully-qualified file path can never match,
/// so publishing one does not read as "a different model" — it reads as "no model open",
/// silently.</para>
/// </summary>
public static class EtabsCurrentModelPath
{
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
    /// Narrows a reported current-model value to one usable as a model file path, or null
    /// when it is not.
    ///
    /// <para>Three things must hold, and the folder shapes fail at least one of them:</para>
    /// <list type="bullet">
    /// <item><description>Fully qualified. A bare <c>sample_v2.EDB</c> would resolve
    /// against this process's working directory, which is not ETABS's — and Rust
    /// normalizes separators without absolutizing, so a relative answer compares unequal
    /// to the absolute working file no matter what it names.</description></item>
    /// <item><description>Names a file. Rejects <c>D:\Work\tadoEng\TestModel\</c>,
    /// <c>D:\</c> and <c>\\server\share</c>, whose last segment is empty.</description></item>
    /// <item><description>Carries an extension. A separator-less folder such as
    /// <c>D:\Models</c> passes the first two — it names <c>Models</c> — and is the same
    /// defect in a different folder shape. ETABS models are always <c>.edb</c>, so the
    /// extension is what separates them. Checked without touching disk: this runs on
    /// every status read, and a filesystem probe would cost a syscall per call and race
    /// its own answer.</description></item>
    /// </list>
    ///
    /// <para>Residual, accepted: a separator-less folder that happens to carry a dot
    /// (<c>D:\Models.v2</c>) still passes. Narrowing the rule to the <c>.edb</c>
    /// extension itself would close it, and is available if a live run ever shows that
    /// shape.</para>
    /// </summary>
    public static string? ResolveOpenFile(string? reported)
    {
        var trimmed = reported?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        if (string.IsNullOrEmpty(Path.GetFileName(trimmed)))
        {
            return null;
        }

        return Path.HasExtension(trimmed) ? trimmed : null;
    }

    /// <summary>True when the reported value is usable as a model file path.</summary>
    public static bool NamesAFile(string? reported) => ResolveOpenFile(reported) is not null;

    /// <summary>
    /// True when ETABS answered with something non-blank that still names no usable file.
    /// Worth reporting: it means the model state cannot be trusted, not that nothing is
    /// open.
    /// </summary>
    public static bool ReportedWithoutFileName(string? reported) =>
        !string.IsNullOrWhiteSpace(reported) && ResolveOpenFile(reported) is null;

    /// <summary>The reported value as it should appear in operator-facing diagnostics.</summary>
    public static string Describe(string? reported) =>
        string.IsNullOrWhiteSpace(reported) ? "(none)" : reported.Trim();
}
