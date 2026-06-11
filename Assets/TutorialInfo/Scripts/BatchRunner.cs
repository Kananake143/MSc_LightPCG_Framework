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
    ///
    /// CSV columns (grouped by concern):
    ///
    ///   Identification
    ///     Run, Level
    ///
    ///   Puzzle structure (from PCG generator)
    ///     Steps, SolutionObjects, Decoys, TotalObjects, GridWidth, GridHeight, MID
    ///
    ///   Solver outcome
    ///     Solved, SolvePhase
    ///
    ///   Search performance (logical, no physics wait)
    ///     SearchNodes, SearchTimeMs
    ///
    ///   Execution performance (physical movement)
    ///     TotalPlacements, InPlaceRotations, Relocations, ExecutionTimeMs
    ///
    ///   Overall timing
    ///     SolveTimeMs, GenerationTimeMs
    /// </summary>
    public class BatchRunner : MonoBehaviour
    {
        [Header("Experiment")]
        public int totalRuns = 1000;
        public bool runOnStart = true;

        [Header("Progressive Difficulty")]
        public int startSteps = 2;
        public int maxSteps = 9;
        public int startDecoys = 0;
        public int decoyEveryN = 3;

        [Header("References")]
        public GridVisualizer gridVisualizer;
        public AISolverAgent solverAgent;

        [Header("CSV Export")]
        public string csvFileName = "PCG_Results.csv";

        [Header("Timing")]
        [Tooltip("Seconds to wait after OnSolveComplete before generating next level.")]
        public float exitDoorWait = 5.0f;

        // ── Private state ─────────────────────────────────────────
        private List<RunRecord> records = new List<RunRecord>();
        private int currentRun = 0;
        private int currentSteps;
        private int currentDecoys;
        private int solvedInSession = 0;

        // ── Record layout ─────────────────────────────────────────
        struct RunRecord
        {
            // Identification
            public int run, level;

            // Puzzle structure
            public int steps, solObjs, decoys, totalObjs, gridWidth, gridHeight;
            public float mid;

            // Solver outcome
            public bool solved;
            public string solvePhase;

            // Search performance
            public int searchNodes;
            public float searchTimeMs;

            // Execution performance
            public int totalPlacements, inPlaceRotations, relocations;
            public float execTimeMs;

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
        IEnumerator RunBatch()
        {
            Debug.Log($"[Batch] Starting {totalRuns} runs | initialSteps={startSteps}");
            records.Clear();
            currentRun = 0;

            for (int i = 0; i < totalRuns; i++)
            {
                currentRun = i + 1;

                // Apply difficulty to generator
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

                // Run solver
                bool waiting = true;
                solverAgent.OnSolveComplete = _ => { waiting = false; };
                solverAgent.StartSolve();
                while (waiting) yield return null;

                // Wait for agent to finish walking out the door
                yield return new WaitForSeconds(exitDoorWait);

                // Collect all metrics
                bool solved = solverAgent.WasSolved;
                int gw = gridVisualizer.desiredWidth;
                int gh = gridVisualizer.desiredHeight;
                float mid = (float)gridVisualizer.LastSolutionObjectCount / (gw * gh);

                records.Add(new RunRecord
                {
                    // Identification
                    run = currentRun,
                    level = solvedInSession + 1,

                    // Puzzle structure
                    steps = currentSteps,
                    solObjs = gridVisualizer.LastSolutionObjectCount,
                    decoys = gridVisualizer.LastDecoyCount,
                    totalObjs = gridVisualizer.LastTotalObjectCount,
                    gridWidth = gw,
                    gridHeight = gh,
                    mid = mid,

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

                    // Overall timing
                    solveTimeMs = solverAgent.SolveTimeMs,
                    genMs = gMs
                });

                if (solved)
                {
                    solvedInSession++;
                    if (currentSteps < maxSteps) currentSteps++;
                    currentDecoys = Mathf.Min(startDecoys + solvedInSession / decoyEveryN, 4);
                    Debug.Log($"[Batch] Solved! Next steps={currentSteps} decoys={currentDecoys}");
                }

                // Periodic progress log every 50 runs
                if (currentRun % 50 == 0)
                {
                    int s = 0; foreach (var r in records) if (r.solved) s++;
                    Debug.Log($"[Batch] {currentRun}/{totalRuns} " +
                              $"Rate={s * 100f / records.Count:F1}% Steps={currentSteps}");
                }

                yield return new WaitForSeconds(0.1f);
            }

            ExportCSV();
        }

        // ════════════════════════════════════════════════════════════
        void ExportCSV()
        {
            var sb = new StringBuilder();

            // Header — grouped by concern for readability in spreadsheet tools
            sb.AppendLine(
                // Identification
                "Run,Level," +
                // Puzzle structure
                "Steps,SolutionObjects,Decoys,TotalObjects,GridWidth,GridHeight,MID," +
                // Solver outcome
                "Solved,SolvePhase," +
                // Search performance (logical, no physics wait)
                "SearchNodes,SearchTimeMs," +
                // Execution performance (physical movement)
                "TotalPlacements,InPlaceRotations,Relocations,ExecutionTimeMs," +
                // Overall timing
                "SolveTimeMs,GenerationTimeMs");

            foreach (var r in records)
                sb.AppendLine(
                    // Identification
                    $"{r.run},{r.level}," +
                    // Puzzle structure
                    $"{r.steps},{r.solObjs},{r.decoys},{r.totalObjs}," +
                    $"{r.gridWidth},{r.gridHeight},{r.mid:F4}," +
                    // Solver outcome
                    $"{(r.solved ? 1 : 0)},{r.solvePhase}," +
                    // Search performance
                    $"{r.searchNodes},{r.searchTimeMs:F2}," +
                    // Execution performance
                    $"{r.totalPlacements},{r.inPlaceRotations},{r.relocations},{r.execTimeMs:F2}," +
                    // Overall timing
                    $"{r.solveTimeMs:F2},{r.genMs:F2}");

            string path = Path.Combine(Application.dataPath, csvFileName);
            File.WriteAllText(path, sb.ToString());

            int solved2 = 0; foreach (var r in records) if (r.solved) solved2++;

            // Summary by solve phase
            int phase1A = 0, phase1B = 0, sweep = 0, none = 0;
            foreach (var r in records)
            {
                if (r.solvePhase == "1A") phase1A++;
                else if (r.solvePhase == "1B") phase1B++;
                else if (r.solvePhase == "Sweep") sweep++;
                else none++;
            }

            Debug.Log($"[Batch] COMPLETE {solved2}/{records.Count} solved | " +
                      $"Phase1A={phase1A} Phase1B={phase1B} Sweep={sweep} Failed={none} | " +
                      $"CSV: {path}");
        }
    }
}