using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightPCG.Systems;

namespace LightPCG.Research
{
    /// <summary>
    /// Runs N automated solve attempts and exports results to CSV.
    /// Set totalRuns in the Inspector before pressing Play.
    /// CSV is saved to: [Project]/Assets/PCG_Results.csv
    ///
    /// MID formula (Section 3.3 of paper):
    ///   MID = α·Cl + β·Ch + γ·Ci
    ///
    ///   Cl  = Component Density    = TotalObjects / (GridWidth × GridHeight)
    ///                                 (TotalObjects = SolutionObjects + Decoys)
    ///                                 ตาม Section 3.4.3 ของรายงาน: Cl = N_totalObjects / 256
    ///   Ch  = Rule Heterogeneity   = RuleTransitions / (SolutionObjects − 1)
    ///   Ci  = Interaction Density  = RefractorCount  /  SolutionObjects
    ///
    ///   α = 0.5  (path length — most influential)
    ///   β = 0.3  (rule variety)
    ///   γ = 0.2  (refractor complexity)
    ///
    /// SolvePhase column (RQ2 difficulty signal):
    ///   Trivial / 1A / 1B / Sweep-S1 / Sweep-S2 / Sweep-S3 / Sweep(failed) / None,
    ///   ordered roughly from least to most solver effort. SweepIterations and
    ///   SweepRelocations give the effort actually spent once the solver fell
    ///   back to CorrectionSweep, so harder (higher-MID) puzzles can be checked
    ///   against deeper phases / higher effort, not just a pass/fail flag.
    /// </summary>
    public class BatchRunner : MonoBehaviour
    {
        // ── MID weighting constants (paper Section 3.3) ───────────────
        private const float ALPHA = 0.5f;   // weight for Cl (Component Density)
        private const float BETA = 0.3f;   // weight for Ch (Rule Heterogeneity)
        private const float GAMMA = 0.2f;   // weight for Ci (Interaction Density)

        [Header("Experiment")]
        public int totalRuns = 50;        // change to 1000 for full experiment
        public bool runOnStart = true;

        [Header("Progressive Difficulty (legacy adaptive mode)")]
        public int startSteps = 2;
        public int maxSteps = 9;
        public int startDecoys = 0;
        public int decoyEveryN = 3;

        [Header("Stratified Sampling")]
        [Tooltip("If true, (Steps × Decoys) combinations are cycled through evenly across " +
                 "totalRuns instead of ramping difficulty up adaptively. This is required for " +
                 "the Low/Medium/High MID comparison in Section 3.4.2 — the adaptive mode " +
                 "reaches max difficulty within ~10 runs and stays there, so 1000 adaptive " +
                 "runs end up giving almost no coverage of the lower/medium tiers.")]
        public bool useStratifiedSampling = true;
        [Tooltip("Decoy counts to cycle through when stratified sampling is enabled.")]
        public int[] decoyLevels = { 0, 1, 2, 3, 4 };

        [Header("References")]
        public GridVisualizer gridVisualizer;
        public AISolverAgent solverAgent;

        [Header("CSV Export")]
        public string csvFileName = "PCG_Results.csv";

        [Header("Timing")]
        [Tooltip("Seconds to wait after OnSolveComplete before generating the next level.")]
        public float exitDoorWait = 5.0f;

        // ── Private state ─────────────────────────────────────────────
        private List<RunRecord> records = new List<RunRecord>();
        private int currentRun = 0;
        private int currentSteps;
        private int currentDecoys;
        private int solvedInSession = 0;

        // ── Run record — all columns written to CSV ───────────────────
        struct RunRecord
        {
            // Identification
            public int run, level;

            // Puzzle structure (from PCG generator)
            public int steps, solObjs, mirrorCount, refractorCount;
            public int ruleTransitions, decoys, totalObjs, gridWidth, gridHeight;

            // MID components (paper Section 3.3)
            public float Cl;    // Component Density       = TotalObjects / 256
            public float Ch;    // Rule Heterogeneity      = RuleTransitions / (SolutionObjects - 1)
            public float Ci;    // Component Complexity    = Refractors / SolutionObjects
            public float MID;   // Composite score = α·Cl + β·Ch + γ·Ci

            // Solver outcome
            public bool solved;
            public string solvePhase;

            // Search performance (logical only, no physics wait)
            public int searchNodes;
            public float searchTimeMs;

            // Execution performance (physical movement)
            public int totalPlacements, inPlaceRotations, relocations;
            public float execTimeMs;

            // Sweep-stage effort (only non-zero when solvePhase starts with "Sweep")
            // Added so RQ2 analysis can show how much extra work the solver
            // needed once it fell back to CorrectionSweep, instead of just
            // knowing "it used Sweep" with no sense of how hard that was.
            public int sweepIterations, sweepRelocations;

            // Overall timing
            public float solveTimeMs, genMs;
        }

        // ════════════════════════════════════════════════════════════
        void Start()
        {
            if (gridVisualizer == null) gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (solverAgent == null) solverAgent = FindFirstObjectByType<AISolverAgent>();
            currentSteps = startSteps;
            currentDecoys = startDecoys;
            if (runOnStart) StartCoroutine(RunBatch());
        }

        public void StartBatch() => StartCoroutine(RunBatch());

        // ════════════════════════════════════════════════════════════
        // Returns the (steps, decoys) pair to use for the given zero-based
        // run index. In stratified mode this is a deterministic round-robin
        // over every (Steps × Decoys) combination so each tier gets roughly
        // totalRuns / (stepsRange × decoyLevels.Length) samples — instead of
        // the old adaptive mode, which raced to max difficulty in ~10 runs
        // and spent the other 990+ runs stuck at a single tier.
        // ════════════════════════════════════════════════════════════
        (int steps, int decoys) GetDifficultyForRun(int zeroBasedIndex)
        {
            int stepsRange = Mathf.Max(1, maxSteps - startSteps + 1);
            int decoyCount = (decoyLevels != null && decoyLevels.Length > 0) ? decoyLevels.Length : 1;

            int stepsIdx = zeroBasedIndex % stepsRange;          // cycles fastest
            int decoyIdx = (zeroBasedIndex / stepsRange) % decoyCount; // cycles slower

            int steps = startSteps + stepsIdx;
            int decoys = (decoyLevels != null && decoyLevels.Length > 0) ? decoyLevels[decoyIdx] : startDecoys;
            return (steps, decoys);
        }

        // ════════════════════════════════════════════════════════════
        IEnumerator RunBatch()
        {
            int stepsRangeForLog = Mathf.Max(1, maxSteps - startSteps + 1);
            int decoyCountForLog = (decoyLevels != null && decoyLevels.Length > 0) ? decoyLevels.Length : 1;
            string modeMsg = useStratifiedSampling
                ? $"stratified | {stepsRangeForLog} steps × {decoyCountForLog} decoy levels " +
                  $"= {stepsRangeForLog * decoyCountForLog} tiers, ~{totalRuns / (stepsRangeForLog * decoyCountForLog)} runs/tier"
                : $"adaptive | startSteps={startSteps}";
            Debug.Log($"[Batch] Starting {totalRuns} runs | mode={modeMsg}");
            records.Clear();
            currentRun = 0;
            solvedInSession = 0;

            for (int i = 0; i < totalRuns; i++)
            {
                currentRun = i + 1;

                // Apply difficulty
                if (useStratifiedSampling)
                {
                    (currentSteps, currentDecoys) = GetDifficultyForRun(i);
                }
                gridVisualizer.minSteps = currentSteps;
                gridVisualizer.maxSteps = currentSteps;
                gridVisualizer.decoyCount = currentDecoys;

                Debug.Log($"[Batch] Run {currentRun}/{totalRuns} " +
                          $"steps={currentSteps} decoys={currentDecoys}");

                // Generate level and measure generation time
                float gStart = Time.realtimeSinceStartup;
                gridVisualizer.GenerateLevel();
                float gMs = (Time.realtimeSinceStartup - gStart) * 1000f;

                yield return new WaitForEndOfFrame();
                yield return new WaitForSeconds(0.3f);

                // Run solver and wait for completion
                bool waiting = true;
                solverAgent.OnSolveComplete = _ => { waiting = false; };
                solverAgent.StartSolve();
                while (waiting) yield return null;

                // Wait for agent to finish walking through the door
                yield return new WaitForSeconds(exitDoorWait);

                // ── Collect puzzle structure data ──
                bool solved = solverAgent.WasSolved;
                int gw = gridVisualizer.desiredWidth;
                int gh = gridVisualizer.desiredHeight;
                int solObjs = gridVisualizer.LastSolutionObjectCount;
                int mirrorCount = gridVisualizer.LastMirrorCount;
                int refractorCount = gridVisualizer.LastRefractorCount;
                int ruleTransitions = gridVisualizer.LastRuleTransitions;

                // ── MID Calculation (paper Section 3.4.3) ────────────
                //
                // Cl = Component Density
                //      สัดส่วนของ object ทั้งหมดบน grid (รวม Decoys) เทียบกับพื้นที่ grid
                //      = N_totalObjects / 256  ตามรายงาน Section 3.4.3
                //      ใช้ TotalObjects (SolutionObjects + Decoys) เพื่อ reflect
                //      visual density ที่ผู้เล่นมองเห็นจริง ไม่ใช่แค่ solution path
                int totalObjs = gridVisualizer.LastTotalObjectCount;
                float Cl = (float)totalObjs / (gw * gh);

                // Ch = Rule Heterogeneity
                //      Fraction of consecutive bend pairs that switch mechanic type.
                //      0 = all same type, 1 = every bend alternates Mirror↔Refractor.
                //      Guard against div-by-zero when solObjs <= 1.
                float Ch = solObjs > 1
                    ? (float)ruleTransitions / (solObjs - 1)
                    : 0f;

                // Ci = Interaction Density
                //      Fraction of bend objects that are Refractors.
                //      Refractors have higher cognitive load than Mirrors because
                //      their deflection depends on chirality and a 3-layer physics check.
                //      0 = all Mirrors, 1 = all Refractors.
                //      Guard against div-by-zero when solObjs = 0.
                float Ci = solObjs > 0
                    ? (float)refractorCount / solObjs
                    : 0f;

                // Composite MID = α·Cl + β·Ch + γ·Ci
                float mid = ALPHA * Cl + BETA * Ch + GAMMA * Ci;

                records.Add(new RunRecord
                {
                    // Identification
                    run = currentRun,
                    level = solvedInSession + 1,

                    // Puzzle structure
                    steps = currentSteps,
                    solObjs = solObjs,
                    mirrorCount = mirrorCount,
                    refractorCount = refractorCount,
                    ruleTransitions = ruleTransitions,
                    decoys = gridVisualizer.LastDecoyCount,
                    totalObjs = totalObjs,   // = SolutionObjects + Decoys (ใช้ตัวแปรที่ declare ไว้แล้วข้างบน)
                    gridWidth = gw,
                    gridHeight = gh,

                    // MID components
                    Cl = Cl,
                    Ch = Ch,
                    Ci = Ci,
                    MID = mid,

                    // Solver outcome
                    solved = solved,
                    solvePhase = solverAgent.SolvePhase,

                    // Search performance
                    searchNodes = solverAgent.SolveIterations,
                    searchTimeMs = solverAgent.SearchTimeMs,

                    // Execution performance
                    totalPlacements = solverAgent.TotalPlacements,
                    inPlaceRotations = solverAgent.InPlaceRotations,
                    relocations = solverAgent.Relocations,
                    execTimeMs = solverAgent.ExecutionTimeMs,

                    // Sweep-stage effort
                    sweepIterations = solverAgent.SweepIterations,
                    sweepRelocations = solverAgent.SweepRelocations,

                    // Overall timing
                    solveTimeMs = solverAgent.SolveTimeMs,
                    genMs = gMs
                });

                // Update difficulty on success (legacy adaptive mode only —
                // stratified mode already set steps/decoys deterministically above)
                if (solved)
                {
                    solvedInSession++;
                    if (!useStratifiedSampling)
                    {
                        if (currentSteps < maxSteps) currentSteps++;
                        currentDecoys = Mathf.Min(startDecoys + solvedInSession / decoyEveryN, 4);
                        Debug.Log($"[Batch] Solved! Next steps={currentSteps} decoys={currentDecoys}");
                    }
                }

                // Progress log every 10 runs
                if (currentRun % 10 == 0)
                {
                    int s = 0;
                    foreach (var r in records) if (r.solved) s++;
                    Debug.Log($"[Batch] Progress {currentRun}/{totalRuns} | " +
                              $"SolveRate={s * 100f / records.Count:F1}% | " +
                              $"Steps={currentSteps} Decoys={currentDecoys}");
                }

                yield return new WaitForSeconds(0.1f);
            }

            ExportCSV();
        }

        // ════════════════════════════════════════════════════════════
        void ExportCSV()
        {
            var sb = new StringBuilder();

            // ── Header ────────────────────────────────────────────────
            sb.AppendLine(
                "Run,Level," +
                "Steps,SolutionObjects,Mirrors,Refractors,RuleTransitions," +
                "Decoys,TotalObjects,GridWidth,GridHeight," +
                "Cl,Ch,Ci,MID," +
                "Solved,SolvePhase," +
                "SearchNodes,SearchTimeMs," +
                "TotalPlacements,InPlaceRotations,Relocations,ExecutionTimeMs," +
                "SweepIterations,SweepRelocations," +
                "SolveTimeMs,GenerationTimeMs");

            // ── Rows ──────────────────────────────────────────────────
            foreach (var r in records)
                sb.AppendLine(
                    $"{r.run},{r.level}," +
                    $"{r.steps},{r.solObjs},{r.mirrorCount},{r.refractorCount},{r.ruleTransitions}," +
                    $"{r.decoys},{r.totalObjs},{r.gridWidth},{r.gridHeight}," +
                    $"{r.Cl:F4},{r.Ch:F4},{r.Ci:F4},{r.MID:F4}," +
                    $"{(r.solved ? 1 : 0)},{r.solvePhase}," +
                    $"{r.searchNodes},{r.searchTimeMs:F2}," +
                    $"{r.totalPlacements},{r.inPlaceRotations},{r.relocations},{r.execTimeMs:F2}," +
                    $"{r.sweepIterations},{r.sweepRelocations}," +
                    $"{r.solveTimeMs:F2},{r.genMs:F2}");

            // ── Save ──────────────────────────────────────────────────
            string path = Path.Combine(Application.dataPath, csvFileName);
            File.WriteAllText(path, sb.ToString());

            // ── Summary log ───────────────────────────────────────────
            int solved2 = 0;
            int p1a = 0, p1b = 0, sweepS1 = 0, sweepS2 = 0, sweepS3 = 0, sweepFailed = 0,
                trivial = 0, none = 0;
            float sumCl = 0f, sumCh = 0f, sumCi = 0f, sumMID = 0f;

            foreach (var r in records)
            {
                if (r.solved) solved2++;
                switch (r.solvePhase)
                {
                    case "1A": p1a++; break;
                    case "1B": p1b++; break;
                    case "Sweep-S1": sweepS1++; break;
                    case "Sweep-S2": sweepS2++; break;
                    case "Sweep-S3": sweepS3++; break;
                    case "Sweep": sweepFailed++; break; // entered sweep but never solved
                    case "Trivial": trivial++; break;
                    default: none++; break;
                }
                sumCl += r.Cl;
                sumCh += r.Ch;
                sumCi += r.Ci;
                sumMID += r.MID;
            }

            int n = records.Count;
            Debug.Log(
                $"[Batch] ══ COMPLETE ══ {solved2}/{n} solved\n" +
                $"  Phase breakdown — Trivial:{trivial} 1A:{p1a} 1B:{p1b} " +
                $"Sweep-S1:{sweepS1} Sweep-S2:{sweepS2} Sweep-S3:{sweepS3} " +
                $"SweepFailed:{sweepFailed} Failed:{none}\n" +
                $"  MID averages — " +
                $"Cl={sumCl / n:F4}  Ch={sumCh / n:F4}  Ci={sumCi / n:F4}  MID={sumMID / n:F4}\n" +
                $"  CSV saved to: {path}");
        }
    }
}