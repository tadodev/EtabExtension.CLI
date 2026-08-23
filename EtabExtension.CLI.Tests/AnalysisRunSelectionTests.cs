// Copyright (c) Thanh Tu. All rights reserved.
// Licensed under the MIT License.

using EtabExtension.CLI.Shared.Infrastructure.Etabs.Analysis;
using Xunit;

namespace EtabExtension.CLI.Tests;

/// <summary>
/// CLI #31 — <c>run-analysis</c> truthfulness.
///
/// <para>Two independent lies were reported by <c>run-analysis</c>, and both are
/// reproduced below against a model that REMEMBERS, because the persistent daemon keeps
/// one ETABS alive across requests and that is the whole reason they were reachable.</para>
///
/// <list type="number">
/// <item><b>Stale case selection.</b> A default run set no run flag at all. After a
/// <c>--cases</c>-scoped request narrowed the model's selection, the next default request
/// printed "Running all cases", ran only the still-selected one, and passed.</item>
/// <item><b>Success from unrelated cases.</b> The verdict counted cases at status 4
/// across the WHOLE model, so results left behind by earlier runs carried a request whose
/// own case had failed.</item>
/// </list>
///
/// <para>Status codes and return-code meanings here are Cardex's (<c>etabs-api-23.3</c>):
/// <c>cAnalyze.GetCaseStatus</c> reports 1 = not run, 2 = could not start, 3 = not
/// finished, 4 = finished; <c>cAnalyze.SetRunCaseFlag</c>, <c>GetRunCaseFlag</c> and
/// <c>RunAnalysis</c> each return zero on success and nonzero on FAILURE — none of them
/// documents a nonzero value that means anything about the model.</para>
/// </summary>
public sealed class AnalysisRunSelectionTests
{
    private const string ModelPath = @"D:\Models\tower.edb";

    // ── Defect 1: a default run must restore, never inherit ──────────────────

    /// <summary>
    /// The reported regression, end to end on one long-lived model: scope a run to DEAD,
    /// then ask for a default run. The default run must actually run all three cases.
    /// </summary>
    [Fact]
    public void DefaultRunAfterASelectiveRunRunsEveryCase()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE", "WIND");

        var selective = AnalysisRunner.Run(api, ModelPath, ["DEAD"], null);
        Assert.True(selective.Success, selective.Error);
        Assert.Equal(["DEAD"], api.LastRunSet);

        var byDefault = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.True(byDefault.Success, byDefault.Error);
        Assert.Equal(["DEAD", "LIVE", "WIND"], api.LastRunSet);
        Assert.Equal(["DEAD", "LIVE", "WIND"], byDefault.Data!.CasesRun);
        Assert.Equal(3, byDefault.Data.CasesRunFinishedCount);
        Assert.Empty(byDefault.Data.CasesNotFinished);
    }

    /// <summary>The restore is an explicit CSI call, not an assumption about the model.</summary>
    [Fact]
    public void DefaultRunSetsTheAllCasesFlagBeforeAnalysing()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE");

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.True(result.Success, result.Error);
        Assert.Contains("SetRunCaseFlag(all, run=True)", api.Calls);
        Assert.True(
            api.Calls.IndexOf("SetRunCaseFlag(all, run=True)") < api.Calls.IndexOf("RunAnalysis"),
            "the selection must be restored BEFORE the analysis runs");
    }

    /// <summary>
    /// If the restore cannot be made, the run does not happen. Running anyway would be the
    /// original defect with an extra step.
    /// </summary>
    [Fact]
    public void DefaultRunFailsWhenTheAllCasesSelectionCannotBeRestored()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE") { AllTrueSelectReturnCode = 3 };

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("ret=3", result.Error!, StringComparison.Ordinal);
        Assert.Contains("SetRunCaseFlag", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    /// <summary>
    /// A selective request establishes its selection outright — it selects what it wants
    /// AND deselects what it does not — instead of assuming the model starts clean.
    /// </summary>
    [Fact]
    public void SelectiveRunDeselectsWhateverThePreviousRequestLeftSelected()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE", "WIND");
        api.SetRunCaseFlag(string.Empty, run: false, all: true);
        api.SetRunCaseFlag("WIND", run: true, all: false);
        api.Calls.Clear();

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD"], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["DEAD"], api.LastRunSet);
        Assert.Contains("SetRunCaseFlag(all, run=False)", api.Calls);
        Assert.Equal(["DEAD"], result.Data!.CasesRun);
    }

    /// <summary>
    /// A selection that silently does not take is caught by reading the flags back. Without
    /// the readback the command would still announce a run set the model does not hold.
    /// </summary>
    [Fact]
    public void ASelectionThatDoesNotTakeIsCaughtByReadingTheFlagsBack()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE") { AcceptsSelectionsWithoutApplyingThem = true };

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD"], null);

        Assert.False(result.Success);
        Assert.Contains("did not take", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    // ── Every CSI selection call is validated ────────────────────────────────

    /// <summary>
    /// A nonzero <c>SetRunCaseFlag</c> is a FAILED CALL. It must surface as one — not be
    /// converted into the semantic claim "case not found", which is the exact defect class
    /// CLI #28 and #29 already repaired here twice.
    /// </summary>
    [Fact]
    public void ASelectionCallThatFailsIsReportedAsAFailedCallNotAsAMissingCase()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE");
        api.SelectionFailures.Add("DEAD");

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD"], null);

        Assert.False(result.Success);
        Assert.Contains("SetRunCaseFlag('DEAD', Run=True) failed (ret=7)", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, api.RunCount);
    }

    /// <summary>
    /// A case that could not be selected is never quietly dropped from a run that then
    /// reports success on the rest.
    /// </summary>
    [Fact]
    public void ACaseThatCannotBeSelectedIsNotSilentlyDroppedFromTheRun()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE", "WIND");
        api.SelectionFailures.Add("WIND");

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD", "WIND"], null);

        Assert.False(result.Success);
        Assert.Contains("WIND", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    /// <summary>
    /// The load-case census is what decides whether a case exists. If the census call
    /// fails, nothing is claimed about the model at all.
    /// </summary>
    [Fact]
    public void ACensusCallThatFailsFailsTheRunRatherThanAssumingTheCaseList()
    {
        var api = new FakeAnalysisApi("DEAD") { CensusReturnCodeForCall = _ => 5 };

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("GetRunCaseFlag failed (ret=5)", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    /// <summary>
    /// Absence from the model's own census IS positive evidence, so it may be reported as
    /// such — and it is reported, in the payload, rather than vanishing.
    /// </summary>
    [Fact]
    public void ACaseTheModelDoesNotDefineIsReportedAndTheRestStillRun()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE");

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD", "GHOST"], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["DEAD"], api.LastRunSet);
        Assert.Equal(["DEAD"], result.Data!.CasesRun);
        Assert.Equal(["GHOST"], result.Data.CasesNotInModel);
        Assert.DoesNotContain("SetRunCaseFlag('GHOST', run=True)", api.Calls);
    }

    [Fact]
    public void ARequestNamingOnlyUndefinedCasesFailsWithoutRunning()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE");

        var result = AnalysisRunner.Run(api, ModelPath, ["GHOST", "PHANTOM"], null);

        Assert.False(result.Success);
        Assert.Contains("GHOST, PHANTOM", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    /// <summary>Requests are matched to the model's own spelling of the case name.</summary>
    [Fact]
    public void ARequestedNameIsResolvedToTheModelsSpelling()
    {
        var api = new FakeAnalysisApi("Modal (Rizt)", "DEAD");

        var result = AnalysisRunner.Run(api, ModelPath, ["modal (rizt)"], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Modal (Rizt)"], api.LastRunSet);
        Assert.Equal(["Modal (Rizt)"], result.Data!.CasesRun);
    }

    [Fact]
    public void AModelWithNoLoadCasesFailsInsteadOfRunningNothingSuccessfully()
    {
        var api = new FakeAnalysisApi();

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("no load cases", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    // ── Defect 2: the verdict is the requested run set, nothing else ─────────

    /// <summary>
    /// The reported case, exactly: twelve unrelated cases sit at status 4 from an earlier
    /// run, the one case this request asked for does not finish. The command FAILS.
    /// </summary>
    [Fact]
    public void ARequestedCaseThatDoesNotFinishFailsDespiteTwelveStaleFinishedCases()
    {
        var stale = Enumerable.Range(1, 12).Select(i => $"OLD{i}").ToArray();
        var api = new FakeAnalysisApi([.. stale, "EQX"]);
        api.MarkFinished(stale);
        api.CasesThatDoNotFinish.Add("EQX");

        var result = AnalysisRunner.Run(api, ModelPath, ["EQX"], null);

        Assert.False(result.Success);
        Assert.Contains("EQX (not finished)", result.Error!, StringComparison.Ordinal);
        Assert.Equal(12, result.Data!.FinishedCaseCount);
        Assert.Equal(0, result.Data.CasesRunFinishedCount);
        Assert.Equal(["EQX (not finished)"], result.Data.CasesNotFinished);
    }

    /// <summary>A case the model never even reports on cannot pass either.</summary>
    [Fact]
    public void ARequestedCaseWithNoReportedStatusFailsTheRun()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE");
        api.MarkFinished("LIVE");
        api.OmitFromStatusReport.Add("DEAD");

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD"], null);

        Assert.False(result.Success);
        Assert.Contains("DEAD (no status reported)", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>One failed case fails a default run, however many of its siblings passed.</summary>
    [Fact]
    public void OneUnfinishedCaseFailsADefaultRunOfManyCases()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE", "WIND");
        api.CasesThatDoNotFinish.Add("WIND");

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("WIND (not finished)", result.Error!, StringComparison.Ordinal);
        Assert.Equal(2, result.Data!.CasesRunFinishedCount);
        Assert.Equal(2, result.Data.FinishedCaseCount);
    }

    /// <summary>A run whose whole set finishes is the only thing that passes.</summary>
    [Fact]
    public void AllRequestedCasesFinishingIsWhatSuccessMeans()
    {
        var api = new FakeAnalysisApi("DEAD", "LIVE", "WIND");
        api.MarkFinished("WIND");

        var result = AnalysisRunner.Run(api, ModelPath, ["DEAD", "LIVE"], null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Data!.CasesRunFinishedCount);
        Assert.Equal(3, result.Data.CaseCount);
        Assert.Equal(3, result.Data.FinishedCaseCount);
        Assert.Equal(["DEAD", "LIVE"], result.Data.CasesRun);
    }

    /// <summary>
    /// If the outcome cannot be read, the run is not successful — even though every
    /// requested case did in fact finish. An unreadable outcome is not a pass.
    /// </summary>
    [Fact]
    public void AnUnreadableOutcomeIsNotAPass()
    {
        var api = new FakeAnalysisApi("DEAD") { CaseStatusReturnCode = 9 };

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("GetCaseStatus failed (ret=9)", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRunAnalysisCallFailsTheCommand()
    {
        var api = new FakeAnalysisApi("DEAD") { RunAnalysisReturnCode = 2 };

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("Analysis failed (ret=2)", result.Error!, StringComparison.Ordinal);
        Assert.Equal(["DEAD"], result.Data!.CasesRun);
    }

    [Fact]
    public void AFailedCreateAnalysisModelCallFailsTheCommand()
    {
        var api = new FakeAnalysisApi("DEAD") { CreateAnalysisModelReturnCode = 4 };

        var result = AnalysisRunner.Run(api, ModelPath, null, null);

        Assert.False(result.Success);
        Assert.Contains("CreateAnalysisModel failed (ret=4)", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, api.RunCount);
    }

    // ── Wiring: one owner for the selection surface ──────────────────────────

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ProductionCalls =
        VisibilityCallGraph.ByProductionMethod(typeof(IEtabsAnalysisApi).Assembly);

    /// <summary>
    /// The revert guard. <c>run-analysis</c> and <c>analyze-and-extract</c> each carried
    /// their own copy of the selection sequence, which is how one bug became two. Exactly
    /// one production method may reach <c>cAnalyze.SetRunCaseFlag</c>; anything else
    /// re-opens that seam.
    /// </summary>
    [Fact]
    public void OnlyTheAnalysisApiAdapterTouchesTheRunCaseFlag()
    {
        var callers = ProductionCalls
            .Where(entry => entry.Value.Any(IsExternalRunCaseFlagCall))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [$"{typeof(IEtabsAnalysisApi).Namespace}.EtabsAnalysisApi.SetRunCaseFlag"],
            callers);
    }

    /// <summary>
    /// A <c>SetRunCaseFlag</c> on something OUTSIDE this codebase — the CSI
    /// <c>cAnalyze</c> itself, or the EtabSharp wrapper over it. Calls onto the CLI's own
    /// <see cref="IEtabsAnalysisApi"/> are the abstraction working and are not the subject
    /// of the rule.
    /// </summary>
    private static bool IsExternalRunCaseFlagCall(string call) =>
        call.EndsWith(".SetRunCaseFlag", StringComparison.Ordinal)
        && !call.StartsWith("EtabExtension.CLI.", StringComparison.Ordinal);

    /// <summary>
    /// That adapter must reach the RAW CSI surface. The EtabSharp <c>AnalyzeManager</c>
    /// wrapper throws on a nonzero return, and a thrown wrapper exception is exactly what
    /// the old code caught and re-published as "case not found".
    /// </summary>
    [Fact]
    public void TheAdapterCallsTheRawCsiSelectionSurface()
    {
        var calls = ProductionCalls[$"{typeof(IEtabsAnalysisApi).Namespace}.EtabsAnalysisApi.SetRunCaseFlag"];

        Assert.Contains("ETABSv1.cAnalyze.SetRunCaseFlag", calls, StringComparer.Ordinal);
    }

    /// <summary>
    /// Both analysis entry points share one runner, so a repair to one cannot leave the
    /// other lying.
    /// </summary>
    [Fact]
    public void EverySelectionDecisionIsMadeInOnePlace()
    {
        var callers = ProductionCalls
            .Where(entry => entry.Value.Any(call =>
                call.EndsWith(".AnalysisRunSelection.Establish", StringComparison.Ordinal)))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([$"{typeof(IEtabsAnalysisApi).Namespace}.AnalysisRunner.Run"], callers);
    }

    /// <summary>
    /// A model that remembers what the last request did to it — which is what makes the
    /// stale-selection defect reachable at all on the persistent daemon.
    /// </summary>
    private sealed class FakeAnalysisApi : IEtabsAnalysisApi
    {
        private const int SelectionFailureCode = 7;

        private readonly List<string> _modelCases;
        private readonly Dictionary<string, bool> _run = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _status = new(StringComparer.OrdinalIgnoreCase);

        public FakeAnalysisApi(params string[] modelCases)
        {
            _modelCases = [.. modelCases];
            foreach (var name in _modelCases)
            {
                // A freshly opened model: every case selected, none run yet (status 1).
                _run[name] = true;
                _status[name] = 1;
            }
        }

        /// <summary>Every call made on this API, in order, for ordering assertions.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The cases the most recent RunAnalysis actually analysed, sorted.</summary>
        public IReadOnlyList<string> LastRunSet { get; private set; } = [];

        public int RunCount { get; private set; }

        /// <summary>Cases whose <c>SetRunCaseFlag</c> refuses with a nonzero return.</summary>
        public HashSet<string> SelectionFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cases that end a run at status 3 (not finished).</summary>
        public HashSet<string> CasesThatDoNotFinish { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cases the status report leaves out entirely.</summary>
        public HashSet<string> OmitFromStatusReport { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Return code for <c>SetRunCaseFlag(All=True, Run=True)</c>.</summary>
        public int AllTrueSelectReturnCode { get; init; }

        /// <summary>Return code for <c>SetRunCaseFlag(All=True, Run=False)</c>.</summary>
        public int AllFalseSelectReturnCode { get; init; }

        /// <summary>Accepts every selection with ret=0 and applies none of it.</summary>
        public bool AcceptsSelectionsWithoutApplyingThem { get; init; }

        public int CaseStatusReturnCode { get; init; }

        public int CreateAnalysisModelReturnCode { get; init; }

        public int RunAnalysisReturnCode { get; init; }

        /// <summary>Return code for the nth (1-based) <c>GetRunCaseFlag</c> call.</summary>
        public Func<int, int>? CensusReturnCodeForCall { get; init; }

        private int _censusCalls;

        public void MarkFinished(params string[] caseNames)
        {
            foreach (var name in caseNames)
            {
                _status[name] = 4;
            }
        }

        public int GetRunCaseFlags(out string[] caseNames, out bool[] run)
        {
            _censusCalls++;
            Calls.Add("GetRunCaseFlag");
            var code = CensusReturnCodeForCall?.Invoke(_censusCalls) ?? 0;
            if (code != 0)
            {
                caseNames = [];
                run = [];
                return code;
            }

            caseNames = [.. _modelCases];
            run = [.. _modelCases.Select(name => _run[name])];
            return 0;
        }

        public int SetRunCaseFlag(string caseName, bool run, bool all)
        {
            Calls.Add(all
                ? $"SetRunCaseFlag(all, run={run})"
                : $"SetRunCaseFlag('{caseName}', run={run})");

            if (all)
            {
                var code = run ? AllTrueSelectReturnCode : AllFalseSelectReturnCode;
                if (code != 0)
                {
                    return code;
                }

                if (!AcceptsSelectionsWithoutApplyingThem)
                {
                    foreach (var name in _modelCases)
                    {
                        _run[name] = run;
                    }
                }

                return 0;
            }

            if (SelectionFailures.Contains(caseName))
            {
                return SelectionFailureCode;
            }

            if (!_run.ContainsKey(caseName))
            {
                // What a real cAnalyze does with a name it does not know: it refuses. The
                // refusal is a failed call, and nothing here turns it into a diagnosis.
                return SelectionFailureCode;
            }

            if (!AcceptsSelectionsWithoutApplyingThem)
            {
                _run[caseName] = run;
            }

            return 0;
        }

        public int GetCaseStatus(out string[] caseNames, out int[] statuses)
        {
            Calls.Add("GetCaseStatus");
            if (CaseStatusReturnCode != 0)
            {
                caseNames = [];
                statuses = [];
                return CaseStatusReturnCode;
            }

            var reported = _modelCases.Where(name => !OmitFromStatusReport.Contains(name)).ToList();
            caseNames = [.. reported];
            statuses = [.. reported.Select(name => _status[name])];
            return 0;
        }

        public int CreateAnalysisModel()
        {
            Calls.Add("CreateAnalysisModel");
            return CreateAnalysisModelReturnCode;
        }

        public int RunAnalysis()
        {
            Calls.Add("RunAnalysis");
            RunCount++;
            if (RunAnalysisReturnCode != 0)
            {
                LastRunSet = [];
                return RunAnalysisReturnCode;
            }

            var analysed = _modelCases.Where(name => _run[name]).ToList();
            foreach (var name in analysed)
            {
                // Cases left deselected keep whatever result they already had — which is
                // precisely how stale status-4 results survive into the next request.
                _status[name] = CasesThatDoNotFinish.Contains(name) ? 3 : 4;
            }

            LastRunSet = [.. analysed.Order(StringComparer.Ordinal)];
            return 0;
        }
    }
}
