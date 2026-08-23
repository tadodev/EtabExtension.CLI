// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Common;

namespace EtabExtension.CLI.Shared.Infrastructure.Etabs.Analysis;

/// <summary>
/// The cases one analysis request is ACTUALLY configured to produce results for, as
/// read back from the model after the selection was established.
///
/// <para>Nothing downstream may assume the run set; it is the readback, not the
/// intention. A default request carries every load case the model defines, because a
/// default request explicitly restores the all-cases selection rather than inheriting
/// whatever the previous request on a long-lived daemon left behind.</para>
/// </summary>
public sealed record AnalysisRunSet
{
    /// <summary>The load cases the model reports as selected to run, in model spelling.</summary>
    public required IReadOnlyList<string> Cases { get; init; }

    /// <summary>True when the request named no cases and the full model set was restored.</summary>
    public required bool IsAllCases { get; init; }

    /// <summary>How many load cases the model defines in total.</summary>
    public required int ModelCaseCount { get; init; }
}

/// <summary>What the model says about the run set once the analysis has returned.</summary>
public sealed record AnalysisRunOutcome
{
    /// <summary>Load cases the model reports a status for.</summary>
    public required int ModelCaseCount { get; init; }

    /// <summary>Cases at status 4 anywhere in the model, including stale earlier results.</summary>
    public required int ModelFinishedCaseCount { get; init; }

    /// <summary>Members of the run set that reached status 4 on this run.</summary>
    public required int RunSetFinishedCount { get; init; }

    /// <summary>Members of the run set that did not, each with the status the model reported.</summary>
    public required IReadOnlyList<string> NotFinished { get; init; }
}

/// <summary>
/// Establishes the run selection for one analysis request and judges the result against
/// it.
///
/// <para><b>CLI #31.</b> Two separate lies lived here. A DEFAULT request set no run flag
/// at all, so on the persistent daemon it silently inherited the narrowed selection a
/// previous <c>--cases</c> request had left in the model: the CLI printed "Running all
/// cases", ran one, and reported success. And success itself was decided by counting
/// cases at status 4 across the WHOLE model, so the requested case could fail outright
/// while a dozen stale results from an earlier run carried the command to a pass.</para>
///
/// <para>Both are repaired the same way: state the selection explicitly, verify it from
/// the model, and judge only the cases that selection actually covers.</para>
/// </summary>
public static class AnalysisRunSelection
{
    /// <summary>The <c>cAnalyze.GetCaseStatus</c> code for a finished case (Cardex: 1-4).</summary>
    private const int FinishedStatus = 4;

    private static readonly StringComparer CaseNameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Puts the model into the selection this request asked for, and returns what the
    /// model then reports as selected.
    ///
    /// <para>A DEFAULT request (<paramref name="requestedCases"/> null or empty) RESTORES
    /// the all-cases selection. A SELECTIVE request deselects every case and then selects
    /// exactly the ones it named. Neither path assumes the model started clean.</para>
    ///
    /// <para>Every CSI call is checked. A nonzero return is a failed call and fails the
    /// request; it is never converted into a claim about the model.</para>
    /// </summary>
    public static Result<AnalysisRunSet> Establish(
        IEtabsAnalysisApi api,
        IReadOnlyList<string>? requestedCases)
    {
        ArgumentNullException.ThrowIfNull(api);

        var censusRet = api.GetRunCaseFlags(out var modelCases, out _);
        if (censusRet != 0)
        {
            return Result.Fail<AnalysisRunSet>(
                $"cAnalyze.GetRunCaseFlag failed (ret={censusRet}). The model's load-case list " +
                "could not be read, so no run selection can be established or verified.");
        }

        if (modelCases.Length == 0)
        {
            return Result.Fail<AnalysisRunSet>(
                "The model defines no load cases, so there is nothing to run.");
        }

        var wantsAll = requestedCases is null or { Count: 0 };
        IReadOnlyList<string> intended;

        if (wantsAll)
        {
            // RESTORE, do not inherit. Without this a default request runs whatever
            // narrowed selection the previous request left in the model.
            var restoreRet = api.SetRunCaseFlag(string.Empty, run: true, all: true);
            if (restoreRet != 0)
            {
                return Result.Fail<AnalysisRunSet>(
                    $"cAnalyze.SetRunCaseFlag(All=True, Run=True) failed (ret={restoreRet}). " +
                    "The all-cases selection could not be restored, so this run cannot " +
                    "honestly claim to run all cases.");
            }

            intended = modelCases;
        }
        else
        {
            var resolution = Resolve(requestedCases!, modelCases);

            // ANY absent case refuses the WHOLE request, and refuses it HERE — before a
            // single run flag has moved and before any analysis. Running the rest and
            // returning success would hand the caller a partial result labelled as a
            // complete one, and no shipping consumer reads a per-case breakdown to notice.
            // Note this rests on a census that SUCCEEDED: the failed-census branch above
            // has already returned, so absence here is the model's own answer and never a
            // failed call re-read as "case not found".
            if (resolution.NotInModel.Count > 0)
            {
                return Result.Fail<AnalysisRunSet>(
                    "Refusing to run a partial analysis: " +
                    $"{resolution.NotInModel.Count} of the {resolution.Resolved.Count + resolution.NotInModel.Count} " +
                    "requested case(s) are not defined in this model: " +
                    $"{string.Join(", ", resolution.NotInModel)}. " +
                    "No run flag was changed and no analysis was run. " +
                    $"The model defines {modelCases.Length} load case(s); re-request with only those.");
            }

            // ESTABLISH the requested selection outright: clear every flag first so the
            // run set is exactly what was asked for and not what happened to be left set.
            var clearRet = api.SetRunCaseFlag(string.Empty, run: false, all: true);
            if (clearRet != 0)
            {
                return Result.Fail<AnalysisRunSet>(
                    $"cAnalyze.SetRunCaseFlag(All=True, Run=False) failed (ret={clearRet}). " +
                    "The previous run selection could not be cleared, so the requested " +
                    "selection cannot be established.");
            }

            foreach (var caseName in resolution.Resolved)
            {
                var selectRet = api.SetRunCaseFlag(caseName, run: true, all: false);
                if (selectRet != 0)
                {
                    // A failed call is a failed call. The model's own census lists this
                    // name, so "not found" would be a fabrication — which is the exact
                    // conversion CLI #28 and #29 were filed for.
                    return Result.Fail<AnalysisRunSet>(
                        $"cAnalyze.SetRunCaseFlag('{caseName}', Run=True) failed (ret={selectRet}). " +
                        "The call failed; this says nothing about whether the case exists — " +
                        "the model's load-case list contains it.");
                }
            }

            intended = resolution.Resolved;
        }

        var verified = Verify(api, intended, wantsAll);
        if (!verified.Success)
        {
            return Result.Fail<AnalysisRunSet>(verified.Error!);
        }

        return Result.Ok(new AnalysisRunSet
        {
            Cases = intended,
            IsAllCases = wantsAll,
            ModelCaseCount = modelCases.Length
        });
    }

    /// <summary>
    /// Judges the finished analysis against <paramref name="runSet"/> — and only against
    /// it. Results belonging to cases this request did not ask for cannot carry it.
    /// </summary>
    public static Result<AnalysisRunOutcome> Evaluate(IEtabsAnalysisApi api, AnalysisRunSet runSet)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(runSet);

        var ret = api.GetCaseStatus(out var caseNames, out var statuses);
        if (ret != 0)
        {
            return Result.Fail<AnalysisRunOutcome>(
                $"cAnalyze.GetCaseStatus failed (ret={ret}). The analysis ran but its outcome " +
                "could not be read, so this run cannot be reported as successful.");
        }

        if (caseNames.Length != statuses.Length)
        {
            return Result.Fail<AnalysisRunOutcome>(
                $"cAnalyze.GetCaseStatus reported {caseNames.Length} case name(s) against " +
                $"{statuses.Length} status value(s); the status report is unusable.");
        }

        var byCase = new Dictionary<string, int>(CaseNameComparer);
        for (var i = 0; i < caseNames.Length; i++)
        {
            byCase[caseNames[i]] = statuses[i];
        }

        var finished = 0;
        var notFinished = new List<string>();
        foreach (var caseName in runSet.Cases)
        {
            if (byCase.TryGetValue(caseName, out var status) && status == FinishedStatus)
            {
                finished++;
                continue;
            }

            notFinished.Add(
                byCase.ContainsKey(caseName)
                    ? $"{caseName} ({DescribeStatus(status)})"
                    : $"{caseName} (no status reported)");
        }

        var outcome = new AnalysisRunOutcome
        {
            ModelCaseCount = caseNames.Length,
            ModelFinishedCaseCount = statuses.Count(s => s == FinishedStatus),
            RunSetFinishedCount = finished,
            NotFinished = notFinished
        };

        if (notFinished.Count > 0)
        {
            return Result.Fail<AnalysisRunOutcome>(
                $"Analysis did not finish {notFinished.Count} of the {runSet.Cases.Count} " +
                $"requested case(s): {string.Join(", ", notFinished)}. " +
                $"({outcome.ModelFinishedCaseCount} of {outcome.ModelCaseCount} case(s) in the " +
                "model are finished, but results the request did not ask for do not make it " +
                "successful.)")
                with
            { Data = outcome };
        }

        return Result.Ok(outcome);
    }

    /// <summary>Cardex <c>cAnalyze.GetCaseStatus</c>: Status is an integer from 1 to 4.</summary>
    private static string DescribeStatus(int status) => status switch
    {
        1 => "not run",
        2 => "could not start",
        3 => "not finished",
        4 => "finished",
        _ => $"unrecognised status {status}"
    };

    private readonly record struct Resolution(
        IReadOnlyList<string> Resolved,
        IReadOnlyList<string> NotInModel);

    /// <summary>
    /// Matches the requested names against the model's own census, keeping the model's
    /// spelling. Absence here is positive evidence from the census, not the residue of a
    /// failed call.
    /// </summary>
    private static Resolution Resolve(IReadOnlyList<string> requested, string[] modelCases)
    {
        var census = new Dictionary<string, string>(CaseNameComparer);
        foreach (var name in modelCases)
        {
            census.TryAdd(name, name);
        }

        var resolved = new List<string>();
        var notInModel = new List<string>();
        var seen = new HashSet<string>(CaseNameComparer);

        foreach (var name in requested)
        {
            if (!seen.Add(name))
            {
                continue;
            }

            if (census.TryGetValue(name, out var modelSpelling))
            {
                resolved.Add(modelSpelling);
            }
            else
            {
                notInModel.Add(name);
            }
        }

        return new Resolution(resolved, notInModel);
    }

    /// <summary>
    /// Reads the run flags back and requires them to be exactly the intended set. This is
    /// what turns "running all cases" from a printed sentence into a checked fact.
    /// </summary>
    private static Result Verify(IEtabsAnalysisApi api, IReadOnlyList<string> intended, bool wantsAll)
    {
        var ret = api.GetRunCaseFlags(out var caseNames, out var run);
        if (ret != 0)
        {
            return Result.Fail(
                $"cAnalyze.GetRunCaseFlag failed (ret={ret}) while verifying the run selection. " +
                "The selection could not be confirmed, so this run cannot state what it will run.");
        }

        if (caseNames.Length != run.Length)
        {
            return Result.Fail(
                $"cAnalyze.GetRunCaseFlag reported {caseNames.Length} case name(s) against " +
                $"{run.Length} flag(s); the run selection is unreadable.");
        }

        var selected = new HashSet<string>(CaseNameComparer);
        for (var i = 0; i < caseNames.Length; i++)
        {
            if (run[i])
            {
                selected.Add(caseNames[i]);
            }
        }

        var expected = new HashSet<string>(intended, CaseNameComparer);
        var missing = expected.Where(name => !selected.Contains(name)).Order(CaseNameComparer).ToList();
        var extra = selected.Where(name => !expected.Contains(name)).Order(CaseNameComparer).ToList();

        if (missing.Count == 0 && extra.Count == 0)
        {
            return Result.Ok();
        }

        var scope = wantsAll ? "all cases" : $"{expected.Count} requested case(s)";
        var detail = new List<string>();
        if (missing.Count > 0)
        {
            detail.Add($"not selected: {string.Join(", ", missing)}");
        }

        if (extra.Count > 0)
        {
            detail.Add($"unexpectedly selected: {string.Join(", ", extra)}");
        }

        return Result.Fail(
            $"The run selection did not take. Asked the model for {scope}; " +
            $"cAnalyze.GetRunCaseFlag reports {string.Join("; ", detail)}. " +
            "Refusing to run and report on a selection the model does not hold.");
    }
}
