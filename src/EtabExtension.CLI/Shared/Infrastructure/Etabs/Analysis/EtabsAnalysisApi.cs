// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabSharp.Core;
using ETABSv1;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Analysis;

/// <summary>
/// The ETABS run-selection and analysis-status surface, expressed as RAW CSI return
/// codes.
///
/// <para>This deliberately bypasses the EtabSharp <c>AnalyzeManager</c> wrapper, which
/// throws <c>EtabsException</c> on a nonzero return. A thrown wrapper exception is
/// indistinguishable from any other COM fault at the call site, and CLI #31 existed
/// because the caller caught that exception and re-published it as the semantic claim
/// "case not found". Returning the raw integer keeps a FAILED CALL a failed call.</para>
///
/// <para>Verified against Cardex (<c>etabs-api-23.3</c>): every member below returns
/// zero on success and nonzero on failure, and none of them documents a nonzero value
/// that carries a meaning about the model.</para>
/// </summary>
public interface IEtabsAnalysisApi
{
    /// <summary>
    /// <c>cAnalyze.GetRunCaseFlag</c> — the model's own load-case census plus the run
    /// flag currently set on each one. This is the only authoritative answer to
    /// "which load cases does this model define" available on the analysis surface.
    /// </summary>
    int GetRunCaseFlags(out string[] caseNames, out bool[] run);

    /// <summary>
    /// <c>cAnalyze.SetRunCaseFlag</c>. When <paramref name="all"/> is true the run flag
    /// is set as specified by <paramref name="run"/> for ALL load cases and
    /// <paramref name="caseName"/> is ignored.
    /// </summary>
    int SetRunCaseFlag(string caseName, bool run, bool all);

    /// <summary>
    /// <c>cAnalyze.GetCaseStatus</c>. <c>statuses</c> holds integers 1-4:
    /// 1 = not run, 2 = could not start, 3 = not finished, 4 = finished.
    /// </summary>
    int GetCaseStatus(out string[] caseNames, out int[] statuses);

    /// <summary><c>cAnalyze.CreateAnalysisModel</c>.</summary>
    int CreateAnalysisModel();

    /// <summary>
    /// <c>cAnalyze.RunAnalysis</c> — runs the cases whose run flag is set. Cardex notes
    /// the analysis model is created automatically as part of this call.
    /// </summary>
    int RunAnalysis();
}

/// <summary>
/// Binds <see cref="IEtabsAnalysisApi"/> to a live <c>cSapModel.Analyze</c>.
/// </summary>
internal sealed class EtabsAnalysisApi(ETABSApplication application) : IEtabsAnalysisApi
{
    private cAnalyze Analyze => application.SapModel.Analyze;

    public int GetRunCaseFlags(out string[] caseNames, out bool[] run)
    {
        var count = 0;
        caseNames = [];
        run = [];
        var ret = Analyze.GetRunCaseFlag(ref count, ref caseNames, ref run);
        caseNames ??= [];
        run ??= [];
        return ret;
    }

    public int SetRunCaseFlag(string caseName, bool run, bool all) =>
        Analyze.SetRunCaseFlag(caseName, run, all);

    public int GetCaseStatus(out string[] caseNames, out int[] statuses)
    {
        var count = 0;
        caseNames = [];
        statuses = [];
        var ret = Analyze.GetCaseStatus(ref count, ref caseNames, ref statuses);
        caseNames ??= [];
        statuses ??= [];
        return ret;
    }

    public int CreateAnalysisModel() => Analyze.CreateAnalysisModel();

    public int RunAnalysis() => Analyze.RunAnalysis();
}
