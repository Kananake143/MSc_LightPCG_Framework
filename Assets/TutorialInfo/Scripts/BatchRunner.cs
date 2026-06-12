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
    /// </summary>
    public class BatchRunner : MonoBehaviour
    {
        [Header("Experiment")]
        public int totalRuns = 50;   // change to 1000 for full experiment
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
        [Tooltip("Seconds to wait after OnSolveComplete before generating the next level.")]
        public float exitDoorWait = 5.0f;

        // ── Private state ─────────────────────────────────────────
        private List<RunRecord> records = new List<RunRecord>();
        private int currentRun = 0;
        private int currentSteps;
        private int currentDecoys;
        private int solvedInSession = 0;

        // ── Full record layout (synced with AISolverAgent fields) ─
        struct RunRecord
        {
            // Identification
            public int run, level;

            // Puzzle structure (from PCG generator)
            public int steps, solObjs, decoys, totalObjs, gridWidth, gridHeight;
            public float mid;

            // Solver outcome
            public bool solved;
            public string solvePhase;

            // Search performance (logical only, no physics wait)
            public int searchNodes;
            public float searchTimeMs;

            // Execution performance (physical movement)
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
            solvedInSession = 0;

            for (int i = 0; i < totalRuns; i++)
            {
                currentRun = i + 1;

                // Apply difficulty
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

                // ── Collect all metrics ──
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

                // Update difficulty on success
                if (solved)
                {
                    solvedInSession++;
                    if (currentSteps < maxSteps) currentSteps++;
                    currentDecoys = Mathf.Min(startDecoys + solvedInSession / decoyEveryN, 4);
                    Debug.Log($"[Batch] Solved! Next steps={currentSteps} decoys={currentDecoys}");
                }

                // Progress log every 10 runs
                if (currentRun % 10 == 0)
                {
                    int s = 0; foreach (var r in records) if (r.solved) s++;
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

            // Header
            sb.AppendLine(
                "Run,Level," +
                "Steps,SolutionObjects,Decoys,TotalObjects,GridWidth,GridHeight,MID," +
                "Solved,SolvePhase," +
                "SearchNodes,SearchTimeMs," +
                "TotalPlacements,InPlaceRotations,Relocations,ExecutionTimeMs," +
                "SolveTimeMs,GenerationTimeMs");

            foreach (var r in records)
                sb.AppendLine(
                    $"{r.run},{r.level}," +
                    $"{r.steps},{r.solObjs},{r.decoys},{r.totalObjs}," +
                    $"{r.gridWidth},{r.gridHeight},{r.mid:F4}," +
                    $"{(r.solved ? 1 : 0)},{r.solvePhase}," +
                    $"{r.searchNodes},{r.searchTimeMs:F2}," +
                    $"{r.totalPlacements},{r.inPlaceRotations},{r.relocations},{r.execTimeMs:F2}," +
                    $"{r.solveTimeMs:F2},{r.genMs:F2}");

            // Save to Assets folder
            string path = Path.Combine(Application.dataPath, csvFileName);
            File.WriteAllText(path, sb.ToString());

            // Summary log
            int solved2 = 0; foreach (var r in records) if (r.solved) solved2++;
            int p1a = 0, p1b = 0, sweep = 0, none = 0, trivial = 0;
            foreach (var r in records)
            {
                switch (r.solvePhase)
                {
                    case "1A": p1a++; break;
                    case "1B": p1b++; break;
                    case "Sweep": sweep++; break;
                    case "Trivial": trivial++; break;
                    default: none++; break;
                }
            }

            Debug.Log($"[Batch] ══ COMPLETE ══ {solved2}/{records.Count} solved\n" +
                      $"  Phase breakdown — Trivial:{trivial} 1A:{p1a} 1B:{p1b} " +
                      $"Sweep:{sweep} Failed:{none}\n" +
                      $"  CSV saved to: {path}");
        }
    }
}