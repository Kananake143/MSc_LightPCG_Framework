using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightPCG.Core;
using LightPCG.Systems;

namespace LightPCG.Research
{
    /// <summary>
    /// Batch Runner — runs N puzzle instances automatically.
    ///
    /// Attach to a GameObject in the scene.
    /// Wire up: gridVisualizer, solverAgent references in Inspector.
    ///
    /// Each run:
    ///   1. Generate new level (GridVisualizer.GenerateLevel)
    ///   2. Start solver (AISolverAgent.StartSolve)
    ///   3. Wait for OnSolveComplete callback
    ///   4. Log all research data to CSV
    ///   5. Repeat until N runs done
    ///
    /// CSV columns (aligns with research methodology):
    ///   Run, Solved, Steps, SolutionObjects, Decoys, TotalObjects,
    ///   Iterations, Placements, SolveTimeMs, GenerationTimeMs
    /// </summary>
    public class BatchRunner : MonoBehaviour
    {
        [Header("Experiment Settings")]
        public int totalRuns = 1000;
        public bool runOnStart = true;

        [Header("References")]
        public GridVisualizer  gridVisualizer;
        public AISolverAgent   solverAgent;

        [Header("CSV Export")]
        public string csvFileName = "PCG_Results.csv";

        // ── Internal ─────────────────────────────────────────────────
        private List<RunRecord> records = new List<RunRecord>();
        private int currentRun = 0;
        private bool waiting   = false;

        struct RunRecord
        {
            public int   run;
            public bool  solved;
            public int   steps;
            public int   solutionObjects;
            public int   decoys;
            public int   totalObjects;
            public int   iterations;
            public int   placements;
            public float solveTimeMs;
            public float generationTimeMs;
        }

        void Start()
        {
            if(gridVisualizer==null)gridVisualizer=FindFirstObjectByType<GridVisualizer>();
            if(solverAgent==null)  solverAgent   =FindFirstObjectByType<AISolverAgent>();

            if(runOnStart) StartCoroutine(RunBatch());
        }

        // ── Public API so you can call from a button too ──────────────
        public void StartBatch() => StartCoroutine(RunBatch());

        IEnumerator RunBatch()
        {
            Debug.Log($"[BatchRunner] Starting {totalRuns} runs...");
            records.Clear();
            currentRun = 0;

            for(int i=0; i<totalRuns; i++)
            {
                currentRun = i+1;
                Debug.Log($"[BatchRunner] Run {currentRun}/{totalRuns}");

                // ── Generate level ────────────────────────────────────
                float genStart = Time.realtimeSinceStartup;
                gridVisualizer.GenerateLevel();
                float genTimeMs = (Time.realtimeSinceStartup - genStart) * 1000f;

                // Wait one frame for visuals/physics to settle
                yield return new WaitForEndOfFrame();
                yield return new WaitForSeconds(0.3f);

                // ── Solve ─────────────────────────────────────────────
                waiting = true;
                solverAgent.OnSolveComplete = (solved) => { waiting = false; };
                solverAgent.StartSolve();

                // Wait until solver finishes
                while(waiting) yield return null;

                // ── Record ────────────────────────────────────────────
                records.Add(new RunRecord
                {
                    run              = currentRun,
                    solved           = solverAgent.WasSolved,
                    steps            = gridVisualizer.LastSteps,
                    solutionObjects  = gridVisualizer.LastSolutionObjectCount,
                    decoys           = gridVisualizer.LastDecoyCount,
                    totalObjects     = gridVisualizer.LastTotalObjectCount,
                    iterations       = solverAgent.SolveIterations,
                    placements       = solverAgent.TotalPlacements,
                    solveTimeMs      = solverAgent.SolveTimeMs,
                    generationTimeMs = genTimeMs
                });

                // Progress log every 50 runs
                if(currentRun % 50 == 0) LogProgress();

                // Short pause between runs
                yield return new WaitForSeconds(0.1f);
            }

            ExportCSV();
            LogSummary();
        }

        void LogProgress()
        {
            int solved = 0;
            foreach(var r in records) if(r.solved) solved++;
            float rate = solved/(float)records.Count*100f;
            Debug.Log($"[BatchRunner] Progress {records.Count}/{totalRuns} — " +
                      $"SolveRate={rate:F1}%");
        }

        void LogSummary()
        {
            int solved=0; float totalSolveMs=0; int totalPlacements=0;
            foreach(var r in records)
            { if(r.solved)solved++;
              totalSolveMs+=r.solveTimeMs;
              totalPlacements+=r.placements; }

            float rate=solved/(float)records.Count*100f;
            Debug.Log($"[BatchRunner] ═══ EXPERIMENT COMPLETE ═══");
            Debug.Log($"  Total runs      : {records.Count}");
            Debug.Log($"  Solved          : {solved} ({rate:F1}%)");
            Debug.Log($"  Avg solve time  : {totalSolveMs/records.Count:F1} ms");
            Debug.Log($"  Avg placements  : {totalPlacements/(float)records.Count:F1}");
            Debug.Log($"  CSV saved to    : {GetCsvPath()}");
        }

        void ExportCSV()
        {
            var sb = new StringBuilder();
            // Header
            sb.AppendLine("Run,Solved,Steps,SolutionObjects,Decoys,TotalObjects," +
                          "Iterations,Placements,SolveTimeMs,GenerationTimeMs");
            // Data rows
            foreach(var r in records)
                sb.AppendLine($"{r.run},{(r.solved?1:0)},{r.steps}," +
                              $"{r.solutionObjects},{r.decoys},{r.totalObjects}," +
                              $"{r.iterations},{r.placements}," +
                              $"{r.solveTimeMs:F2},{r.generationTimeMs:F2}");

            string path = GetCsvPath();
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[BatchRunner] CSV exported: {path}");
        }

        string GetCsvPath()
        {
            // Saved to Unity project's Assets folder (or persistent data path in builds)
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, csvFileName);
#else
            return Path.Combine(Application.persistentDataPath, csvFileName);
#endif
        }
    }
}
